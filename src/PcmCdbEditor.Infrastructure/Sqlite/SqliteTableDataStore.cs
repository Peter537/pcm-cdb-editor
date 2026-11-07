using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Sqlite;

public sealed class SqliteTableDataStore : ITableDataStore
{
    private static readonly string[] HiddenRowIdAliases = ["rowid", "_rowid_", "oid"];
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
        ArgumentNullException.ThrowIfNull(query);
        RowsQueryResult result = await ExecuteRowsQueryAsync(
                sqlitePath,
                catalog,
                query.Definition,
                query.Page,
                commandObserver,
                cancellationToken)
            .ConfigureAwait(false);
        return new TableSlice(
            query.TableName,
            query.Page,
            result.Rows,
            result.HasMore);
    }

    public Task<IReadOnlyList<TypedRow>> QueryAllRowsAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQueryDefinition query,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => QueryAllRowsCoreAsync(sqlitePath, catalog, query, _commandObserver, cancellationToken),
            cancellationToken);

    private static async Task<IReadOnlyList<TypedRow>> QueryAllRowsCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQueryDefinition query,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        RowsQueryResult result = await ExecuteRowsQueryAsync(
                sqlitePath,
                catalog,
                query,
                page: null,
                commandObserver,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.HasMore)
        {
            throw new InvalidDataException("An unbounded table query returned a partial result.");
        }

        return FreezeRows(result.Rows, cancellationToken);
    }

    private static async Task<RowsQueryResult> ExecuteRowsQueryAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQueryDefinition query,
        PageRequest? page,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        RowsQueryPlan plan = BuildRowsQueryPlan(catalog, query, page is not null);
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
        if (page is not null)
        {
            command.Parameters.AddWithValue("$limit", page.Limit + 1);
            command.Parameters.AddWithValue("$offset", page.Offset);
        }

        var rows = page is null
            ? new List<TypedRow>()
            : new List<TypedRow>(page.Limit);
        var hasMore = false;
        commandObserver?.Invoke(CommandKind.Page);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (page is not null && rows.Count == page.Limit)
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
                page is null,
                commandObserver,
                cancellationToken)
            .ConfigureAwait(false);

        return new RowsQueryResult(projectedRows, hasMore);
    }

    internal static string BuildPageCommandTextForDiagnostics(
        DatabaseSchemaCatalog catalog,
        TableQuery query) => BuildRowsQueryPlan(catalog, query.Definition, includePage: true).CommandText;

    internal static string BuildAllRowsCommandTextForDiagnostics(
        DatabaseSchemaCatalog catalog,
        TableQueryDefinition query) => BuildRowsQueryPlan(catalog, query, includePage: false).CommandText;

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
        var builder = new QueryBuilder(catalog, table);
        var where = builder.BuildWhere(filter, search: null);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteCountAsync(connection, table, builder.BuildWithClause(), where, builder.Parameters, cancellationToken)
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
        var builder = new QueryBuilder(catalog, table);
        var where = builder.BuildWhere(query.Filter, query.Search);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteCountAsync(connection, table, builder.BuildWithClause(), where, builder.Parameters, cancellationToken)
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
                chosenRelationships: null,
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
                operation.ChosenRelationships,
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
                     && !MayUseRowIdDefault(table, column)))
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
            await SqliteMutationSafety.EnsureInsertDeleteAreReversibleAsync(
                    connection, sqliteTransaction, table.Name, cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            if (values.Count == 0)
            {
                command.CommandText = $"INSERT OR ABORT INTO main.{SqliteSupport.QuoteIdentifier(table.Name)} DEFAULT VALUES";
            }
            else
            {
                var selectedColumns = values.Keys.Select(name => RequireColumn(table, name)).ToArray();
                command.CommandText = $"INSERT OR ABORT INTO main.{SqliteSupport.QuoteIdentifier(table.Name)} ({string.Join(", ", selectedColumns.Select(column => SqliteSupport.QuoteIdentifier(column.Name)))}) VALUES ({string.Join(", ", selectedColumns.Select((_, index) => $"$v{index}"))})";
                for (var index = 0; index < selectedColumns.Length; index++)
                {
                    command.Parameters.AddWithValue($"$v{index}", ToDbValue(values[selectedColumns[index].Name]));
                }
            }

            RowIdentity insertedIdentity = await ExecuteInsertReturningIdentityAsync(command, table, cancellationToken)
                .ConfigureAwait(false);
            if (operation.AssignedIdentity is { } expected && !expected.Equals(insertedIdentity))
            {
                throw new UnsafeRowMutationException(
                    $"SQLite assigned a different row identity in '{table.Name}' than the insertion requested.");
            }

            var current = await ReadRowAsync(
                    connection,
                    sqliteTransaction,
                    table,
                    insertedIdentity,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DBConcurrencyException("The inserted row could not be read back by its assigned identity.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditResult(1, current);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static bool MayUseRowIdDefault(TableSchema table, ColumnSchema column) =>
        !table.IsWithoutRowId
        && table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey
        && table.StableIdentity.Columns.Count == 1
        && column.IsPrimaryKey
        && column.DeclaredType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);

    private static async Task<RowIdentity> ExecuteInsertReturningIdentityAsync(
        SqliteCommand command,
        TableSchema table,
        CancellationToken cancellationToken)
    {
        bool useRowId = table.StableIdentity.Kind == StableIdentityKind.RowIdFallback;
        var columns = table.StableIdentity.Columns;
        command.CommandText += useRowId
            ? " RETURNING rowid"
            : $" RETURNING {string.Join(", ", columns.Select(SqliteSupport.QuoteIdentifier))}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DBConcurrencyException("SQLite did not return an inserted row.");
        }

        RowIdentity identity;
        if (useRowId)
        {
            identity = RowIdentity.FromRowId(reader.GetInt64(0));
        }
        else
        {
            var components = new List<RowIdentityComponent>(columns.Count);
            for (int ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                SqliteValue value = ReadValue(reader, ordinal);
                if (value.Kind == SqliteValueKind.Null)
                {
                    throw new UnsafeRowMutationException(
                        $"SQLite returned NULL for '{table.Name}.{columns[ordinal]}'. Enter a primary key value so Undo can identify the inserted row.",
                        columns[ordinal]);
                }

                components.Add(new RowIdentityComponent(columns[ordinal], value));
            }

            identity = RowIdentity.FromPrimaryKey(components);
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DBConcurrencyException("SQLite returned more than one inserted row.");
        }

        return identity;
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
            await SqliteMutationSafety.EnsureInsertDeleteAreReversibleAsync(
                    connection,
                    sqliteTransaction,
                    table.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            var current = await RequireRevisionAsync(
                    connection,
                    sqliteTransaction,
                    table,
                    deletionIdentity,
                    operation.DeletedRow.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = $"DELETE FROM main.{SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(deletionIdentity, command)}";
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
        IReadOnlyDictionary<string, RelationshipLookupOption>? chosenRelationships,
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
            bool hasChosenRelationships = chosenRelationships is { Count: > 0 };
            if (!hasChosenRelationships)
            {
                await SqliteMutationSafety.EnsureUpdateIsReversibleAsync(
                        connection, sqliteTransaction, table.Name, newValues.Keys, cancellationToken)
                    .ConfigureAwait(false);
            }

            TypedRow before;
            try
            {
                before = await RequireRevisionAsync(
                        connection, sqliteTransaction, table, identity, expectedRevision, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DBConcurrencyException exception) when (chosenRelationships is { Count: > 0 })
            {
                throw new UnsafeRowMutationException(exception.Message, chosenRelationships.Keys.First());
            }

            if (chosenRelationships is { Count: > 0 })
            {
                await SqliteRelationshipLookup.ValidateChoicesAsync(
                    connection, sqliteTransaction, catalog, table.Name, newValues, chosenRelationships,
                    afterWrite: false, cancellationToken).ConfigureAwait(false);

                if (newValues.All(pair => before.Values[pair.Key] == pair.Value))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new EditResult(0, before);
                }

                await SqliteMutationSafety.EnsureUpdateIsReversibleAsync(
                        connection, sqliteTransaction, table.Name, newValues.Keys, cancellationToken)
                    .ConfigureAwait(false);
            }
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

            command.CommandText = $"UPDATE OR ABORT main.{SqliteSupport.QuoteIdentifier(table.Name)} SET {string.Join(", ", assignments)} WHERE {BuildIdentityPredicate(identity, command)}";
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new DBConcurrencyException("The row changed before the update could complete.");
            }

            var current = await ReadRowAsync(connection, sqliteTransaction, table, identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DBConcurrencyException("The updated row could not be read back before commit.");
            if (chosenRelationships is { Count: > 0 })
            {
                await SqliteRelationshipLookup.ValidateChoicesAsync(
                    connection, sqliteTransaction, catalog, table.Name, current.Values, chosenRelationships,
                    afterWrite: true, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditResult(current.Revision == before.Revision ? 0 : affected, current);
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
        TableSchema table,
        RowIdentity identity,
        RowRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var current = await ReadRowAsync(connection, transaction, table, identity, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DBConcurrencyException("The target row no longer exists.");
        if (current.Revision != expectedRevision)
        {
            throw new DBConcurrencyException("The target row was changed by another operation.");
        }

        return current;
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
        command.CommandText = $"SELECT {BuildSelectColumns(table, columns)} FROM main.{SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(identity, command)} LIMIT 2";
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
        bool allRows,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || projection.Columns.Length == 0)
        {
            return rows;
        }

        var lookups = new Dictionary<ForeignKeyLookupTarget, IReadOnlyDictionary<SqliteValue, RelatedValue>>();
        foreach (var group in projection.Columns.Where(static column => column.LookupTarget is not null)
                     .GroupBy(static column => column.LookupTarget!))
        {
            var keys = new HashSet<SqliteValue>();
            foreach (RelationshipColumn column in group)
            {
                foreach (TypedRow row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SqliteValue value = row.Values[column.SourceColumn];
                    if (value.Kind != SqliteValueKind.Null)
                    {
                        keys.Add(value);
                    }
                }
            }

            lookups[group.Key] = await ReadResolvedLookupsAsync(
                    connection, transaction, group.Key, keys.ToArray(), allRows, commandObserver, cancellationToken)
                .ConfigureAwait(false);
        }

        var projectedRows = new List<TypedRow>(rows.Count);
        foreach (TypedRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relatedValues = new Dictionary<string, RelatedValue>(StringComparer.OrdinalIgnoreCase);
            foreach (RelationshipColumn column in projection.Columns)
            {
                SqliteValue source = row.Values[column.SourceColumn];
                if (source.Kind == SqliteValueKind.Null)
                {
                    continue;
                }

                relatedValues[column.SourceColumn] = column.LookupTarget is { } target
                    ? lookups[target][source]
                    : new RelatedValue(column.Fallback);
            }

            projectedRows.Add(new TypedRow(row.Identity, row.Values, relatedValues));
        }

        return projectedRows;
    }

    private static ReadOnlyCollection<TypedRow> FreezeRows(
        IReadOnlyList<TypedRow> rows,
        CancellationToken cancellationToken)
    {
        var snapshot = new TypedRow[rows.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot[index] = rows[index];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(snapshot);
    }

    private static async Task<IReadOnlyDictionary<SqliteValue, RelatedValue>> ReadResolvedLookupsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ForeignKeyLookupTarget target,
        SqliteValue[] keys,
        bool allRows,
        Action<CommandKind>? commandObserver,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<SqliteValue, RelatedValue>();
        bool stageKeys = allRows && keys.Length > ForeignKeyLookupBatchSize;
        if (stageKeys)
        {
            await StageLookupKeysAsync(connection, transaction, keys, cancellationToken).ConfigureAwait(false);
        }

        int queryBatchSize = stageKeys ? keys.Length : ForeignKeyLookupBatchSize;
        for (var batchOffset = 0; batchOffset < keys.Length; batchOffset += queryBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteValue[] batch = stageKeys ? keys : keys.Skip(batchOffset).Take(queryBatchSize).ToArray();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            string keySource = stageKeys
                ? "SELECT \"__ordinal\", \"__value\" FROM temp.\"__pcm_lookup_keys\""
                : "VALUES " + string.Join(", ", batch.Select((_, index) => $"({index}, $value{index})"));
            string targetColumn = SqliteSupport.QuoteIdentifier(target.TargetColumn);
            command.CommandText = $"""
                WITH "__pcm_keys"("__ordinal", "__value") AS ({keySource}),
                "__pcm_resolved" AS (
                    SELECT {targetColumn} AS "__pcm_key",
                           COUNT(*) AS "__pcm_count",
                           MAX({BuildLabelExpression(target)}) AS "__pcm_name"
                    FROM main.{SqliteSupport.QuoteIdentifier(target.TargetTable)}
                    WHERE {targetColumn} IN (SELECT +"__value" FROM "__pcm_keys")
                    GROUP BY {targetColumn}
                )
                SELECT "__pcm_keys"."__ordinal", COALESCE("__pcm_resolved"."__pcm_count", 0),
                       "__pcm_resolved"."__pcm_name"
                FROM "__pcm_keys"
                LEFT JOIN "__pcm_resolved"
                  ON "__pcm_resolved"."__pcm_key" = +"__pcm_keys"."__value"
                ORDER BY "__pcm_keys"."__ordinal"
                """;
            for (var index = 0; !stageKeys && index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$value{index}", ToDbValue(batch[index]));
            }

            commandObserver?.Invoke(CommandKind.ForeignKeyLookup);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int ordinal = reader.GetInt32(0);
                long count = reader.GetInt64(1);
                string? label = reader.IsDBNull(2) ? null : reader.GetString(2);
                RelationshipResolution resolution = count switch
                {
                    0 => RelationshipResolution.MissingTarget,
                    > 1 => RelationshipResolution.AmbiguousKey,
                    _ when label is null => RelationshipResolution.LabelUnavailable,
                    _ => RelationshipResolution.Resolved
                };
                if ((uint)ordinal >= (uint)batch.Length
                    || !results.TryAdd(batch[ordinal], new RelatedValue(
                        resolution, resolution == RelationshipResolution.Resolved ? label : null)))
                {
                    throw new InvalidDataException("SQLite returned an invalid foreign-key lookup row.");
                }
            }
        }

        if (stageKeys)
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DROP TABLE temp.\"__pcm_lookup_keys\"";
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private static async Task StageLookupKeysAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SqliteValue[] keys,
        CancellationToken cancellationToken)
    {
        // All rows can contain many distinct keys. Staging them in bounded batches
        // lets SQLite scan an unindexed target once and retain its comparison rules.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TEMP TABLE \"__pcm_lookup_keys\" (\"__ordinal\" INTEGER PRIMARY KEY, \"__value\")";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        for (int offset = 0; offset < keys.Length; offset += ForeignKeyLookupBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(ForeignKeyLookupBatchSize, keys.Length - offset);
            command.Parameters.Clear();
            var rows = new string[count];
            for (int index = 0; index < count; index++)
            {
                rows[index] = $"({offset + index}, $value{index})";
                command.Parameters.AddWithValue($"$value{index}", ToDbValue(keys[offset + index]));
            }

            command.CommandText = $"INSERT INTO temp.\"__pcm_lookup_keys\" VALUES {string.Join(", ", rows)}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> ExecuteCountAsync(
        SqliteConnection connection,
        TableSchema table,
        string withClause,
        string where,
        IReadOnlyList<SqlParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{withClause}SELECT COUNT(*) FROM main.{SqliteSupport.QuoteIdentifier(table.Name)} AS {QueryProjection.BaseAlias}{where}";
        AddParameters(command, parameters);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static RowsQueryPlan BuildRowsQueryPlan(
        DatabaseSchemaCatalog catalog,
        TableQueryDefinition query,
        bool includePage)
    {
        TableSchema table = RequireTable(catalog, query.TableName);
        ColumnSchema[] columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
        QueryProjection projection = QueryProjection.Create(catalog, table, query.ForeignKeyDisplayMode);
        var builder = new QueryBuilder(catalog, table);
        string where = builder.BuildWhere(query.Filter, query.Search);
        string orderBy = builder.BuildOrderBy(query.Sorts);
        string withClause = builder.BuildWithClause();
        string commandText =
            $"{withClause}SELECT {BuildSelectColumns(table, columns, qualify: true)} " +
            $"FROM main.{SqliteSupport.QuoteIdentifier(table.Name)} AS {QueryProjection.BaseAlias}" +
            $"{where}{orderBy}" +
            (includePage ? " LIMIT $limit OFFSET $offset" : string.Empty);
        return new RowsQueryPlan(
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

        if (table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey &&
            table.StableIdentity.Columns.All(name => values[name].Kind != SqliteValueKind.Null))
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

    private sealed record RowsQueryPlan(
        TableSchema Table,
        ColumnSchema[] Columns,
        QueryProjection Projection,
        string CommandText,
        IReadOnlyList<SqlParameterValue> Parameters);

    private sealed record RowsQueryResult(
        IReadOnlyList<TypedRow> Rows,
        bool HasMore);

    private sealed class QueryProjection(RelationshipColumn[] columns)
    {
        public const string BaseAlias = "\"__pcm_base\"";

        public RelationshipColumn[] Columns { get; } = columns;

        public static QueryProjection Create(
            DatabaseSchemaCatalog catalog,
            TableSchema table,
            ForeignKeyDisplayMode mode)
        {
            if (mode == ForeignKeyDisplayMode.RawValue)
            {
                return new QueryProjection([]);
            }

            var columns = new List<RelationshipColumn>();
            foreach (ColumnSchema column in table.Columns.Where(static column => !column.IsHidden))
            {
                ForeignKeyRelation[] relations = table.Relationships.Where(relation =>
                    relation.SourceColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (relations.Length == 0 && !column.Name.StartsWith("fkID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out var relation))
                {
                    columns.Add(new RelationshipColumn(
                        column.Name, CreateLookupTarget(catalog, relation), RelationshipResolution.Resolved));
                    continue;
                }

                RelationshipResolution fallback;
                if (relations.Length == 0)
                {
                    fallback = RelationshipResolution.UnknownRelationship;
                }
                else if (relations.Length > 1)
                {
                    fallback = RelationshipResolution.AmbiguousRelationship;
                }
                else if (relations[0].IsComposite)
                {
                    fallback = RelationshipResolution.CompositeRelationship;
                }
                else if (!catalog.TryGetTable(relations[0].TargetTable, out TableSchema target)
                    || !target.Columns.Any(key => !key.IsHidden
                        && key.Name.Equals(relations[0].TargetColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    fallback = RelationshipResolution.TargetUnavailable;
                }
                else
                {
                    columns.Add(new RelationshipColumn(column.Name,
                        new ForeignKeyLookupTarget(target.Name,
                            RequireColumn(target, relations[0].TargetColumn).Name, null, null),
                        RelationshipResolution.LabelUnavailable));
                    continue;
                }

                columns.Add(new RelationshipColumn(column.Name, null, fallback));
            }

            return new QueryProjection(columns.ToArray());
        }
    }

    private sealed record RelationshipColumn(
        string SourceColumn,
        ForeignKeyLookupTarget? LookupTarget,
        RelationshipResolution Fallback);

    private sealed record ForeignKeyLookupTarget(
        string TargetTable,
        string TargetColumn,
        string? DisplayColumn,
        string? AdditionalDisplayColumn);

    private static ForeignKeyLookupTarget CreateLookupTarget(DatabaseSchemaCatalog catalog, ForeignKeyRelation relation)
    {
        TableSchema target = RequireTable(catalog, relation.TargetTable);
        return new ForeignKeyLookupTarget(
            target.Name,
            RequireColumn(target, relation.TargetColumn).Name,
            RequireColumn(target, relation.DisplayColumn!).Name,
            relation.AdditionalDisplayColumn is { } additional ? RequireColumn(target, additional).Name : null);
    }

    private static string BuildLabelExpression(ForeignKeyLookupTarget target) =>
        SqliteRelationshipSql.BuildLabelExpression(target.DisplayColumn, target.AdditionalDisplayColumn);

    private sealed class QueryBuilder(DatabaseSchemaCatalog catalog, TableSchema table)
    {
        private readonly List<SqlParameterValue> _parameters = [];
        private readonly List<string> _commonTables = [];
        private readonly Dictionary<ForeignKeyLookupTarget, string> _lookupNames = [];
        private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<SqlParameterValue> Parameters => _parameters;

        public string BuildWithClause() => _commonTables.Count == 0 ? string.Empty : $"WITH {string.Join(", ", _commonTables)} ";

        public string BuildWhere(FilterExpression? filter, GlobalSearchRequest? search)
        {
            var predicates = new List<string>();
            if (filter is not null)
            {
                predicates.Add(BuildFilter(filter));
            }

            if (search is not null && !string.IsNullOrWhiteSpace(search.Text))
            {
                var matches = new List<string>();
                string parameter = Add(SqliteValue.Text($"%{EscapeLike(search.Text)}%"));
                foreach (string name in search.EligibleColumns.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ColumnSchema column = RequireColumn(table, name);
                    string storedValue = $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)}";
                    matches.Add($"(typeof({storedValue}) <> 'blob' AND CAST({storedValue} AS TEXT) LIKE {parameter} ESCAPE '\\' COLLATE NOCASE)");

                    if (RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out _))
                    {
                        matches.Add($"({GetLabelExpression(column)} LIKE {parameter} ESCAPE '\\' COLLATE NOCASE)");
                    }
                }

                if (matches.Count == 0)
                {
                    throw new ArgumentException("Global search needs at least one eligible column.", nameof(search));
                }

                predicates.Add($"({string.Join(" OR ", matches)})");
            }

            return predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        }

        public string BuildOrderBy(IReadOnlyList<SortDescriptor> sorts)
        {
            var descriptors = new List<string>();
            var storedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labelColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SortDescriptor sort in sorts)
            {
                if (!Enum.IsDefined(sort.Direction) || !Enum.IsDefined(sort.Target))
                {
                    throw new ArgumentOutOfRangeException(nameof(sorts), "Unsupported sort direction or target.");
                }

                ColumnSchema column = RequireColumn(table, sort.ColumnName);
                string expression;
                if (sort.Target == QueryValueTarget.RelatedLabel)
                {
                    if (!labelColumns.Add(column.Name))
                    {
                        continue;
                    }

                    expression = GetLabelExpression(column);
                }
                else
                {
                    if (!storedColumns.Add(column.Name))
                    {
                        continue;
                    }

                    expression = $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)}";
                }

                descriptors.Add($"{expression} {(sort.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
            }

            if (table.StableIdentity.Kind == StableIdentityKind.DeclaredPrimaryKey)
            {
                descriptors.AddRange(table.StableIdentity.Columns.Where(storedColumns.Add)
                    .Select(column => $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column)} ASC"));
                if (!table.IsWithoutRowId &&
                    HiddenRowIdAliases.FirstOrDefault(alias => !table.Columns.Any(column =>
                        column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase))) is { } rowIdAlias)
                {
                    // Legacy rowid tables can have several NULL primary keys.
                    descriptors.Add($"{QueryProjection.BaseAlias}.{rowIdAlias} ASC");
                }
            }
            else if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
            {
                descriptors.Add($"{QueryProjection.BaseAlias}.rowid ASC");
            }

            return descriptors.Count == 0 ? string.Empty : $" ORDER BY {string.Join(", ", descriptors)}";
        }

        private string GetLabelExpression(ColumnSchema column)
        {
            if (_labels.TryGetValue(column.Name, out string? expression))
            {
                return expression;
            }

            if (!RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out var relation))
            {
                throw new ArgumentException($"Column '{column.Name}' has no supported related label.");
            }

            ForeignKeyLookupTarget target = CreateLookupTarget(catalog, relation);
            if (!_lookupNames.TryGetValue(target, out string? lookupName))
            {
                lookupName = $"\"__pcm_relation_{_commonTables.Count}\"";
                string targetColumn = SqliteSupport.QuoteIdentifier(target.TargetColumn);
                _commonTables.Add($"""
                    {lookupName} AS MATERIALIZED (
                        SELECT {targetColumn} AS "__pcm_key", COUNT(*) AS "__pcm_count",
                               MAX({BuildLabelExpression(target)}) AS "__pcm_name"
                        FROM main.{SqliteSupport.QuoteIdentifier(target.TargetTable)}
                        WHERE {targetColumn} IS NOT NULL
                        GROUP BY {targetColumn}
                    )
                    """);
                _lookupNames.Add(target, lookupName);
            }

            expression = $"""
                (SELECT CASE WHEN "__pcm_count" = 1 THEN "__pcm_name" END
                 FROM {lookupName}
                 WHERE "__pcm_key" = +{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)}) COLLATE NOCASE
                """;
            _labels.Add(column.Name, expression);
            return expression;
        }

        private string BuildFilter(FilterExpression expression)
        {
            return expression switch
            {
                FilterCondition condition => BuildCondition(condition),
                FilterGroup group when Enum.IsDefined(group.Operator) =>
                    $"({string.Join(group.Operator == FilterGroupOperator.And ? " AND " : " OR ", group.Children.Select(BuildFilter))})",
                _ => throw new ArgumentException("Unsupported filter expression type.", nameof(expression))
            };
        }

        private string BuildCondition(FilterCondition condition)
        {
            if (!Enum.IsDefined(condition.Target) || !Enum.IsDefined(condition.Operator))
            {
                throw new ArgumentOutOfRangeException(nameof(condition), "Unsupported filter operator or target.");
            }

            var column = RequireColumn(table, condition.ColumnName);
            var identifier = condition.Target == QueryValueTarget.RelatedLabel
                ? GetLabelExpression(column)
                : $"{QueryProjection.BaseAlias}.{SqliteSupport.QuoteIdentifier(column.Name)}";
            if (condition.Operator == FilterOperator.IsNull)
            {
                return $"{identifier} IS NULL";
            }

            if (condition.Operator == FilterOperator.IsNotNull)
            {
                return $"{identifier} IS NOT NULL";
            }

            if (condition.Target == QueryValueTarget.RelatedLabel && condition.Value.Kind != SqliteValueKind.Text)
            {
                throw new ArgumentException("Related-label comparisons require a text value.", nameof(condition));
            }

            if (condition.Target == QueryValueTarget.StoredValue
                && condition.Operator is FilterOperator.GreaterThan
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
