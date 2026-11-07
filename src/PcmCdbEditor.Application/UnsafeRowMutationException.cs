namespace PcmCdbEditor.Application;

/// <summary>The edit or replay was refused before commit. Any tentative changes have been rolled back.</summary>
public sealed class UnsafeRowMutationException(string message, string? columnName = null) : InvalidOperationException(message)
{
    public string? ColumnName { get; } = columnName;
}
