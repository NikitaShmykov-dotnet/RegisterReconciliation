using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RegisterReconciliation.Tests;

public sealed class HttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string OperatorKey = "test-operator-key-0001";
    private const string ReviewerKey = "test-reviewer-key-0001";

    [Fact]
    public async Task DataRoutesRequireKeyAndWritesRequireTheirOwnRole()
    {
        await using var schema = await postgres.CreateSchemaAsync();
        await using var app = CreateApp(schema);
        using var guest = app.CreateClient();
        using var reviewer = CreateClient(app, ReviewerKey);
        using var operatorClient = CreateClient(app, OperatorKey);
        using var wrongKey = CreateClient(app, "invalid");

        await AssertProblemAsync(await guest.GetAsync("/runs/1"), HttpStatusCode.Unauthorized);
        await AssertProblemAsync(await guest.PostAsync("/imports/internal", CsvText("ignored")), HttpStatusCode.Unauthorized);
        await AssertProblemAsync(await wrongKey.GetAsync("/runs/1"), HttpStatusCode.Unauthorized);
        await AssertProblemAsync(await reviewer.PostAsync("/imports/internal", CsvText("ignored")), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await reviewer.PostAsJsonAsync("/runs", new StartRun(1, 2)), HttpStatusCode.Forbidden);
        await AssertProblemAsync(await operatorClient.PostAsJsonAsync("/runs/1/results/1/resolution", new ResolveResult("reason")),
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ImportChecksTypeSizeAndEncodingAndReportsBadRows()
    {
        await using var schema = await postgres.CreateSchemaAsync();
        await using var app = CreateApp(schema);
        using var client = CreateClient(app, OperatorKey);

        await AssertProblemAsync(await client.PostAsync("/imports/internal", new StringContent(Csv.Header)),
            HttpStatusCode.UnsupportedMediaType);

        using var oversized = CsvText(Csv.Header);
        oversized.Headers.ContentLength = Imports.MaxBytes + 1L;
        await AssertProblemAsync(await client.PostAsync("/imports/internal", oversized), HttpStatusCode.RequestEntityTooLarge);

        using var invalidUtf8 = new ByteArrayContent([.. Encoding.UTF8.GetBytes(Csv.Header + "\n"), 0xff]);
        invalidUtf8.Headers.ContentType = new("text/csv");
        await AssertProblemAsync(await client.PostAsync("/imports/internal", invalidUtf8), HttpStatusCode.BadRequest);
        Assert.Equal(0L, await schema.CountAsync("import_batches"));

        var batch = await ImportAsync(client, "internal", "invalid");
        Assert.Equal(2, batch.InvalidCount);
        var page = await client.GetFromJsonAsync<PageResponse<ImportError>>($"/imports/{batch.Id}/errors?limit=1");
        Assert.Equal(2, page!.NextAfter);
        Assert.Contains("bad id", Assert.Single(page.Items).Raw);
        var external = await ImportAsync(client, "external", "external");
        await AssertProblemAsync(await client.PostAsJsonAsync("/runs", new StartRun(batch.Id, external.Id)),
            HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ResultsPaginateAndCsvPreservesAuditedDecision()
    {
        await using var schema = await postgres.CreateSchemaAsync();
        await using var app = CreateApp(schema);
        using var operatorClient = CreateClient(app, OperatorKey);
        using var reviewer = CreateClient(app, ReviewerKey);
        using var guest = app.CreateClient();
        var left = await ImportAsync(operatorClient, "internal", "internal");
        var right = await ImportAsync(operatorClient, "external", "external");
        using var started = await operatorClient.PostAsJsonAsync("/runs", new StartRun(left.Id, right.Id));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<ReconciliationRun>())!;
        var first = (await reviewer.GetFromJsonAsync<PageResponse<RunResult>>($"/runs/{run.Id}/results?limit=2"))!;
        var rest = (await reviewer.GetFromJsonAsync<PageResponse<RunResult>>($"/runs/{run.Id}/results?after={first.NextAfter}"))!;
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(first.Items[^1].Position + 1, rest.Items[0].Position);
        Assert.Equal(11, first.Items.Count + rest.Items.Count);
        Assert.Null(rest.NextAfter);
        var target = Assert.Single(rest.Items, item => item is { Category: "ambiguous", Internal: not null });
        var path = $"/runs/{run.Id}/results/{target.Position}/resolution";
        var decision = new ResolveResult("=SUM(1,2)");
        await AssertProblemAsync(await guest.PostAsJsonAsync(path, decision), HttpStatusCode.Unauthorized);
        using var resolved = await reviewer.PostAsJsonAsync(path, decision);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var audit = (await resolved.Content.ReadFromJsonAsync<Resolution>())!;
        Assert.Equal("olga", audit.Author);
        Assert.Equal(decision.Comment, audit.Comment);
        Assert.NotEqual(default, audit.ResolvedAt);
        await AssertProblemAsync(await reviewer.PostAsJsonAsync(path, decision), HttpStatusCode.Conflict);

        using var export = await reviewer.GetAsync($"/runs/{run.Id}/export", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var report = await export.Content.ReadAsStringAsync();
        Assert.Contains("\"'=SUM(1,2)\"", report);
        Assert.Contains($"{target.Position},ambiguous,", report);
    }

    [Fact]
    public async Task BusyImportRejectsRunAndCancellationReturnsPermit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var schema = await postgres.CreateSchemaAsync();
        await using var app = CreateApp(schema);
        using var client = CreateClient(app, OperatorKey);
        await using var blocker = await schema.Database.OpenAsync(timeout.Token);
        await using var transaction = await blocker.BeginTransactionAsync(timeout.Token);
        await using var locked = Database.Command(blocker, "lock table import_batches in access exclusive mode");
        await locked.ExecuteNonQueryAsync(timeout.Token);

        var pending = client.PostAsync("/imports/internal", CsvText(Csv.Header + "\na,1,USD,2026-01-01T00:00:00Z,order"), request.Token);
        await using var observer = await schema.Database.OpenAsync(timeout.Token);
        await using var waiting = Database.Command(observer, """
            select exists (select 1 from pg_locks where relation = 'import_batches'::regclass and not granted)
            """);
        while (await waiting.ExecuteScalarAsync(timeout.Token) is false)
            await Task.Delay(20, timeout.Token);

        await AssertProblemAsync(await client.PostAsJsonAsync("/runs", new StartRun(1, 2), timeout.Token),
            HttpStatusCode.TooManyRequests);

        await request.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        while (true)
        {
            using var response = await client.PostAsync("/imports/internal", CsvText("bad header"), timeout.Token);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                break;
            }

            await Task.Delay(20, timeout.Token);
        }

        await transaction.RollbackAsync(timeout.Token);
        Assert.Equal(0L, await schema.CountAsync("import_batches"));
    }

    private static WebApplicationFactory<Program> CreateApp(TestSchema schema) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:RegisterReconciliation", schema.ConnectionString)
            .UseSetting("Operators:ivan", OperatorKey)
            .UseSetting("Reviewers:olga", ReviewerKey));

    private static HttpClient CreateClient(WebApplicationFactory<Program> app, string key)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static StringContent CsvText(string content) => new(content, Encoding.UTF8, "text/csv");

    private static async Task<ImportBatch> ImportAsync(HttpClient client, string source, string name)
    {
        var csv = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "demo", name + ".csv"));
        using var response = await client.PostAsync($"/imports/{source}", CsvText(csv));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportReceipt>())!.Batch;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
            Assert.Equal((int)expected, problem!.Status);
        }
    }
}
