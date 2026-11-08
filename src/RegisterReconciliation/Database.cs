using Npgsql;

namespace RegisterReconciliation;

public sealed class Database(NpgsqlDataSource dataSource)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct) => dataSource.OpenConnectionAsync(ct);

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var initialize = Command(connection, """
            select pg_advisory_xact_lock(584032);
            create table if not exists schema_migrations (name text primary key);
            """);
        await initialize.ExecuteNonQueryAsync(ct);

        var assembly = typeof(Database).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            await using var check = Command(connection, "select 1 from schema_migrations where name = $1", name);
            if (await check.ExecuteScalarAsync(ct) is not null)
                continue;

            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var migration = Command(connection, await reader.ReadToEndAsync(ct));
            await migration.ExecuteNonQueryAsync(ct);
            await using var record = Command(connection, "insert into schema_migrations values ($1)", name);
            await record.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    internal static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
}
