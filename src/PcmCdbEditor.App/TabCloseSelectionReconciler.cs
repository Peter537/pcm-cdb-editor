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
            .Where(table => !table.Equals(closingTable, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string? selectedTable = MatchRemaining(remainingTables, selectedAfterRemoval);
        if (selectedTable is null &&
            selectedBeforeClose is not null &&
            !selectedBeforeClose.Equals(closingTable, StringComparison.OrdinalIgnoreCase))
        {
            selectedTable = MatchRemaining(remainingTables, selectedBeforeClose);
        }

        if (selectedTable is null &&
            selectedBeforeClose?.Equals(closingTable, StringComparison.OrdinalIgnoreCase) == true &&
            remainingTables.Length > 0)
        {
            selectedTable = remainingTables[Math.Min(closingIndex, remainingTables.Length - 1)];
        }

        string? sidebarTable = selectedTable is not null &&
            visibleTables.Contains(selectedTable, StringComparer.OrdinalIgnoreCase)
                ? selectedTable
                : null;
        return new TabCloseSelectionResolution(selectedTable, sidebarTable);
    }

    private static int IndexOf(IReadOnlyList<string> tables, string tableName)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            if (tables[index].Equals(tableName, StringComparison.OrdinalIgnoreCase))
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
                table.Equals(tableName, StringComparison.OrdinalIgnoreCase));
}

internal sealed record TabCloseSelectionResolution(
    string? SelectedTable,
    string? SidebarTable);
