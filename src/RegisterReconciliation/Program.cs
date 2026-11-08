using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using RegisterReconciliation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RequestErrors>();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("RegisterReconciliation")
    ?? throw new InvalidOperationException("Set ConnectionStrings__RegisterReconciliation.")));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<Imports>();
builder.Services.AddSingleton<Runs>();
builder.Services.AddHealthChecks().AddCheck<PostgresHealth>("postgres");
builder.Services.AddAuthentication(DemoApiKey.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoApiKey>(DemoApiKey.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("operate", policy => policy.RequireRole("operator"))
    .AddPolicy("review", policy => policy.RequireRole("reviewer"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("registry-work", limiter =>
    {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
    });
    options.OnRejected = async (rejection, _) =>
        await Results.Problem("Сервис занят обработкой реестров. Повторите запрос позже.", statusCode: 429)
            .ExecuteAsync(rejection.HttpContext);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

var data = app.MapGroup("").RequireAuthorization();

data.MapPost("/imports/{source}", async (string source, HttpContext context, Imports imports, ILogger<Imports> log,
    CancellationToken ct) =>
{
    if (source is not ("internal" or "external"))
        return Results.Problem("Источник должен быть internal или external.", statusCode: 400);
    if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "text/csv", StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Ожидается Content-Type: text/csv.", statusCode: 415);

    var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false })
        limit.MaxRequestBodySize = Imports.MaxBytes;
    if (context.Request.ContentLength > Imports.MaxBytes)
        return Results.Problem("CSV exceeds 32 MiB.", statusCode: 413);

    var receipt = await imports.ImportAsync(source, context.Request.Body, ct);
    log.LogInformation("Import {BatchId} from {Source}: {Rows} rows, {Invalid} invalid, reused={Reused}",
        receipt.Batch.Id, source, receipt.Batch.RowCount, receipt.Batch.InvalidCount, receipt.Reused);
    return receipt.Reused ? Results.Ok(receipt) : Results.Created($"/imports/{receipt.Batch.Id}", receipt);
}).RequireAuthorization("operate").RequireRateLimiting("registry-work")
    .Accepts<Stream>("text/csv").Produces<ImportReceipt>(201).Produces<ImportReceipt>();

data.MapGet("/imports/{id:long}", async (long id, Imports imports, CancellationToken ct) =>
    await imports.GetAsync(id, ct) is { } batch ? Results.Ok(batch) : Results.NotFound())
    .Produces<ImportBatch>();

data.MapGet("/imports/{id:long}/errors", async (long id, int? after, int? limit, Imports imports, CancellationToken ct) =>
{
    if (!ValidPage(after, limit))
        return InvalidPage();
    if (await imports.GetAsync(id, ct) is null)
        return Results.NotFound();

    var size = limit ?? 50;
    var rows = await imports.ErrorsAsync(id, after ?? 0, size + 1, ct);
    return Results.Ok(Page(rows, size, row => row.Line));
}).Produces<PageResponse<ImportError>>();

data.MapPost("/runs", async (StartRun request, Runs runs, ILogger<Runs> log, CancellationToken ct) =>
{
    var run = await runs.StartAsync(request.InternalBatchId, request.ExternalBatchId, ct);
    log.LogInformation("Run {RunId} finished for batches {InternalBatchId} and {ExternalBatchId}",
        run.Id, run.InternalBatchId, run.ExternalBatchId);
    return Results.Created($"/runs/{run.Id}", run);
}).RequireAuthorization("operate").RequireRateLimiting("registry-work").Produces<ReconciliationRun>(201);

data.MapGet("/runs/{id:long}", async (long id, Runs runs, CancellationToken ct) =>
    await runs.GetAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound())
    .Produces<ReconciliationRun>();

data.MapGet("/runs/{id:long}/results", async (long id, int? after, int? limit, Runs runs, CancellationToken ct) =>
{
    if (!ValidPage(after, limit))
        return InvalidPage();
    if (!await runs.ExistsAsync(id, ct))
        return Results.NotFound();

    var size = limit ?? 50;
    var rows = await runs.ReadResultsAsync(id, after ?? 0, size + 1, ct).ToListAsync(ct);
    return Results.Ok(Page(rows, size, row => row.Position));
}).Produces<PageResponse<RunResult>>();

data.MapGet("/runs/{id:long}/export", async (long id, Runs runs, CancellationToken ct) =>
{
    if (!await runs.ExistsAsync(id, ct))
        return Results.NotFound();

    return Results.Stream(async output =>
    {
        await using var csv = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
        await csv.WriteLineAsync(Export.Header.AsMemory(), ct);
        await foreach (var row in runs.ReadResultsAsync(id, 0, int.MaxValue, ct))
            await csv.WriteLineAsync(Export.Line(row).AsMemory(), ct);
        await csv.FlushAsync(ct);
    }, "text/csv; charset=utf-8", $"run-{id}.csv");
}).RequireRateLimiting("registry-work").Produces(200, contentType: "text/csv");

data.MapPost("/runs/{id:long}/results/{position:int}/resolution", async (long id, int position,
    ResolveResult request, HttpContext context, Runs runs, ILogger<Runs> log, CancellationToken ct) =>
{
    var comment = request.Comment?.Trim();
    if (comment is null or { Length: 0 or > 1000 } || comment.Contains('\0'))
        return Results.Problem("Комментарий должен содержать от 1 до 1000 символов и не содержать NUL.", statusCode: 400);

    var resolution = await runs.ResolveAsync(id, position, context.User.Identity!.Name!, comment, ct);
    if (resolution is null)
        return Results.NotFound();

    log.LogInformation("Reviewer {Reviewer} resolved result {Position} of run {RunId}", resolution.Author, position, id);
    return Results.Ok(resolution);
}).RequireAuthorization("review").Produces<Resolution>();

await app.Services.GetRequiredService<Database>().MigrateAsync(app.Lifetime.ApplicationStopping);
await app.RunAsync();

static bool ValidPage(int? after, int? limit) => after is not < 0 && limit is not (< 1 or > 200);

static IResult InvalidPage() => Results.Problem("Нужны after >= 0 и limit от 1 до 200.", statusCode: 400);

static PageResponse<T> Page<T>(List<T> rows, int size, Func<T, int> cursor)
{
    if (rows.Count <= size)
        return new PageResponse<T>(rows, null);
    rows.RemoveAt(size);
    return new PageResponse<T>(rows, cursor(rows[^1]));
}

public sealed record StartRun(long InternalBatchId, long ExternalBatchId);
public sealed record ResolveResult(string? Comment);
public sealed record PageResponse<T>(List<T> Items, int? NextAfter);

public partial class Program;
