using System.Data;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.History;

/// <summary>
/// Replays editor and maintenance history against the isolated working SQLite
/// database. Guard validation and every row transition share one transaction.
/// </summary>
public sealed class SqliteEditOperationReplayer : IEditOperationReplayer
{
    public Task<EditReplayResult> ReplayAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        EditHistoryReplay replay,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ReplayCoreAsync(sqlitePath, catalog, replay, cancellationToken),
            cancellationToken);

    private static async Task<EditReplayResult> ReplayCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        EditHistoryReplay replay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(replay);
        IReadOnlyList<RowTarget> targets = GetTargets(replay.Operation);
        ValidateGuardCoverage(targets, replay.Guards);

        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            if (replay.Operation is MaintenanceEditOperation
                {
                    Tool: MaintenanceToolKind.RiderCreation
                })
            {
                await using var deferCommand = connection.CreateCommand();
                deferCommand.Transaction = sqliteTransaction;
                deferCommand.CommandText = "PRAGMA defer_foreign_keys = ON";
                await deferCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (RowReplayGuard guard in replay.Guards)
            {
                await ValidateGuardAsync(connection, sqliteTransaction, catalog, guard, cancellationToken)
                    .ConfigureAwait(false);
            }

            int affected = await ApplyAsync(
                    connection,
                    sqliteTransaction,
                    catalog,
                    replay.Operation,
                    replay.Direction,
                    cancellationToken)
                .ConfigureAwait(false);

            var oppositeGuards = new List<RowReplayGuard>(targets.Count);
            foreach (RowTarget target in targets)
            {
                TableSchema table = RequireEditableTable(catalog, target.TableName);
                TypedRow? row = await ReadRowAsync(
                        connection,
                        sqliteTransaction,
                        table,
                        target.Identity,
                        cancellationToken)
                    .ConfigureAwait(false);
                oppositeGuards.Add(row is null
                    ? RowReplayGuard.Absent(table.Name, target.Identity)
                    : RowReplayGuard.Present(table.Name, row));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EditReplayResult(affected, oppositeGuards.AsReadOnly());
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ValidateGuardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseSchemaCatalog catalog,
        RowReplayGuard guard,
        CancellationToken cancellationToken)
    {
        TableSchema table = RequireEditableTable(catalog, guard.TableName);
        TypedRow? current = await ReadRowAsync(
                connection,
                transaction,
                table,
                guard.Identity,
                cancellationToken)
            .ConfigureAwait(false);
        if (guard.Expectation == RowReplayExpectation.Absent)
        {
            if (current is not null)
            {
                throw new DBConcurrencyException(
                    $"History replay stopped because a row expected to be absent now exists in '{table.Name}'.");
            }

            return;
        }

        if (current is null)
        {
            throw new DBConcurrencyException(
                $"History replay stopped because a guarded row no longer exists in '{table.Name}'.");
        }

        if (current.Revision != guard.ExpectedRevision)
        {
            throw new DBConcurrencyException(
                $"History replay stopped because a guarded row changed in '{table.Name}'.");
        }
    }

    private static Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseSchemaCatalog catalog,
        EditOperation operation,
        EditReplayDirection direction,
        CancellationToken cancellationToken) => operation switch
    {
        CellUpdateOperation cell => UpdateAsync(
            connection,
            transaction,
            RequireEditableTable(catalog, cell.TableName),
            cell.Identity,
            new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase)
            {
                [cell.ColumnName] = direction == EditReplayDirection.Undo ? cell.OldValue : cell.NewValue
            },
            cancellationToken),
        RowUpdateOperation row => UpdateAsync(
            connection,
            transaction,
            RequireEditableTable(catalog, row.TableName),
            row.Identity,
            direction == EditReplayDirection.Undo ? row.OldValues : row.NewValues,
            cancellationToken),
        RowInsertionOperation insertion => ReplayInsertionAsync(
            connection,
            transaction,
            RequireEditableTable(catalog, insertion.TableName),
            insertion,
            direction,
            cancellationToken),
        RowDeletionOperation deletion => ReplayDeletionAsync(
            connection,
            transaction,
            RequireEditableTable(catalog, deletion.TableName),
            deletion,
            direction,
            cancellationToken),
        MaintenanceEditOperation maintenance => ReplayMaintenanceAsync(
            connection,
            transaction,
            catalog,
            maintenance,
            direction,
            cancellationToken),
        _ => throw new NotSupportedException(
            $"History replay does not support operation type '{operation.GetType().Name}'.")
    };

    private static Task<int> ReplayInsertionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowInsertionOperation insertion,
        EditReplayDirection direction,
        CancellationToken cancellationToken)
    {
        RowIdentity identity = insertion.AssignedIdentity
            ?? insertion.InsertedRow?.Identity
            ?? throw new InvalidOperationException(
                "An insertion history entry requires the database-assigned identity captured after insertion.");
        return direction == EditReplayDirection.Undo
            ? DeleteAsync(connection, transaction, table, identity, cancellationToken)
            : InsertAsync(
                connection,
                transaction,
                table,
                identity,
                insertion.InsertedRow?.Values ?? insertion.Values,
                cancellationToken);
    }

    private static Task<int> ReplayDeletionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowDeletionOperation deletion,
        EditReplayDirection direction,
        CancellationToken cancellationToken)
    {
        RowIdentity identity = deletion.DeletedRow.Identity
            ?? throw new InvalidOperationException("A deleted-row history entry has no stable identity.");
        return direction == EditReplayDirection.Undo
            ? InsertAsync(connection, transaction, table, identity, deletion.DeletedRow.Values, cancellationToken)
            : DeleteAsync(connection, transaction, table, identity, cancellationToken);
    }

    private static async Task<int> ReplayMaintenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseSchemaCatalog catalog,
        MaintenanceEditOperation maintenance,
        EditReplayDirection direction,
        CancellationToken cancellationToken)
    {
        IEnumerable<MaintenanceRowChange> ordered = direction == EditReplayDirection.Undo
            ? maintenance.Changes.Reverse()
            : maintenance.Changes;
        int affected = 0;
        foreach (MaintenanceRowChange change in ordered)
        {
            TableSchema table = RequireEditableTable(catalog, change.TableName);
            IReadOnlyDictionary<string, SqliteValue>? source = direction == EditReplayDirection.Undo
                ? change.AfterValues
                : change.BeforeValues;
            IReadOnlyDictionary<string, SqliteValue>? target = direction == EditReplayDirection.Undo
                ? change.BeforeValues
                : change.AfterValues;

            if (source is null)
            {
                affected += await InsertAsync(
                        connection,
                        transaction,
                        table,
                        change.Identity,
                        target ?? throw new InvalidDataException("An inserted maintenance row has no payload."),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (target is null)
            {
                affected += await DeleteAsync(
                        connection,
                        transaction,
                        table,
                        change.Identity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                affected += await UpdateAsync(
                        connection,
                        transaction,
                        table,
                        change.Identity,
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return affected;
    }

    private static async Task<int> UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowIdentity identity,
        IReadOnlyDictionary<string, SqliteValue> values,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(table, identity);
        if (values.Count == 0)
        {
            throw new InvalidDataException("A replay update requires at least one value.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var assignments = new List<string>(values.Count);
        int index = 0;
        foreach (KeyValuePair<string, SqliteValue> pair in values)
        {
            ColumnSchema column = RequireColumn(table, pair.Key);
            RowIdentityComponent? identityComponent = identity.Components.FirstOrDefault(component =>
                component.ColumnName.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            if (identityComponent is not null)
            {
                if (identityComponent.Value != pair.Value)
                {
                    throw new InvalidOperationException("History replay cannot change a stable identity column.");
                }

                continue;
            }

            if (column.IsGenerated || column.IsHidden)
            {
                // Complete deletion/maintenance snapshots may contain generated
                // values. SQLite recomputes them; replay never writes them.
                continue;
            }

            string parameterName = $"$v{index++}";
            assignments.Add($"{SqliteSupport.QuoteIdentifier(column.Name)} = {parameterName}");
            command.Parameters.AddWithValue(parameterName, ToDbValue(pair.Value));
        }

        if (assignments.Count == 0)
        {
            return 0;
        }

        command.CommandText = $"UPDATE {SqliteSupport.QuoteIdentifier(table.Name)} SET {string.Join(", ", assignments)} WHERE {BuildIdentityPredicate(identity, command)}";
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new DBConcurrencyException("A row disappeared while history replay was updating it.");
        }

        return affected;
    }

    private static async Task<int> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowIdentity identity,
        IReadOnlyDictionary<string, SqliteValue> values,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(table, identity);
        await SqliteDeleteSafety.EnsureInsertIsReversibleAsync(
                connection,
                transaction,
                table.Name,
                cancellationToken)
            .ConfigureAwait(false);
        var insertValues = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, SqliteValue> pair in values)
        {
            ColumnSchema column = RequireColumn(table, pair.Key);
            if (!column.IsGenerated && !column.IsHidden)
            {
                insertValues.Add(column.Name, pair.Value);
            }
        }

        foreach (RowIdentityComponent component in identity.Components)
        {
            if (identity.Kind == RowIdentityKind.DeclaredPrimaryKey)
            {
                insertValues[RequireColumn(table, component.ColumnName).Name] = component.Value;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new List<string>(insertValues.Count + 1);
        var parameters = new List<string>(insertValues.Count + 1);
        int index = 0;
        if (identity.Kind == RowIdentityKind.RowId)
        {
            names.Add("rowid");
            parameters.Add("$rowid");
            command.Parameters.AddWithValue("$rowid", ToDbValue(identity.Components[0].Value));
        }

        foreach (KeyValuePair<string, SqliteValue> pair in insertValues)
        {
            string parameterName = $"$v{index++}";
            names.Add(SqliteSupport.QuoteIdentifier(pair.Key));
            parameters.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, ToDbValue(pair.Value));
        }

        command.CommandText = names.Count == 0
            ? $"INSERT INTO {SqliteSupport.QuoteIdentifier(table.Name)} DEFAULT VALUES"
            : $"INSERT INTO {SqliteSupport.QuoteIdentifier(table.Name)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)})";
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new DBConcurrencyException("History replay did not restore exactly one row.");
        }

        return affected;
    }

    private static async Task<int> DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(table, identity);
        await SqliteDeleteSafety.EnsureDeleteIsReversibleAsync(
                connection,
                transaction,
                table.Name,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(identity, command)}";
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new DBConcurrencyException("A row disappeared while history replay was deleting it.");
        }

        return affected;
    }

    private static async Task<TypedRow?> ReadRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema table,
        RowIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(table, identity);
        ColumnSchema[] columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        string projection = string.Join(", ", columns.Select(column => SqliteSupport.QuoteIdentifier(column.Name)));
        if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
        {
            projection = projection.Length == 0 ? "rowid AS \"__pcm_rowid\"" : $"{projection}, rowid AS \"__pcm_rowid\"";
        }

        command.CommandText = $"SELECT {projection} FROM {SqliteSupport.QuoteIdentifier(table.Name)} WHERE {BuildIdentityPredicate(identity, command)} LIMIT 2";
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

        RowIdentity actualIdentity = table.StableIdentity.Kind == StableIdentityKind.RowIdFallback
            ? RowIdentity.FromRowId(reader.GetInt64(columns.Length))
            : RowIdentity.FromPrimaryKey(table.StableIdentity.Columns.Select(name =>
                new RowIdentityComponent(name, values[name])));
        var row = new TypedRow(actualIdentity, values);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DBConcurrencyException("A stable identity unexpectedly matched multiple rows.");
        }

        return row;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<RowTarget> GetTargets(EditOperation operation)
    {
        IEnumerable<RowTarget> targets = operation switch
        {
            CellUpdateOperation cell => [new RowTarget(cell.TableName, cell.Identity)],
            RowUpdateOperation row => [new RowTarget(row.TableName, row.Identity)],
            RowInsertionOperation insertion =>
            [
                new RowTarget(
                    insertion.TableName,
                    insertion.AssignedIdentity
                        ?? insertion.InsertedRow?.Identity
                        ?? throw new InvalidOperationException(
                            "An insertion must capture its assigned identity before it is recorded."))
            ],
            RowDeletionOperation deletion =>
            [
                new RowTarget(
                    deletion.TableName,
                    deletion.DeletedRow.Identity
                        ?? throw new InvalidOperationException("A deleted row has no stable identity."))
            ],
            MaintenanceEditOperation maintenance => maintenance.Changes.Select(static change =>
                new RowTarget(change.TableName, change.Identity)),
            _ => throw new NotSupportedException(
                $"History replay does not support operation type '{operation.GetType().Name}'.")
        };

        RowTarget[] copy = targets.ToArray();
        if (copy.Select(TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
        {
            throw new InvalidDataException("A history operation cannot target the same row more than once.");
        }

        return Array.AsReadOnly(copy);
    }

    private static void ValidateGuardCoverage(
        IReadOnlyList<RowTarget> targets,
        IReadOnlyList<RowReplayGuard> guards)
    {
        if (guards.Count != targets.Count
            || guards.Select(static guard => TargetKey(new RowTarget(guard.TableName, guard.Identity)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != guards.Count)
        {
            throw new InvalidOperationException("History replay requires exactly one row guard per target row.");
        }

        var targetKeys = targets.Select(TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (guards.Any(guard => !targetKeys.Contains(TargetKey(new RowTarget(guard.TableName, guard.Identity)))))
        {
            throw new InvalidOperationException("A history replay guard does not match the operation targets.");
        }
    }

    private static TableSchema RequireEditableTable(DatabaseSchemaCatalog catalog, string tableName)
    {
        if (!catalog.TryGetTable(tableName, out TableSchema? table))
        {
            throw new InvalidOperationException("The history table no longer exists in the current schema.");
        }

        if (table.EditCapability != TableEditCapability.Editable)
        {
            throw new InvalidOperationException($"Table '{table.Name}' is no longer safely editable.");
        }

        return table;
    }

    private static ColumnSchema RequireColumn(TableSchema table, string columnName) =>
        table.Columns.FirstOrDefault(column => column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Column '{columnName}' no longer exists in table '{table.Name}'.");

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
            throw new InvalidOperationException(
                "A history row identity no longer matches the discovered stable identity.");
        }
    }

    private static string BuildIdentityPredicate(RowIdentity identity, SqliteCommand command)
    {
        var predicates = new List<string>(identity.Components.Count);
        for (var index = 0; index < identity.Components.Count; index++)
        {
            RowIdentityComponent component = identity.Components[index];
            string parameterName = $"$id{index}";
            string columnName = identity.Kind == RowIdentityKind.RowId
                ? "rowid"
                : SqliteSupport.QuoteIdentifier(component.ColumnName);
            predicates.Add($"{columnName} = {parameterName}");
            command.Parameters.AddWithValue(parameterName, ToDbValue(component.Value));
        }

        return string.Join(" AND ", predicates);
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
            _ => throw new InvalidDataException("The row contains an unsupported SQLite storage class.")
        };
    }

    private static object ToDbValue(SqliteValue value) => value.ToClrValue() ?? DBNull.Value;

    private static string TargetKey(RowTarget target) => $"{target.TableName}\u001f{target.Identity}";

    private sealed record RowTarget(string TableName, RowIdentity Identity);
}
