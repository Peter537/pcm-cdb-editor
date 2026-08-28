using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

internal sealed record TableSortOption(string DescriptorColumnName, string Label);

internal sealed record TableHeaderSortIndicator(
    string HeaderColumnName,
    SortDirection Direction);

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

    public static SortDescriptor[] PlanHeaderClick(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        ForeignKeyDisplayMode displayMode,
        IEnumerable<SortDescriptor>? descriptors,
        string? headerColumnName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        SortDescriptor[] current = descriptors?.ToArray() ?? [];
        HashSet<string> displayableSources = displayMode == ForeignKeyDisplayMode.RawValue
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetDisplayableSources(catalog, table);
        if (!TryResolveHeaderDescriptor(
                table,
                displayableSources,
                headerColumnName,
                out string descriptorColumnName))
        {
            return current;
        }

        SortDirection direction = current.FirstOrDefault() is { } primary &&
                                  primary.ColumnName.Equals(
                                      descriptorColumnName,
                                      StringComparison.OrdinalIgnoreCase) &&
                                  primary.Direction == SortDirection.Descending
            ? SortDirection.Ascending
            : SortDirection.Descending;

        return
        [
            new SortDescriptor(descriptorColumnName, direction),
            .. current.Where(descriptor => !descriptor.ColumnName.Equals(
                descriptorColumnName,
                StringComparison.OrdinalIgnoreCase)),
        ];
    }

    public static TableHeaderSortIndicator? GetPrimaryHeaderIndicator(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        ForeignKeyDisplayMode displayMode,
        IEnumerable<SortDescriptor>? descriptors)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        SortDescriptor? primary = descriptors?.FirstOrDefault();
        if (primary is null || !Enum.IsDefined(primary.Direction))
        {
            return null;
        }

        HashSet<string> displayableSources = displayMode == ForeignKeyDisplayMode.RawValue
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetDisplayableSources(catalog, table);
        foreach (ColumnSchema column in table.Columns.Where(static column => !column.IsHidden))
        {
            if (column.Name.Equals(primary.ColumnName, StringComparison.OrdinalIgnoreCase) ||
                displayableSources.Contains(column.Name) &&
                $"{column.Name}{DisplaySuffix}".Equals(
                    primary.ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new TableHeaderSortIndicator(column.Name, primary.Direction);
            }
        }

        return null;
    }

    private static bool TryResolveHeaderDescriptor(
        TableSchema table,
        HashSet<string> displayableSources,
        string? headerColumnName,
        out string descriptorColumnName)
    {
        descriptorColumnName = string.Empty;
        if (string.IsNullOrWhiteSpace(headerColumnName))
        {
            return false;
        }

        ColumnSchema? column = table.Columns.FirstOrDefault(column =>
            !column.IsHidden &&
            column.Name.Equals(headerColumnName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            return false;
        }

        descriptorColumnName = displayableSources.Contains(column.Name)
            ? $"{column.Name}{DisplaySuffix}"
            : column.Name;
        return true;
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
