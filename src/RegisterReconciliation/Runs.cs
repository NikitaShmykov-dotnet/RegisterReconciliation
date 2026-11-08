using Npgsql;
using NpgsqlTypes;

namespace RegisterReconciliation;

public sealed record ReconciliationRun(long Id, long InternalBatchId, long ExternalBatchId, int AlgorithmVersion,
    DateTime StartedAt, Dictionary<string, int> Counts);

public sealed record RowReference(long Id, int Line, string? ExternalId, decimal Amount, string Currency,
    DateTime OccurredAt, string OrderReference);

public sealed record Resolution(string Author, DateTime ResolvedAt, string Comment);

public sealed record RunResult(int Position, string Category, string? Method, RowReference? Internal,
    RowReference? External, int CandidateCount, Resolution? Resolution);

public sealed class RunInputException(string message) : Exception(message);
public sealed class ResolutionConflictException(string message) : Exception(message);

public sealed class Runs(Database database, Imports imports)
{
    public async Task<ReconciliationRun> StartAsync(long internalBatchId, long externalBatchId, CancellationToken ct)
    {
        var left = await imports.GetAsync(internalBatchId, ct);
        var right = await imports.GetAsync(externalBatchId, ct);
        if (left?.Source != "internal" || right?.Source != "external")
            throw new RunInputException("Нужны существующие пакеты internal и external соответственно.");
        if (left.InvalidCount != 0 || right.InvalidCount != 0)
            throw new RunInputException("В пакетах есть ошибки. Откройте отчёт импорта и загрузите исправленные файлы.");

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = Database.Command(connection, """
            insert into reconciliation_runs (internal_batch_id, external_batch_id, algorithm_version)
            values ($1, $2, $3) returning id, started_at
            """, left.Id, right.Id, Matcher.Version);
        long id;
        DateTime startedAt;
        await using (var reader = await insert.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            id = reader.GetInt64(0);
            startedAt = reader.GetDateTime(1);
        }

        var internalRows = await ReadOperationsAsync(connection, left.Id, ct);
        var externalRows = await ReadOperationsAsync(connection, right.Id, ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            copy outcomes (run_id, position, category, method, internal_row_id, external_row_id, candidate_count)
            from stdin (format binary)
            """, ct))
        {
            var position = 0;
            foreach (var outcome in Matcher.Compare(internalRows, externalRows, ct))
            {
                counts[outcome.Category] = counts.GetValueOrDefault(outcome.Category) + 1;
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(id, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(++position, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(outcome.Category, NpgsqlDbType.Text, ct);
                if (outcome.Method is { } method)
                    await copy.WriteAsync(method, NpgsqlDbType.Text, ct);
                else
                    await copy.WriteNullAsync(ct);
                await WriteRowIdAsync(copy, outcome.Internal, ct);
                await WriteRowIdAsync(copy, outcome.External, ct);
                await copy.WriteAsync(outcome.CandidateCount, NpgsqlDbType.Integer, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new ReconciliationRun(id, left.Id, right.Id, Matcher.Version, startedAt, counts);
    }

    public async Task<ReconciliationRun?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var query = Database.Command(connection, """
            select r.internal_batch_id, r.external_batch_id, r.algorithm_version, r.started_at,
                   counts.category, counts.total
            from reconciliation_runs r
            left join lateral (
                select category, count(*)::int as total from outcomes where run_id = r.id group by category
            ) counts on true
            where r.id = $1 order by counts.category
            """, id);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var run = new ReconciliationRun(id, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
            reader.GetDateTime(3), counts);
        do
        {
            if (!reader.IsDBNull(4))
                counts.Add(reader.GetString(4), reader.GetInt32(5));
        } while (await reader.ReadAsync(ct));
        return run;
    }

    public async Task<bool> ExistsAsync(long id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var query = Database.Command(connection, "select 1 from reconciliation_runs where id = $1", id);
        return await query.ExecuteScalarAsync(ct) is not null;
    }

    public async IAsyncEnumerable<RunResult> ReadResultsAsync(long id, int after, int limit,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var query = Database.Command(connection, """
            select o.position, o.category, o.method, o.candidate_count,
                   i.id, i.line, i.external_id, i.amount, i.currency, i.occurred_at, i.order_reference,
                   e.id, e.line, e.external_id, e.amount, e.currency, e.occurred_at, e.order_reference,
                   r.author, r.resolved_at, r.comment
            from outcomes o
            left join imported_rows i on i.id = o.internal_row_id
            left join imported_rows e on e.id = o.external_row_id
            left join resolutions r using (run_id, position)
            where o.run_id = $1 and o.position > $2
            order by o.position limit $3
            """, id, after, limit);
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new RunResult(reader.GetInt32(0), reader.GetString(1), NullableText(reader, 2),
                ReadReference(reader, 4), ReadReference(reader, 11), reader.GetInt32(3),
                reader.IsDBNull(18) ? null : new Resolution(reader.GetString(18), reader.GetDateTime(19), reader.GetString(20)));
        }
    }

    public async Task<Resolution?> ResolveAsync(long id, int position, string author, string comment, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var query = Database.Command(connection, """
            insert into resolutions (run_id, position, author, comment)
            select run_id, position, $3, $4 from outcomes
            where run_id = $1 and position = $2 and category <> 'matched'
            on conflict (run_id, position) do nothing
            returning resolved_at
            """, id, position, author, comment);
        if (await query.ExecuteScalarAsync(ct) is DateTime timestamp)
            return new Resolution(author, timestamp, comment);

        await using var lookup = Database.Command(connection, """
            select category from outcomes where run_id = $1 and position = $2
            """, id, position);
        return await lookup.ExecuteScalarAsync(ct) switch
        {
            null => null,
            "matched" => throw new ResolutionConflictException("Совпадение не требует ручного решения."),
            _ => throw new ResolutionConflictException("Решение уже записано.")
        };
    }

    private static async Task<List<Operation>> ReadOperationsAsync(NpgsqlConnection connection, long id, CancellationToken ct)
    {
        await using var query = Database.Command(connection, """
            select id, external_id, amount, currency, occurred_at, order_reference
            from imported_rows where batch_id = $1 order by line
            """, id);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var rows = new List<Operation>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Operation(reader.GetInt64(0), NullableText(reader, 1), reader.GetDecimal(2),
                reader.GetString(3), reader.GetDateTime(4), reader.GetString(5)));
        }

        return rows;
    }

    private static Task WriteRowIdAsync(NpgsqlBinaryImporter copy, Operation? row, CancellationToken ct) =>
        row is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(row.Id, NpgsqlDbType.Bigint, ct);

    private static RowReference? ReadReference(NpgsqlDataReader reader, int offset) =>
        reader.IsDBNull(offset) ? null : new RowReference(reader.GetInt64(offset), reader.GetInt32(offset + 1),
            NullableText(reader, offset + 2), reader.GetDecimal(offset + 3), reader.GetString(offset + 4),
            reader.GetDateTime(offset + 5), reader.GetString(offset + 6));

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
