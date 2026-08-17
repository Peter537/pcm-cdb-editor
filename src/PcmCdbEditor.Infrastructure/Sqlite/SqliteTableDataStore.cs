using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Sqlite;

public sealed class SqliteTableDataStore : ITableDataStore
{
    private const int ForeignKeyLookupBatchSize = 400;
    private readonly Action<CommandKind>? _commandObserver;

    public SqliteTableDataStore()
    {
    }

    internal SqliteTableDataStore(Action<CommandKind> commandObserver)
    {
        ArgumentNullException.ThrowIfNull(commandObserver);
        _commandObserver = commandObserver;
    }

    internal enum CommandKind
    {
        Page,
        ForeignKeyLookup
    }

    public Task<TablePage> QueryAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => QueryCoreAsync(sqlitePath, catalog, query, _commandObserver, cancellationToken),
            cancellationToken);

    private static async Task<TablePage> QueryCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        var total = await CountCoreAsync(sqlitePath, catalog, query, cancellationToken)
            .ConfigureAwait(false);
        var slice = await QueryRowsCoreAsync(
                sqlitePath,
                catalog,
                query,
                commandObserver,
                cancellationToken)
            .ConfigureAwait(false);
        return new TablePage(
            slice.TableName,
            slice.Request,
            total,
            slice.Rows,
            slice.Request.Offset + slice.Rows.Count < total);
    }

    public Task<TableSlice> QueryRowsAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => QueryRowsCoreAsync(sqlitePath, catalog, query, _commandObserver, cancellationToken),
            cancellationToken);

    private static async Task<TableSlice> QueryRowsCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        PageQueryPlan plan = BuildPageQueryPlan(catalog, query);
        TableSchema table = plan.Table;
        ColumnSchema[] columns = plan.Columns;
        QueryProjection projection = plan.Projection;

        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = plan.CommandText;
        AddParameters(command, plan.Parameters);
        command.Parameters.AddWithValue("$limit", query.Page.Limit + 1);
        command.Parameters.AddWithValue("$offset", query.Page.Offset);

        var rows = new List<TypedRow>(query.Page.Limit);
        var hasMore = false;
        commandObserver?.Invoke(CommandKind.Page);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == query.Page.Limit)
                {
                    hasMore = true;
                    break;
                }

                var values = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
                for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                {
                    values[columns[ordinal].Name] = ReadValue(reader, ordinal);
                }

                rows.Add(new TypedRow(ReadIdentity(table, values, reader, columns.Length), values));
            }
        }

        IReadOnlyList<TypedRow> projectedRows = await ResolveForeignKeyDisplaysAsync(
                connection,
                transaction: null,
                rows,
                projection,
                commandObserver,
                cancellationToken)
            .ConfigureAwait(false);

        return new TableSlice(
            table.Name,
            query.Page,
            projectedRows,
            hasMore);
    }

    internal static string BuildPageCommandTextForDiagnostics(
        DatabaseSchemaCatalog catalog,
        TableQuery query) => BuildPageQueryPlan(catalog, query).CommandText;

    public Task<long> CountAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        string tableName,
        FilterExpression? filter,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => CountCoreAsync(sqlitePath, catalog, tableName, filter, cancellationToken),
            cancellationToken);

    private static async Task<long> CountCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        string tableName,
        FilterExpression? filter,
        CancellationToken cancellationToken)
    {
        var table = RequireTable(catalog, tableName);
        var builder = new QueryBuilder(table, QueryProjection.Raw);
        var where = builder.BuildWhere(filter, search: null);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteCountAsync(connection, table, where, builder.Parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> CountAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => CountCoreAsync(sqlitePath, catalog, query, cancellationToken),
            cancellationToken);

    private static async Task<long> CountCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        var table = RequireTable(catalog, query.TableName);
        var builder = new QueryBuilder(table, QueryProjection.Raw);
        var where = builder.BuildWhere(query.Filter, query.Search);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteCountAsync(connection, table, where, builder.Parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<EditResult> UpdateCellAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        CellUpdateOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return SqliteOperationRunner.RunAsync(
            () => UpdateAsync(
                sqlitePath,
                catalog,
                operation.TableName,
                operation.Identity,
                new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase)
                {
                    [operation.ColumnName] = operation.NewValue
                },
                operation.ExpectedRevision,
                cancellationToken),
            cancellationToken);
    }

    public Task<EditResult> UpdateRowAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        RowUpdateOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return SqliteOperationRunner.RunAsync(
            () => UpdateAsync(
                sqlitePath,
                catalog,
                operation.TableName,
                operation.Identity,
                operation.NewValues,
                operation.ExpectedRevision,
                cancellationToken),
            cancellationToken);
    }

    public Task<EditResult> InsertRowAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        RowInsertionOperation operation,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => InsertRowCoreAsync(sqlitePath, catalog, operation, cancellationToken),
            cancellationToken);

    private static async Task<EditResult> InsertRowCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        RowInsertionOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var table = RequireEditableTable(catalog, operation.TableName);
        var values = operation.Values;
        ValidateWritableValues(table, values, allowEmpty: true);
        var insertableColumns = table.Columns.Where(static column => !column.IsGenerated && !column.IsHidden).ToArray();
        foreach (var column in insertableColumns.Where(column =>
                     !column.IsNullable
                     && column.DefaultExpression is null
                     && !IsGeneratedIntegerPrimaryKey(table, column)))
        {
            if (!values.TryGetValue(column.Name, out var value) || value.Kind == SqliteValueKind.Null)
            {
                throw new InvalidDataException($"Required column '{column.Name}' does not have a value.");
            }
        }

        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            if (values.Count == 0)
            {
                command.CommandText = $"INSERT INTO {SqliteSupport.QuoteIdentifier(table.Name)} DEFAULT VALUES";
            }
            else
            {
                var selectedColumns = values.Keys.Select(name => RequireColumn(table, name)).ToArray();
                command.CommandText = $"INSERT INTO {SqliteSupport.QuoteIdentifier(table.Name)} ({string.Join(", ", selectedColumns.Select(column => SqliteSupport.QuoteIdentifier(column.Name)))}) VALUES ({string.Join(", ", selectedColumns.Select((_, index) => $"$v{index}"))})";
                for (var index = 0; index < selectedColumns.Length; index++)
                {
                    command.Parameters.AddWithValue($"$v{index}", ToDbValue(values[selectedColumns[index].Name]));
                }
            }

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var insertedIdentity = operation.AssignedIdentity
                ?? await ResolveInsertedIdentityAsync(
                        connection,
                        sqliteTransaction,
                        table,
                        values,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateIdentity(table, insertedIdentity);
            var current = await ReadRowAsync(
                    connection,
                    sqliteTransaction,
                    table,
                    insertedIdentity,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DBConcurrencyException("The inserted row could not be read back by its assigned identity.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditResult(affected, current);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsGeneratedIntegerPrimaryKey(TableSchema table, ColumnSchema column) =>
        table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey
        && table.StableIdentity.Columns.Count == 1
        && column.IsPrimaryKey
        && column.Affinity == SqliteAffinity.Integer;

    private static async Task<RowIdentity> ResolveInsertedIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        IReadOnlyDictionary<string, SqliteValue> values,
        CancellationToken cancellationToken)
    {
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
        {
            return RowIdentity.FromRowId(await ReadLastInsertedRowIdAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        if (table.StableIdentity.Kind != StableIdentityKind.DeclaredPrimaryKey)
        {
            throw new InvalidOperationException("The inserted row has no discoverable stable identity.");
        }

        var components = new List<RowIdentityComponent>(table.StableIdentity.Columns.Count);
        foreach (var name in table.StableIdentity.Columns)
        {
            if (values.TryGetValue(name, out var value) && value.Kind != SqliteValueKind.Null)
            {
                components.Add(new RowIdentityComponent(name, value));
                continue;
            }

            var column = RequireColumn(table, name);
            if (table.StableIdentity.Columns.Count == 1 && column.Affinity == SqliteAffinity.Integer)
            {
                components.Add(new RowIdentityComponent(
                    name,
                    SqliteValue.Integer(await ReadLastInsertedRowIdAsync(
                            connection,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false))));
                continue;
            }

            throw new InvalidOperationException(
                "Insert operations for composite or non-integer generated keys require an assigned identity.");
        }

        return RowIdentity.FromPrimaryKey(components);
    }

    private static async Task<long> ReadLastInsertedRowIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var identityCommand = connection.CreateCommand();
        identityCommand.Transaction = transaction;
        identityCommand.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(
            await identityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    public Task<EditResult> DeleteRowAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        RowDeletionOperation operation,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => DeleteRowCoreAsync(sqlitePath, catalog, operation, cancellationToken),
            cancellationToken);

    private static async Task<EditResult> DeleteRowCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        RowDeletionOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var table = RequireEditableTable(catalog, operation.TableName);
        var deletionIdentity = operation.DeletedRow.Identity
            ?? throw new InvalidOperationException("A row without a verified database identity cannot be deleted.");
        ValidateIdentity(table, deletionIdentity);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            await SqliteDeleteSafety.EnsureDeleteIsReversibleAsync(
                    connection,
                    sqliteTransaction,
                    table.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            var current = await RequireRevisionAsync(
                    connection,
                    sqliteTransaction,
                    catalog,
                    table,
                    deletionIdentity,
                    operation.DeletedRow.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = $"DELETE FROM {SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(deletionIdentity, command)}";
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new DBConcurrencyException("The row changed before deletion could complete.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditResult(affected, current);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<EditResult> UpdateAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        string tableName,
        RowIdentity identity,
        IReadOnlyDictionary<string, SqliteValue> newValues,
        RowRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var table = RequireEditableTable(catalog, tableName);
        ValidateIdentity(table, identity);
        ValidateWritableValues(table, newValues, allowEmpty: false);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            await RequireRevisionAsync(
                    connection,
                    sqliteTransaction,
                    catalog,
                    table,
                    identity,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            var assignments = new List<string>();
            var index = 0;
            foreach (var pair in newValues)
            {
                var column = RequireColumn(table, pair.Key);
                var parameterName = $"$v{index++}";
                assignments.Add($"{SqliteSupport.QuoteIdentifier(column.Name)} = {parameterName}");
                command.Parameters.AddWithValue(parameterName, ToDbValue(pair.Value));
            }

            command.CommandText = $"UPDATE {SqliteSupport.QuoteIdentifier(table.Name)} SET {string.Join(", ", assignments)} WHERE {BuildIdentityPredicate(identity, command)}";
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new DBConcurrencyException("The row changed before the update could complete.");
            }

            var current = await ReadRowAsync(connection, sqliteTransaction, table, identity, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditResult(affected, current);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<TypedRow> RequireRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        RowIdentity identity,
        RowRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var current = await ReadRowAsync(connection, transaction, table, identity, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DBConcurrencyException("The target row no longer exists.");
        if (current.Revision != expectedRevision
            && !await MatchesProjectedRevisionAsync(
                    connection,
                    transaction,
                    catalog,
                    table,
                    current,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new DBConcurrencyException("The target row was changed by another operation.");
        }

        return current;
    }

    private static async Task<bool> MatchesProjectedRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        TypedRow current,
        RowRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        foreach (var mode in new[] { ForeignKeyDisplayMode.ResolvedName, ForeignKeyDisplayMode.RawAndName })
        {
            var projection = QueryProjection.Create(catalog, table, mode);
            if (projection.ResolvedColumns.Length == 0)
            {
                continue;
            }

            TypedRow projected = (await ResolveForeignKeyDisplaysAsync(
                    connection,
                    transaction,
                    [current],
                    projection,
                    commandObserver: null,
                    cancellationToken)
                .ConfigureAwait(false))[0];

            if (projected.Revision == expectedRevision)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<TypedRow?> ReadRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowIdentity identity,
        CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {BuildSelectColumns(table, columns)} FROM {SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(identity, command)} LIMIT 2";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var values = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            values[columns[ordinal].Name] = ReadValue(reader, ordinal);
        }

        var row = new TypedRow(ReadIdentity(table, values, reader, columns.Length), values);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DBConcurrencyException("The row identity unexpectedly matched multiple rows.");
        }

        return row;
    }

    private static async Task<IReadOnlyList<TypedRow>> ResolveForeignKeyDisplaysAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        List<TypedRow> rows,
        QueryProjection projection,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || projection.ResolvedColumns.Length == 0)
        {
            return rows;
        }

        var lookups = new Dictionary<ForeignKeyLookupTarget, IReadOnlyDictionary<SqliteValue, ResolvedLookup>>();
        foreach (IGrouping<ForeignKeyLookupTarget, ResolvedColumn> group in
                 projection.ResolvedColumns.GroupBy(static resolved => resolved.LookupTarget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] sourceColumns = group
                .Select(static resolved => resolved.SourceColumn)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SqliteValue[] keys = sourceColumns
                .SelectMany(sourceColumn => rows.Select(row => row.Values[sourceColumn]))
                .Where(static value => value.Kind != SqliteValueKind.Null)
                .Distinct()
                .ToArray();
            lookups[group.Key] = await ReadResolvedLookupsAsync(
                    connection,
                    transaction,
                    group.Key,
                    keys,
                    commandObserver,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var projectedRows = new List<TypedRow>(rows.Count);
        foreach (TypedRow row in rows)
        {
            var values = new Dictionary<string, SqliteValue>(row.Values, StringComparer.OrdinalIgnoreCase);
            foreach (ResolvedColumn resolved in projection.ResolvedColumns)
            {
                SqliteValue source = row.Values[resolved.SourceColumn];
                if (source.Kind == SqliteValueKind.Null)
                {
                    values[resolved.SyntheticName] = SqliteValue.Null;
                    continue;
                }

                if (!lookups[resolved.LookupTarget].TryGetValue(source, out ResolvedLookup? lookup)
                    || lookup is null)
                {
                    throw new InvalidDataException("A bounded foreign-key lookup did not return its source key.");
                }

                string display = lookup.ResolvedName is null
                    ? lookup.RawText
                    : projection.Mode == ForeignKeyDisplayMode.RawAndName
                        ? $"{lookup.RawText} | {lookup.ResolvedName}"
                        : lookup.ResolvedName;
                values[resolved.SyntheticName] = SqliteValue.Text(display);
            }

            projectedRows.Add(new TypedRow(row.Identity, values));
        }

        return projectedRows;
    }

    private static async Task<IReadOnlyDictionary<SqliteValue, ResolvedLookup>> ReadResolvedLookupsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ForeignKeyLookupTarget target,
        SqliteValue[] keys,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<SqliteValue, ResolvedLookup>();
        for (var batchOffset = 0; batchOffset < keys.Length; batchOffset += ForeignKeyLookupBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteValue[] batch = keys
                .Skip(batchOffset)
                .Take(ForeignKeyLookupBatchSize)
                .ToArray();
            if (batch.Length == 0)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            string valuesSql = string.Join(", ", batch.Select((_, index) => $"({index}, $value{index})"));
            string targetTable = SqliteSupport.QuoteIdentifier(target.TargetTable);
            string targetColumn = SqliteSupport.QuoteIdentifier(target.TargetColumn);
            string displayColumn = SqliteSupport.QuoteIdentifier(target.DisplayColumn);
            command.CommandText = $@"
WITH ""__pcm_keys""(""__ordinal"", ""__value"") AS (VALUES {valuesSql}),
""__pcm_resolved"" AS (
    SELECT {targetColumn} AS ""__pcm_key"",
           COUNT(*) AS ""__pcm_count"",
           MAX(CAST({displayColumn} AS TEXT)) AS ""__pcm_name""
    FROM {targetTable}
    WHERE {targetColumn} IN (SELECT ""__value"" FROM ""__pcm_keys"")
    GROUP BY {targetColumn}
)
SELECT ""__pcm_keys"".""__ordinal"",
       CAST(""__pcm_keys"".""__value"" AS TEXT),
       CASE
           WHEN ""__pcm_resolved"".""__pcm_count"" = 1
                AND ""__pcm_resolved"".""__pcm_name"" IS NOT NULL
           THEN ""__pcm_resolved"".""__pcm_name""
           ELSE NULL
       END
FROM ""__pcm_keys""
LEFT JOIN ""__pcm_resolved""
    ON ""__pcm_resolved"".""__pcm_key"" = ""__pcm_keys"".""__value""
ORDER BY ""__pcm_keys"".""__ordinal""";
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$value{index}", ToDbValue(batch[index]));
            }

            commandObserver?.Invoke(CommandKind.ForeignKeyLookup);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int ordinal = reader.GetInt32(0);
                if ((uint)ordinal >= (uint)batch.Length || reader.IsDBNull(1))
                {
                    throw new InvalidDataException("SQLite returned an invalid bounded foreign-key lookup row.");
                }

                results[batch[ordinal]] = new ResolvedLookup(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        return results;
    }

    private static async Task<long> ExecuteCountAsync(
        SqliteConnection connection,
        TableSchema table,
        string where,
        IReadOnlyList<SqlParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {SqliteSupport.QuoteIdentifier(table.Name)} AS {QueryProjection.BaseAlias}{where}";
        AddParameters(command, parameters);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static PageQueryPlan BuildPageQueryPlan(
        DatabaseSchemaCatalog catalog,
        TableQuery query)
    {
        TableSchema table = RequireTable(catalog, query.TableName);
        ColumnSchema[] columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
        QueryProjection projection = QueryProjection.Create(catalog, table, query.ForeignKeyDisplayMode);
        var builder = new QueryBuilder(table, projection);
        string where = builder.BuildWhere(query.Filter, query.Search);
        string orderBy = builder.BuildOrderBy(query.Sorts);
        string joins = builder.BuildJoins();
        string commandText =
            $"SELECT {BuildSelectColumns(table, columns, qualify: true)} " +
            $"FROM {SqliteSupport.QuoteIdentifier(table.Name)} AS {QueryProjection.BaseAlias}" +
            $"{joins}{where}{orderBy} LIMIT $limit OFFSET $offset";
        return new PageQueryPlan(
            table,
            columns,
            projection,
            commandText,
            builder.Parameters.ToArray());
    }

    private static string BuildSelectColumns(
        TableSchema table,
        IReadOnlyList<ColumnSchema> columns,
        bool qualify = false)
    {
        var qualifier = qualify ? $"{QueryProjection.BaseAlias}." : string.Empty;
        var selected = columns
            .Select(column => $"{qualifier}{SqliteSupport.QuoteIdentifier(column.Name)}")
            .ToList();
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
        {
            selected.Add($"{qualifier}rowid AS \"__pcm_rowid\"");
        }

        return string.Join(", ", selected);
    }

    private static RowIdentity? ReadIdentity(
        TableSchema table,
        Dictionary<string, SqliteValue> values,
        SqliteDataReader reader,
        int rowIdOrdinal)
    {
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
        {
            return RowIdentity.FromRowId(reader.GetInt64(rowIdOrdinal));
        }

        if (table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey)
        {
            return RowIdentity.FromPrimaryKey(table.StableIdentity.Columns.Select(name =>
                new RowIdentityComponent(name, values[name])));
        }

        return null;
    }

    private static string BuildIdentityPredicate(RowIdentity identity, SqliteCommand command)
    {
        var predicates = new List<string>(identity.Components.Count);
        for (var index = 0; index < identity.Components.Count; index++)
        {
            var component = identity.Components[index];
            var parameterName = $"$id{index}";
            var identifier = identity.Kind == RowIdentityKind.RowId
                ? "rowid"
                : SqliteSupport.QuoteIdentifier(component.ColumnName);
            predicates.Add($"{identifier} = {parameterName}");
            command.Parameters.AddWithValue(parameterName, ToDbValue(component.Value));
        }

        return string.Join(" AND ", predicates);
    }

    private static void ValidateIdentity(TableSchema table, RowIdentity identity)
    {
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback
            && identity.Kind == RowIdentityKind.RowId
            && identity.Components.Count == 1)
        {
            return;
        }

        if (table.StableIdentity.Kind != StableIdentityKind.DeclaredPrimaryKey
            || identity.Kind != RowIdentityKind.DeclaredPrimaryKey
            || !table.StableIdentity.Columns.SequenceEqual(
                identity.Components.Select(static component => component.ColumnName),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied row identity does not match the discovered stable identity.");
        }
    }

    private static void ValidateWritableValues(
        TableSchema table,
        IReadOnlyDictionary<string, SqliteValue> values,
        bool allowEmpty)
    {
        if (!allowEmpty && values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        foreach (var pair in values)
        {
            var column = RequireColumn(table, pair.Key);
            if (column.IsGenerated || column.IsHidden)
            {
                throw new InvalidOperationException($"Column '{column.Name}' is not writable.");
            }

            if (!column.IsNullable && pair.Value.Kind == SqliteValueKind.Null)
            {
                throw new InvalidDataException($"Column '{column.Name}' does not allow NULL.");
            }

            if (pair.Value.Kind == SqliteValueKind.Blob)
            {
                throw new NotSupportedException("Blob values are preserved for reading but blob editing is not supported.");
            }
        }
    }

    private static TableSchema RequireEditableTable(DatabaseSchemaCatalog catalog, string tableName)
    {
        var table = RequireTable(catalog, tableName);
        if (table.EditCapability != TableEditCapability.Editable)
        {
            throw new InvalidOperationException($"Table '{table.Name}' is read-only because it has no safe editable identity.");
        }

        return table;
    }

    private static TableSchema RequireTable(DatabaseSchemaCatalog catalog, string tableName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return catalog.TryGetTable(tableName, out var table)
            ? table
            : throw new ArgumentException($"Table '{tableName}' is not present in the discovered schema.", nameof(tableName));
    }

    private static ColumnSchema RequireColumn(TableSchema table, string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return table.Columns.FirstOrDefault(column => column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Column '{columnName}' is not present in table '{table.Name}'.",
                nameof(columnName));
    }

    private static SqliteValue ReadValue(SqliteDataReader reader, int ordinal)
    {
        return reader.GetFieldType(ordinal) switch
        {
            _ when reader.IsDBNull(ordinal) => SqliteValue.Null,
            var type when type == typeof(long) => SqliteValue.Integer(reader.GetInt64(ordinal)),
            var type when type == typeof(double) => SqliteValue.Real(reader.GetDouble(ordinal)),
            var type when type == typeof(string) => SqliteValue.Text(reader.GetString(ordinal)),
            var type when type == typeof(byte[]) => SqliteValue.Blob((byte[])reader.GetValue(ordinal)),
            _ => throw new InvalidDataException($"SQLite returned unsupported storage class '{reader.GetFieldType(ordinal).Name}'.")
        };
    }

    private static object ToDbValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null => DBNull.Value,
        SqliteValueKind.Integer => value.IntegerValue,
        SqliteValueKind.Real => value.RealValue,
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        SqliteValueKind.Blob => value.GetBlobBytes(),
        _ => throw new InvalidOperationException($"Unsupported SQLite value kind '{value.Kind}'.")
    };

    private static void AddParameters(SqliteCommand command, IEnumerable<SqlParameterValue> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, ToDbValue(parameter.Value));
        }
    }

    private sealed record SqlParameterValue(string Name, SqliteValue Value);

    private sealed record PageQueryPlan(
        TableSchema Table,
        ColumnSchema[] Columns,
        QueryProjection Projection,
        string CommandText,
        IReadOnlyList<SqlParameterValue> Parameters);

    private sealed record ResolvedLookup(string RawText, string? ResolvedName);

    private sealed class QueryProjection
    {
        public const string BaseAlias = "\"__pcm_base\"";

        private QueryProjection(
            ForeignKeyDisplayMode mode,
            IEnumerable<ResolvedColumn>? resolvedColumns = null)
        {
            Mode = mode;
            ResolvedColumns = resolvedColumns?.ToArray() ?? [];
        }

        public static QueryProjection Raw { get; } = new(ForeignKeyDisplayMode.RawValue);

        public ForeignKeyDisplayMode Mode { get; }

        public ResolvedColumn[] ResolvedColumns { get; }

        public static QueryProjection Create(
            DatabaseSchemaCatalog catalog,
            TableSchema table,
            ForeignKeyDisplayMode mode)
        {
            if (mode == ForeignKeyDisplayMode.RawValue)
            {
                return Raw;
            }

            var relationships = new List<ResolvedColumn>();
            foreach (var group in table.Relationships.GroupBy(
                         static relationship => relationship.SourceColumn,
                         StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() != 1)
                {
                    continue;
                }

                var relationship = group.Single();
                if (string.IsNullOrWhiteSpace(relationship.TargetColumn)
                    || string.IsNullOrWhiteSpace(relationship.DisplayColumn)
                    || !table.Columns.Any(column => column.Name.Equals(
                        relationship.SourceColumn,
                        StringComparison.OrdinalIgnoreCase))
                    || !catalog.TryGetTable(relationship.TargetTable, out TableSchema target))
                {
                    continue;
                }

                ColumnSchema? targetColumn = target.Columns.FirstOrDefault(column => column.Name.Equals(
                        relationship.TargetColumn,
                        StringComparison.OrdinalIgnoreCase));
                ColumnSchema? displayColumn = target.Columns.FirstOrDefault(column => column.Name.Equals(
                        relationship.DisplayColumn,
                        StringComparison.OrdinalIgnoreCase));
                if (targetColumn is null || displayColumn is null)
                {
                    continue;
                }

                relationships.Add(new ResolvedColumn(
                    relationship.SourceColumn,
                    target.Name,
                    targetColumn.Name,
                    displayColumn.Name,
                    $"{relationship.SourceColumn}__display"));
            }

            return new QueryProjection(mode, relationships);
        }

        public bool TryGet(string name, out ResolvedColumn column)
        {
            column = ResolvedColumns.FirstOrDefault(item =>
                item.SyntheticName.Equals(name, StringComparison.OrdinalIgnoreCase))!;
            return column is not null;
        }
    }

    private sealed record ResolvedColumn(
        string SourceColumn,
        string TargetTable,
        string TargetColumn,
        string DisplayColumn,
        string SyntheticName)
    {
        public ForeignKeyLookupTarget LookupTarget { get; } = new(
            TargetTable,
            TargetColumn,
            DisplayColumn);
    }

    private sealed record ForeignKeyLookupTarget(
        string TargetTable,
        string TargetColumn,
        string DisplayColumn);

    private sealed class QueryBuilder(TableSchema table, QueryProjection projection)
    {
        private readonly List<SqlParameterValue> _parameters = [];
        private readonly List<string> _joins = [];

        public IReadOnlyList<SqlParameterValue> Parameters => _parameters;

        public string BuildJoins() => string.Concat(_joins);

        public string BuildWhere(FilterExpression? filter, GlobalSearchRequest? search)
        {
            var predicates = new List<string>();
            if (filter is not null)
            {
                predicates.Add(BuildFilter(filter));
            }

            if (search is not null && !string.IsNullOrWhiteSpace(search.Text))
            {
                var eligible = search.EligibleColumns
                    .Select(name => RequireColumn(table, name))
                    .Where(static column => column.Affinity != SqliteAffinity.Blob)
                    .ToArray();
                if (eligible.Length == 0)
                {
                    throw new ArgumentException("Global search needs at least one eligible column.", nameof(search));
                }

                var searchParameter = Add(SqliteValue.Text($"%{EscapeLike(search.Text)}%"));
                predicates.Add($"({string.Join(" OR ", eligible.Select(column => $"CAST({QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)} AS TEXT) LIKE {searchParameter} ESCAPE '\\' COLLATE NOCASE"))})");
            }

            return predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        }

        public string BuildOrderBy(IReadOnlyList<SortDescriptor> sorts)
        {
            var descriptors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sort in sorts)
            {
                if (projection.TryGet(sort.ColumnName, out var resolved))
                {
                    if (seen.Add(resolved.SyntheticName))
                    {
                        descriptors.Add(
                            $"{BuildResolvedSortExpression(resolved)} " +
                            (sort.Direction == SortDirection.Descending ? "DESC" : "ASC"));
                    }

                    continue;
                }

                var column = RequireColumn(table, sort.ColumnName);
                if (seen.Add(column.Name))
                {
                    descriptors.Add($"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)} {(sort.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
                }
            }

            if (table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey)
            {
                descriptors.AddRange(table.StableIdentity.Columns
                    .Where(seen.Add)
                    .Select(column => $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column)} ASC"));
            }
            else if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
            {
                descriptors.Add($"{QueryProjection.BaseAlias}.rowid ASC");
            }

            return descriptors.Count == 0 ? string.Empty : $" ORDER BY {string.Join(", ", descriptors)}";
        }

        private string BuildResolvedSortExpression(ResolvedColumn resolved)
        {
            string alias = $"\"__pcm_sort_fk_{_joins.Count}\"";
            string targetTable = SqliteSupport.QuoteIdentifier(resolved.TargetTable);
            string targetColumn = SqliteSupport.QuoteIdentifier(resolved.TargetColumn);
            string displayColumn = SqliteSupport.QuoteIdentifier(resolved.DisplayColumn);
            _joins.Add($@" LEFT JOIN (
    SELECT {targetColumn} AS ""__pcm_key"",
           COUNT(*) AS ""__pcm_count"",
           MAX(CAST({displayColumn} AS TEXT)) AS ""__pcm_name""
    FROM {targetTable}
    WHERE {targetColumn} IS NOT NULL
    GROUP BY {targetColumn}
) AS {alias}
ON {alias}.""__pcm_key"" = {QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(resolved.SourceColumn)}");

            string source =
                $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(resolved.SourceColumn)}";
            string uniqueName =
                $"{alias}.\"__pcm_count\" = 1 AND {alias}.\"__pcm_name\" IS NOT NULL";
            return projection.Mode == ForeignKeyDisplayMode.RawAndName
                ? $"CASE WHEN {source} IS NULL THEN NULL WHEN {uniqueName} THEN CAST({source} AS TEXT) || ' | ' || {alias}.\"__pcm_name\" ELSE CAST({source} AS TEXT) END"
                : $"CASE WHEN {source} IS NULL THEN NULL WHEN {uniqueName} THEN {alias}.\"__pcm_name\" ELSE CAST({source} AS TEXT) END";
        }

        private string BuildFilter(FilterExpression expression)
        {
            return expression switch
            {
                FilterCondition condition => BuildCondition(condition),
                FilterGroup group => $"({string.Join(group.Operator == FilterGroupOperator.And ? " AND " : " OR ", group.Children.Select(BuildFilter))})",
                _ => throw new ArgumentException("Unsupported filter expression type.", nameof(expression))
            };
        }

        private string BuildCondition(FilterCondition condition)
        {
            var column = RequireColumn(table, condition.ColumnName);
            var identifier = $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)}";
            if (condition.Operator == FilterOperator.IsNull)
            {
                return $"{identifier} IS NULL";
            }

            if (condition.Operator == FilterOperator.IsNotNull)
            {
                return $"{identifier} IS NOT NULL";
            }

            if (condition.Operator is FilterOperator.GreaterThan
                    or FilterOperator.GreaterThanOrEqual
                    or FilterOperator.LessThan
                    or FilterOperator.LessThanOrEqual
                && (column.Affinity is SqliteAffinity.Blob or SqliteAffinity.Text
                    || condition.Value.Kind is not (SqliteValueKind.Integer or SqliteValueKind.Real)))
            {
                throw new ArgumentException("Ordered comparison requires a numeric column and numeric typed value.");
            }

            var value = condition.Operator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith
                ? SqliteValue.Text(condition.Value.Kind == SqliteValueKind.Text
                    ? condition.Operator switch
                    {
                        FilterOperator.Contains => $"%{EscapeLike(condition.Value.TextValue ?? string.Empty)}%",
                        FilterOperator.StartsWith => $"{EscapeLike(condition.Value.TextValue ?? string.Empty)}%",
                        _ => $"%{EscapeLike(condition.Value.TextValue ?? string.Empty)}"
                    }
                    : throw new ArgumentException("LIKE filters require text values."))
                : condition.Value;
            var parameter = Add(value);
            return condition.Operator switch
            {
                FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith =>
                    $"{identifier} LIKE {parameter} ESCAPE '\\' COLLATE NOCASE",
                FilterOperator.Equals => $"{identifier} = {parameter}",
                FilterOperator.NotEquals => $"{identifier} <> {parameter}",
                FilterOperator.GreaterThan => $"{identifier} > {parameter}",
                FilterOperator.GreaterThanOrEqual => $"{identifier} >= {parameter}",
                FilterOperator.LessThan => $"{identifier} < {parameter}",
                FilterOperator.LessThanOrEqual => $"{identifier} <= {parameter}",
                _ => throw new ArgumentOutOfRangeException(nameof(condition), "Unsupported filter operator.")
            };
        }

        private string Add(SqliteValue value)
        {
            var name = $"$p{_parameters.Count}";
            _parameters.Add(new SqlParameterValue(name, value));
            return name;
        }

        private static string EscapeLike(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
