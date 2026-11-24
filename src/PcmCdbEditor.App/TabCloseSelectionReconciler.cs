using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

internal static class TabCloseSelectionReconciler
{
    public static TabCloseSelectionResolution Resolve(
        IReadOnlyList<string> openTablesBeforeClose,
        string closingTable,
        string? selectedBeforeClose,
        string? selectedAfterRemoval,
        IReadOnlyCollection<string> visibleTables)
    {
        ArgumentNullException.ThrowIfNull(openTablesBeforeClose);
        ArgumentException.ThrowIfNullOrWhiteSpace(closingTable);
        ArgumentNullException.ThrowIfNull(visibleTables);

        int closingIndex = IndexOf(openTablesBeforeClose, closingTable);
        if (closingIndex < 0)
        {
            throw new ArgumentException(
                "The closing table must be present in the pre-close tab list.",
                nameof(closingTable));
        }

        string[] remainingTables = openTablesBeforeClose
            .Where(table => !SqliteIdentifierComparer.Instance.Equals(table, closingTable))
            .ToArray();
        string? selectedTable = MatchRemaining(remainingTables, selectedAfterRemoval);
        if (selectedTable is null &&
            selectedBeforeClose is not null &&
            !SqliteIdentifierComparer.Instance.Equals(selectedBeforeClose, closingTable))
        {
            selectedTable = MatchRemaining(remainingTables, selectedBeforeClose);
        }

        if (selectedTable is null &&
            SqliteIdentifierComparer.Instance.Equals(selectedBeforeClose, closingTable) &&
            remainingTables.Length > 0)
        {
            selectedTable = remainingTables[Math.Min(closingIndex, remainingTables.Length - 1)];
        }

        string? sidebarTable = selectedTable is not null &&
            visibleTables.Contains(selectedTable, SqliteIdentifierComparer.Instance)
                ? selectedTable
                : null;
        return new TabCloseSelectionResolution(selectedTable, sidebarTable);
    }

    private static int IndexOf(IReadOnlyList<string> tables, string tableName)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            if (SqliteIdentifierComparer.Instance.Equals(tables[index], tableName))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? MatchRemaining(IReadOnlyList<string> remainingTables, string? tableName) =>
        tableName is null
            ? null
            : remainingTables.FirstOrDefault(table =>
                SqliteIdentifierComparer.Instance.Equals(table, tableName));
}

internal sealed record TabCloseSelectionResolution(
    string? SelectedTable,
    string? SidebarTable);
