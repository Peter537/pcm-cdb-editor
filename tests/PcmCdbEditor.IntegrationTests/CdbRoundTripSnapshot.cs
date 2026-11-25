using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

internal sealed class CdbRoundTripSnapshot
{
    private readonly SortedDictionary<string, CatalogObject> _objects = new(StringComparer.Ordinal);

    public static async Task<CdbRoundTripSnapshot> ReadAsync(string sqlitePath, CancellationToken cancellationToken)
    {
        var snapshot = new CdbRoundTripSnapshot();
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interrupt = await SqliteOperationRunner.OpenInterruptiblyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var objects = new List<(string Name, string Type, long WithoutRowId, long Strict)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT name, type, wr, strict FROM pragma_table_list
                WHERE schema = 'main' AND type IN ('table', 'view', 'virtual')
                  AND name NOT LIKE 'sqlite!_%' ESCAPE '!'
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        foreach (var (name, type, withoutRowId, strict) in objects)
        {
            var identifier = SqliteSupport.QuoteIdentifier(name);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA main.table_xinfo({identifier})";
            string columns = await DigestRowsAsync(command, cancellationToken).ConfigureAwait(false);
            string? contents = null;
            if (type != "view")
            {
                command.CommandText = $"SELECT * FROM main.{identifier}";
                contents = await DigestRowsAsync(command, cancellationToken).ConfigureAwait(false);
            }

            snapshot._objects.Add(name, new CatalogObject(type, withoutRowId, strict, columns, contents));
        }

        return snapshot;
    }

    public string? FindDifference(CdbRoundTripSnapshot actual)
    {
        if (!_objects.Keys.SequenceEqual(actual._objects.Keys, StringComparer.Ordinal))
        {
            return "The round trip changed the exported object names or count.";
        }

        var ordinal = 0;
        foreach (var (name, expected) in _objects)
        {
            ordinal++;
            var observed = actual._objects[name];
            if (expected.Type != observed.Type || expected.WithoutRowId != observed.WithoutRowId ||
                expected.Strict != observed.Strict || expected.Columns != observed.Columns)
            {
                return $"The round trip changed the column metadata or type of exported object {ordinal}.";
            }

            if (expected.Contents != observed.Contents)
            {
                return $"The round trip changed the typed rows of exported object {ordinal}.";
            }
        }

        return null;
    }

    private static async Task<string> DigestRowsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<byte[]>();
        using var row = new MemoryStream();
        using var writer = new BinaryWriter(row);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            row.SetLength(0);
            row.Position = 0;
            for (var i = 0; i < reader.FieldCount; i++)
            {
                switch (reader.GetValue(i))
                {
                    case DBNull:
                        writer.Write((byte)0);
                        break;
                    case long integer:
                        writer.Write((byte)1);
                        writer.Write(integer);
                        break;
                    case double real:
                        writer.Write((byte)2);
                        writer.Write(real);
                        break;
                    case string text:
                        writer.Write((byte)3);
                        writer.Write(text);
                        break;
                    case byte[] blob:
                        writer.Write((byte)4);
                        writer.Write(blob.Length);
                        writer.Write(blob);
                        break;
                    default:
                        throw new InvalidDataException("Unexpected SQLite storage class in the round-trip comparison.");
                }
            }

            rows.Add(SHA256.HashData(row.GetBuffer().AsSpan(0, checked((int)row.Length))));
        }

        rows.Sort(static (left, right) => left.AsSpan().SequenceCompareTo(right));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] hash in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            digest.AppendData(hash);
        }

        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private sealed record CatalogObject(string Type, long WithoutRowId, long Strict, string Columns, string? Contents);
}
