using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace RegisterReconciliation;

public sealed record ImportBatch(long Id, string Source, DateTime ImportedAt, int RowCount, int InvalidCount);
public sealed record ImportReceipt(ImportBatch Batch, bool Reused);
public sealed record ImportError(int Line, string Raw, string Error);

public sealed class InvalidImportException(string message) : Exception(message);

public sealed class Imports(Database database)
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public const int MaxRows = 150_000;

    public async Task<ImportReceipt> ImportAsync(string source, Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int count;
        while ((count = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxBytes)
                throw new BadHttpRequestException("CSV exceeds 32 MiB.", StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, count);
        }

        var digest = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        buffer.Position = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).StartsWith("\uFEFF"u8) ? 3 : 0;
        using var csv = new StreamReader(buffer, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        if (await csv.ReadLineAsync(ct) != Csv.Header)
            throw new InvalidImportException("Неверный заголовок CSV.");

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var reserve = Database.Command(connection, """
            insert into import_batches (source, digest) values ($1, $2)
            on conflict (source, digest) do nothing returning id
            """, source, digest);

        if (await reserve.ExecuteScalarAsync(ct) is not long id)
        {
            // READ COMMITTED takes a fresh snapshot after the conflicting insert commits.
            var existing = await ReadBatchAsync(connection, "source = $1 and digest = $2", ct, source, digest);
            return new ImportReceipt(existing!, true);
        }

        await using var stage = Database.Command(connection, """
            create temporary table staged_rows on commit drop as
            select line, raw, external_id, amount, currency, occurred_at, order_reference, error
            from imported_rows with no data
            """);
        await stage.ExecuteNonQueryAsync(ct);

        var rows = 0;
        var invalid = 0;
        await using (var copy = await connection.BeginBinaryImportAsync("""
            copy staged_rows (line, raw, external_id, amount, currency, occurred_at, order_reference, error)
            from stdin (format binary)
            """, ct))
        {
            while (await csv.ReadLineAsync(ct) is { } raw)
            {
                if (++rows > MaxRows)
                    throw new InvalidImportException($"Максимум {MaxRows} строк на файл.");
                if (raw.Contains('\0'))
                    throw new InvalidImportException("CSV содержит нулевой символ, который PostgreSQL не может сохранить.");

                var (row, error) = Csv.Parse(raw);
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(rows + 1, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(raw, NpgsqlDbType.Text, ct);

                if (row is null)
                {
                    invalid++;
                    for (var i = 0; i < 5; i++)
                        await copy.WriteNullAsync(ct);
                    await copy.WriteAsync(error!, NpgsqlDbType.Text, ct);
                    continue;
                }

                if (row.ExternalId is { } externalId)
                    await copy.WriteAsync(externalId, NpgsqlDbType.Text, ct);
                else
                    await copy.WriteNullAsync(ct);
                await copy.WriteAsync(row.Amount, NpgsqlDbType.Numeric, ct);
                await copy.WriteAsync(row.Currency, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(row.OccurredAt, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(row.OrderReference, NpgsqlDbType.Text, ct);
                await copy.WriteNullAsync(ct);
            }

            if (rows == 0)
                throw new InvalidImportException("В CSV нет строк данных.");
            await copy.CompleteAsync(ct);
        }

        await using var publish = Database.Command(connection, """
            with published as (
                insert into imported_rows (batch_id, line, raw, external_id, amount, currency, occurred_at, order_reference, error)
                select $1, line, raw, external_id, amount, currency, occurred_at, order_reference, error from staged_rows
            )
            update import_batches set row_count = $2, invalid_count = $3 where id = $1
            """, id, rows, invalid);
        await publish.ExecuteNonQueryAsync(ct);

        var batch = await ReadBatchAsync(connection, "id = $1", ct, id);
        await transaction.CommitAsync(ct);
        return new ImportReceipt(batch!, false);
    }

    public async Task<ImportBatch?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await ReadBatchAsync(connection, "id = $1", ct, id);
    }

    public async Task<List<ImportError>> ErrorsAsync(long id, int after, int limit, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var query = Database.Command(connection, """
            select line, raw, error from imported_rows
            where batch_id = $1 and line > $2 and error is not null
            order by line limit $3
            """, id, after, limit);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var errors = new List<ImportError>();
        while (await reader.ReadAsync(ct))
            errors.Add(new ImportError(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return errors;
    }

    private static async Task<ImportBatch?> ReadBatchAsync(NpgsqlConnection connection, string predicate,
        CancellationToken ct, params object[] args)
    {
        await using var query = Database.Command(connection, """
            select id, source, imported_at, row_count, invalid_count from import_batches where
            """ + " " + predicate, args);
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ImportBatch(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2), reader.GetInt32(3), reader.GetInt32(4))
            : null;
    }
}
