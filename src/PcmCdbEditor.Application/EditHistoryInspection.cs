using System.Collections.ObjectModel;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class EditHistoryRow
{
    internal EditHistoryRow(
        string tableName,
        RowIdentity? identity,
        IReadOnlyDictionary<string, SqliteValue>? beforeValues,
        IReadOnlyDictionary<string, SqliteValue>? afterValues)
    {
        TableName = tableName;
        Identity = identity;
        BeforeValues = beforeValues;
        AfterValues = afterValues;
    }

    public string TableName { get; }
    public RowIdentity? Identity { get; }
    public IReadOnlyDictionary<string, SqliteValue>? BeforeValues { get; }
    public IReadOnlyDictionary<string, SqliteValue>? AfterValues { get; }

    public IEnumerable<string> ChangedColumns
    {
        get
        {
            if (BeforeValues is not { } before)
            {
                return AfterValues?.Keys ?? [];
            }

            if (AfterValues is not { } after)
            {
                return before.Keys;
            }

            return before.Keys.Union(after.Keys, SqliteIdentifierComparer.Instance)
                .Where(column => !before.TryGetValue(column, out SqliteValue oldValue) ||
                    !after.TryGetValue(column, out SqliteValue newValue) || oldValue != newValue);
        }
    }
}

/// <summary>
/// Projects one recorded operation or row at a time without expanding a batch in
/// the history snapshot. A null value map denotes a row absent on that side.
/// </summary>
public static class EditHistoryInspection
{
    public static string Describe(EditOperation operation) => operation switch
    {
        CellUpdateOperation cell => $"Edit {cell.TableName}.{cell.ColumnName}",
        RowUpdateOperation => $"Edit row in {operation.TableName}",
        RowInsertionOperation => $"Insert row in {operation.TableName}",
        RowDeletionOperation => $"Delete row in {operation.TableName}",
        MaintenanceEditOperation maintenance => maintenance.Description,
        _ => $"Edit {operation.TableName}"
    };

    public static int GetRowCount(EditOperation operation) => operation switch
    {
        MaintenanceEditOperation maintenance => maintenance.Changes.Count,
        CellUpdateOperation or RowUpdateOperation or RowInsertionOperation or RowDeletionOperation => 1,
        _ => 0
    };

    public static EditHistoryRow GetRow(EditOperation operation, int rowIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rowIndex, GetRowCount(operation));
        return operation switch
        {
            CellUpdateOperation cell => new EditHistoryRow(
                cell.TableName, cell.Identity,
                SingleValue(cell.ColumnName, cell.OldValue),
                SingleValue(cell.ColumnName, cell.NewValue)),
            RowUpdateOperation row => new EditHistoryRow(row.TableName, row.Identity, row.OldValues, row.NewValues),
            RowInsertionOperation insertion => new EditHistoryRow(
                insertion.TableName, insertion.AssignedIdentity, null, insertion.InsertedRow?.Values ?? insertion.Values),
            RowDeletionOperation deletion => new EditHistoryRow(
                deletion.TableName, deletion.DeletedRow.Identity, deletion.DeletedRow.Values, null),
            MaintenanceEditOperation maintenance => FromMaintenance(maintenance.Changes[rowIndex]),
            _ => throw new ArgumentException("The recorded edit type is unavailable for inspection.", nameof(operation))
        };
    }

    private static EditHistoryRow FromMaintenance(MaintenanceRowChange change) =>
        new(change.TableName, change.Identity, change.BeforeValues, change.AfterValues);

    private static ReadOnlyDictionary<string, SqliteValue> SingleValue(string column, SqliteValue value) =>
        new(new Dictionary<string, SqliteValue>(SqliteIdentifierComparer.Instance) { [column] = value });
}
