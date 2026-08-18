using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

/// <summary>
/// Holds one validated inline edit until the grid reports that its edit lifecycle has ended.
/// </summary>
public sealed class InlineEditCommitStager
{
    private PendingEdit? _pending;

    public void Stage(
        EditOperation operation,
        long bindGeneration,
        object rowToken,
        string columnName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(rowToken);
        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ArgumentException("A staged edit requires a column name.", nameof(columnName));
        }

        _pending = new PendingEdit(operation, bindGeneration, rowToken, columnName);
    }

    public EditOperation? Complete(
        bool committed,
        long bindGeneration,
        object? rowToken,
        string? columnName)
    {
        PendingEdit? pending = _pending;
        _pending = null;
        if (!committed ||
            pending is null ||
            pending.BindGeneration != bindGeneration ||
            !ReferenceEquals(pending.RowToken, rowToken) ||
            !pending.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return pending.Operation;
    }

    public void Clear() => _pending = null;

    private sealed record PendingEdit(
        EditOperation Operation,
        long BindGeneration,
        object RowToken,
        string ColumnName);
}
