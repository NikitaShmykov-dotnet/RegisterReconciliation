using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace RegisterReconciliation;

internal sealed class RequestErrors : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            BadHttpRequestException badRequest => badRequest.StatusCode,
            InvalidImportException or DecoderFallbackException => StatusCodes.Status400BadRequest,
            RunInputException => StatusCodes.Status422UnprocessableEntity,
            ResolutionConflictException => StatusCodes.Status409Conflict,
            _ => 0
        };
        if (status == 0)
            return false;

        var detail = exception is DecoderFallbackException ? "CSV должен быть в UTF-8." : exception.Message;
        await Results.Problem(detail, statusCode: status).ExecuteAsync(context);
        return true;
    }
}

internal sealed class DemoApiKey(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "DemoApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var supplied))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (supplied.Count != 1)
            return Task.FromResult(AuthenticateResult.Fail("Expected one API key."));

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString()));
        ClaimsIdentity? identity = null;
        foreach (var (section, role) in new[] { ("Operators", "operator"), ("Reviewers", "reviewer") })
        {
            foreach (var account in configuration.GetSection(section).GetChildren())
            {
                if (account.Value is not { Length: >= 16 } key ||
                    !CryptographicOperations.FixedTimeEquals(suppliedHash, SHA256.HashData(Encoding.UTF8.GetBytes(key))))
                    continue;

                if (identity is not null)
                    return Task.FromResult(AuthenticateResult.Fail("API key is assigned to multiple accounts."));

                identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, account.Key), new Claim(ClaimTypes.Role, role)],
                    SchemeName);
            }
        }

        return Task.FromResult(identity is null
            ? AuthenticateResult.Fail("Unknown API key.")
            : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

internal sealed class PostgresHealth(Database database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var ping = Database.Command(connection, "select 1");
            await ping.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}

internal static class Export
{
    internal const string Header = "position,category,method,candidate_count," +
        "internal_line,internal_external_id,internal_amount,internal_currency,internal_occurred_at,internal_order," +
        "external_line,external_external_id,external_amount,external_currency,external_occurred_at,external_order," +
        "reviewer,resolved_at,comment";

    internal static string Line(RunResult row) => string.Join(",",
        row.Position.ToString(CultureInfo.InvariantCulture), row.Category, Csv.Cell(row.Method),
        row.CandidateCount.ToString(CultureInfo.InvariantCulture), Fields(row.Internal), Fields(row.External),
        Csv.Cell(row.Resolution?.Author), row.Resolution?.ResolvedAt.ToString("O", CultureInfo.InvariantCulture),
        Csv.Cell(row.Resolution?.Comment));

    private static string Fields(RowReference? row) => row is null ? ",,,,," : string.Join(",",
        row.Line.ToString(CultureInfo.InvariantCulture), Csv.Cell(row.ExternalId),
        row.Amount.ToString("0.00", CultureInfo.InvariantCulture), Csv.Cell(row.Currency),
        row.OccurredAt.ToString("O", CultureInfo.InvariantCulture), Csv.Cell(row.OrderReference));
}
