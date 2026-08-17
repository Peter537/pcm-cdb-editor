using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

internal sealed record TableSortOption(string DescriptorColumnName, string Label);

internal static class ForeignKeySortDescriptorMapper
{
    private const string DisplaySuffix = "__display";

    public static TableSortOption[] GetOptions(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        ForeignKeyDisplayMode displayMode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        var displayableSources = displayMode == ForeignKeyDisplayMode.RawValue
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetDisplayableSources(catalog, table);
        var options = new List<TableSortOption>();
        foreach (ColumnSchema column in table.Columns
                     .Where(static column => !column.IsHidden)
                     .OrderBy(static column => column.Ordinal))
        {
            if (!displayableSources.Contains(column.Name))
            {
                options.Add(new TableSortOption(column.Name, column.Name));
                continue;
            }

            options.Add(new TableSortOption(column.Name, $"{column.Name} (raw value)"));
            options.Add(new TableSortOption(
                $"{column.Name}{DisplaySuffix}",
                displayMode == ForeignKeyDisplayMode.ResolvedName
                    ? $"{column.Name} (displayed name)"
                    : $"{column.Name} (displayed raw value and name)"));
        }

        return options.ToArray();
    }

    public static SortDescriptor[] Restore(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        ForeignKeyDisplayMode displayMode,
        IEnumerable<SortDescriptor>? descriptors)
    {
        if (descriptors is null)
        {
            return [];
        }

        var options = GetOptions(catalog, table, displayMode)
            .ToDictionary(
                static option => option.DescriptorColumnName,
                StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restored = new List<SortDescriptor>();
        foreach (SortDescriptor descriptor in descriptors)
        {
            if (!Enum.IsDefined(descriptor.Direction) ||
                !options.TryGetValue(descriptor.ColumnName, out TableSortOption? option) ||
                !seen.Add(option.DescriptorColumnName))
            {
                continue;
            }

            restored.Add(new SortDescriptor(option.DescriptorColumnName, descriptor.Direction));
        }

        return restored.ToArray();
    }

    private static HashSet<string> GetDisplayableSources(
        DatabaseSchemaCatalog catalog,
        TableSchema table)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, ForeignKeyRelation> group in table.Relationships.GroupBy(
                     static relationship => relationship.SourceColumn,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1)
            {
                continue;
            }

            ForeignKeyRelation relationship = group.Single();
            if (string.IsNullOrWhiteSpace(relationship.TargetColumn) ||
                string.IsNullOrWhiteSpace(relationship.DisplayColumn) ||
                !table.Columns.Any(column => column.Name.Equals(
                    relationship.SourceColumn,
                    StringComparison.OrdinalIgnoreCase)) ||
                !catalog.TryGetTable(relationship.TargetTable, out TableSchema target) ||
                !target.Columns.Any(column => column.Name.Equals(
                    relationship.TargetColumn,
                    StringComparison.OrdinalIgnoreCase)) ||
                !target.Columns.Any(column => column.Name.Equals(
                    relationship.DisplayColumn,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sources.Add(relationship.SourceColumn);
        }

        return sources;
    }
}
