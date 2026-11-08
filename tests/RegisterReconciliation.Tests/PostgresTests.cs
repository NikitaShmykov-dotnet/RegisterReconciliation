using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace RegisterReconciliation.Tests;

public sealed class PostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture fixture;
    private static CancellationToken Ct => CancellationToken.None;

    public PostgresTests(PostgresFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task ConcurrentIdenticalImportsPublishOneBatchPerSource()
    {
        await using var db = await fixture.CreateSchemaAsync();
        var imports = new Imports(db.Database);
        var content = await DemoAsync("internal");
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ImportAsync(imports, "internal", content)));
        var batch = Assert.Single(receipts, receipt => !receipt.Reused).Batch;

        Assert.All(receipts, receipt => Assert.Equal(batch, receipt.Batch));
        Assert.Equal(7L, await db.CountAsync("imported_rows"));

        var other = await ImportAsync(imports, "external", content);
        Assert.False(other.Reused);
        Assert.NotEqual(batch.Id, other.Batch.Id);
        Assert.Equal(14L, await db.CountAsync("imported_rows"));
    }

    [Fact]
    public async Task InvalidRowsAreRetainedAndBlockRun()
    {
        await using var db = await fixture.CreateSchemaAsync();
        var imports = new Imports(db.Database);
        var left = await ImportAsync(imports, "internal", await DemoAsync("invalid"));
        var right = await ImportAsync(imports, "external", await DemoAsync("external"));
        var errors = await imports.ErrorsAsync(left.Batch.Id, 0, 10, Ct);

        Assert.Equal(2, left.Batch.InvalidCount);
        Assert.Collection(errors, error =>
        {
            Assert.Equal(2, error.Line);
            Assert.Equal("bad id,19.999,usd,2026-01-15T10:00:00,", error.Raw);
            Assert.Contains("external_id, amount, currency, occurred_at, order_reference", error.Error);
        }, error => Assert.Equal(3, error.Line));
        await Assert.ThrowsAsync<RunInputException>(() => new Runs(db.Database, imports).StartAsync(left.Batch.Id, right.Batch.Id, Ct));
        Assert.Equal(0L, await db.CountAsync("reconciliation_runs"));
    }

    [Fact]
    public async Task DemoIsDeterministicAndConcurrentResolutionKeepsFirstDecision()
    {
        await using var db = await fixture.CreateSchemaAsync();
        var imports = new Imports(db.Database);
        var runs = new Runs(db.Database, imports);
        var left = await ImportAsync(imports, "internal", await DemoAsync("internal"));
        var right = await ImportAsync(imports, "external", await DemoAsync("external"));

        var first = await runs.StartAsync(left.Batch.Id, right.Batch.Id, Ct);
        var second = await runs.StartAsync(left.Batch.Id, right.Batch.Id, Ct);
        var results = await runs.ReadResultsAsync(first.Id, 0, 100, Ct).ToListAsync(Ct);
        var loaded = await runs.GetAsync(first.Id, Ct);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, first.AlgorithmVersion);
        Assert.Equal(first.Counts.OrderBy(pair => pair.Key), loaded!.Counts.OrderBy(pair => pair.Key));
        Assert.Equal(2, first.Counts["matched"]);
        Assert.Equal(1, first.Counts["amount-mismatch"]);
        Assert.Equal(1, first.Counts["missing-external"]);
        Assert.Equal(1, first.Counts["missing-internal"]);
        Assert.Equal(3, first.Counts["duplicate"]);
        Assert.Equal(3, first.Counts["ambiguous"]);
        Assert.Equal(results, await runs.ReadResultsAsync(second.Id, 0, 100, Ct).ToListAsync(Ct));
        Assert.Equal(results.Skip(3).Take(3), await runs.ReadResultsAsync(first.Id, results[2].Position, 3, Ct).ToListAsync(Ct));

        var target = Assert.Single(results, result => result is { Category: "ambiguous", Internal: not null });
        var attempts = await Task.WhenAll(ResolveAsync("olga"), ResolveAsync("ivan"));
        var decision = Assert.Single(attempts.OfType<Resolution>());
        var saved = Assert.Single(await runs.ReadResultsAsync(first.Id, target.Position - 1, 1, Ct).ToListAsync(Ct));
        Assert.Equal(target with { Resolution = decision }, saved);
        Assert.Equal("Провайдер подтвердил повтор выгрузки", decision.Comment);
        Assert.Equal(DateTimeKind.Utc, decision.ResolvedAt.Kind);
        Assert.Equal(14L, await db.CountAsync("imported_rows"));
        Assert.Equal(1L, await db.CountAsync("resolutions"));

        async Task<Resolution?> ResolveAsync(string author)
        {
            try
            {
                return await runs.ResolveAsync(first.Id, target.Position, author, "Провайдер подтвердил повтор выгрузки", Ct);
            }
            catch (ResolutionConflictException)
            {
                return null;
            }
        }
    }

    [Fact]
    public async Task FailedPublishRollsBackBatchAndAllowsRetry()
    {
        await using var db = await fixture.CreateSchemaAsync();
        var imports = new Imports(db.Database);
        var content = await DemoAsync("internal");
        await db.ExecuteAsync("alter table imported_rows add constraint injected_failure check (line <> 3)");

        var failure = await Assert.ThrowsAsync<PostgresException>(() => ImportAsync(imports, "internal", content));
        Assert.Equal("injected_failure", failure.ConstraintName);
        Assert.Equal(0L, await db.CountAsync("import_batches"));
        Assert.Equal(0L, await db.CountAsync("imported_rows"));

        await db.ExecuteAsync("alter table imported_rows drop constraint injected_failure");
        var receipt = await ImportAsync(imports, "internal", content);
        Assert.False(receipt.Reused);
        Assert.Equal(7L, await db.CountAsync("imported_rows"));
    }

    [Fact]
    public async Task FailedOutcomeCopyRollsBackRunAndAllowsRetry()
    {
        await using var db = await fixture.CreateSchemaAsync();
        var imports = new Imports(db.Database);
        var runs = new Runs(db.Database, imports);
        var left = await ImportAsync(imports, "internal", await DemoAsync("internal"));
        var right = await ImportAsync(imports, "external", await DemoAsync("external"));
        await db.ExecuteAsync("alter table outcomes add constraint injected_failure check (position <> 2)");

        var failure = await Assert.ThrowsAsync<PostgresException>(() => runs.StartAsync(left.Batch.Id, right.Batch.Id, Ct));
        Assert.Equal("injected_failure", failure.ConstraintName);
        Assert.Equal(0L, await db.CountAsync("reconciliation_runs"));
        Assert.Equal(0L, await db.CountAsync("outcomes"));

        await db.ExecuteAsync("alter table outcomes drop constraint injected_failure");
        var run = await runs.StartAsync(left.Batch.Id, right.Batch.Id, Ct);
        Assert.Equal(11, run.Counts.Values.Sum());
    }

    private static Task<string> DemoAsync(string name) =>
        File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "demo", name + ".csv"), Ct);

    private static async Task<ImportReceipt> ImportAsync(Imports imports, string source, string content)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await imports.ImportAsync(source, body, Ct);
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private NpgsqlDataSource admin = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RECONCILE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            container = new PostgreSqlBuilder("postgres:18.6").Build();
            await container.StartAsync();
            connection = container.GetConnectionString();
        }

        admin = NpgsqlDataSource.Create(connection);
    }

    public async Task<TestSchema> CreateSchemaAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using var create = admin.CreateCommand($"create schema {name}");
        await create.ExecuteNonQueryAsync();
        var scoped = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { SearchPath = name };
        var schema = new TestSchema(admin, NpgsqlDataSource.Create(scoped.ConnectionString), name);
        try
        {
            await schema.Database.MigrateAsync(CancellationToken.None);
            return schema;
        }
        catch
        {
            await schema.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (admin is not null)
            await admin.DisposeAsync();
        if (container is not null)
            await container.DisposeAsync();
    }
}

public sealed class TestSchema(NpgsqlDataSource admin, NpgsqlDataSource scoped, string name) : IAsyncDisposable
{
    public Database Database { get; } = new(scoped);
    public string ConnectionString => scoped.ConnectionString;

    public async Task ExecuteAsync(string sql)
    {
        await using var command = scoped.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string table)
    {
        await using var command = scoped.CreateCommand($"select count(*) from {table}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        await scoped.DisposeAsync();
        await using var drop = admin.CreateCommand($"drop schema {name} cascade");
        await drop.ExecuteNonQueryAsync();
    }
}
