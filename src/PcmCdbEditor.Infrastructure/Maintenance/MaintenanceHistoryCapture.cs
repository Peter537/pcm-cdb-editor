using Microsoft.Data.Sqlite;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

internal static class MaintenanceHistoryCapture
{
    public static TableSchema RequireEditableTable(DatabaseSchemaCatalog catalog, string tableName)
    {
        if (!catalog.TryGetTable(tableName, out TableSchema? table)
            || table.EditCapability != TableEditCapability.Editable)
        {
            throw new InvalidOperationException(
                $"Maintenance history cannot safely identify rows in '{tableName}'.");
        }

        return table;
    }

    public static async Task<IReadOnlyList<TypedRow>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, table, null, [], cancellationToken).ConfigureAwait(false);

    public static async Task<IReadOnlyList<TypedRow>> ReadByIntegerIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        string idColumn,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        ColumnSchema column = table.Columns.FirstOrDefault(candidate =>
            candidate.Name.Equals(idColumn, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Maintenance history column '{idColumn}' no longer exists in '{table.Name}'.");
        string[] parameterNames = ids.Select((_, index) => $"$historyId{index}").ToArray();
        string predicate = $"{SqliteSupport.QuoteIdentifier(column.Name)} IN ({string.Join(", ", parameterNames)})";
        return await ReadAsync(
                connection,
                transaction,
                table,
                predicate,
                ids.Select((id, index) => (parameterNames[index], id)).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TypedRow>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        string? predicate,
        IReadOnlyList<(string Name, long Value)> parameters,
        CancellationToken cancellationToken)
    {
        ColumnSchema[] columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
        var projection = columns.Select(column => SqliteSupport.QuoteIdentifier(column.Name)).ToList();
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
        {
            projection.Add("rowid AS \"__pcm_history_rowid\"");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", projection)} FROM {SqliteSupport.QuoteIdentifier(table.Name)}"
            + (predicate is null ? string.Empty : $" WHERE {predicate}");
        foreach ((string name, long value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<TypedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            {
                values[columns[ordinal].Name] = ReadValue(reader, ordinal);
            }

            RowIdentity identity = table.StableIdentity.Kind == StableIdentityKind.RowIdFallback
                ? RowIdentity.FromRowId(reader.GetInt64(columns.Length))
                : RowIdentity.FromPrimaryKey(table.StableIdentity.Columns.Select(name =>
                    new RowIdentityComponent(name, values[name])));
            rows.Add(new TypedRow(identity, values));
        }

        return rows.AsReadOnly();
    }

    private static SqliteValue ReadValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return SqliteValue.Null;
        }

        return reader.GetFieldType(ordinal) switch
        {
            var type when type == typeof(long) => SqliteValue.Integer(reader.GetInt64(ordinal)),
            var type when type == typeof(double) => SqliteValue.Real(reader.GetDouble(ordinal)),
            var type when type == typeof(string) => SqliteValue.Text(reader.GetString(ordinal)),
            var type when type == typeof(byte[]) => SqliteValue.Blob((byte[])reader.GetValue(ordinal)),
            _ => throw new InvalidDataException("A maintenance row contains an unsupported SQLite storage class.")
        };
    }
}
