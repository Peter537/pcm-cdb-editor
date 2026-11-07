using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

internal sealed record TableQueryColumnOption(ColumnSchema Column, QueryValueTarget Target, string Label)
{
    public string ColumnName => Column.Name;

    public SqliteAffinity Affinity => Target == QueryValueTarget.RelatedLabel ? SqliteAffinity.Text : Column.Affinity;

    public bool Matches(string columnName, QueryValueTarget target) =>
        Target == target && ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase);
}

internal sealed record TableHeaderSortIndicator(string HeaderColumnName, SortDirection Direction);

internal static class ForeignKeySortDescriptorMapper
{
    public static TableQueryColumnOption[] GetOptions(DatabaseSchemaCatalog catalog, TableSchema table)
    {
        var options = new List<TableQueryColumnOption>();
        foreach (ColumnSchema column in table.Columns.Where(static column => !column.IsHidden)
                     .OrderBy(static column => column.Ordinal))
        {
            bool hasLabel = RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out _);
            options.Add(new TableQueryColumnOption(
                column,
                QueryValueTarget.StoredValue,
                hasLabel ? $"{column.Name} (stored key)" : column.Name));
            if (hasLabel)
            {
                options.Add(new TableQueryColumnOption(
                    column,
                    QueryValueTarget.RelatedLabel,
                    $"{column.Name} (related name)"));
            }
        }

        return options.ToArray();
    }

    public static SortDescriptor[] Restore(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        IEnumerable<SortDescriptor>? descriptors)
    {
        if (descriptors is null)
        {
            return [];
        }

        var seen = new HashSet<(string, QueryValueTarget)>();
        var restored = new List<SortDescriptor>();
        foreach (SortDescriptor descriptor in descriptors)
        {
            if (Enum.IsDefined(descriptor.Direction) &&
                FindSortableColumn(catalog, table, descriptor.ColumnName, descriptor.Target) is { } column &&
                seen.Add((column.Name, descriptor.Target)))
            {
                restored.Add(descriptor with { ColumnName = column.Name });
            }
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
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        SortDescriptor[] current = descriptors?.ToArray() ?? [];
        var column = FindSortableColumn(catalog, table, headerColumnName, QueryValueTarget.StoredValue);
        if (column is null)
        {
            return current;
        }

        QueryValueTarget target = displayMode != ForeignKeyDisplayMode.RawValue &&
            RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out _)
                ? QueryValueTarget.RelatedLabel
                : QueryValueTarget.StoredValue;
        bool Matches(SortDescriptor descriptor) =>
            descriptor.Target == target && descriptor.ColumnName.Equals(column.Name, StringComparison.OrdinalIgnoreCase);
        var direction = current.FirstOrDefault() is { Direction: SortDirection.Descending } primary && Matches(primary)
            ? SortDirection.Ascending
            : SortDirection.Descending;

        return [new SortDescriptor(column.Name, direction, target), .. current.Where(sort => !Matches(sort))];
    }

    public static TableHeaderSortIndicator? GetPrimaryHeaderIndicator(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        IEnumerable<SortDescriptor>? descriptors)
    {
        SortDescriptor? primary = descriptors?.FirstOrDefault();
        return primary is not null &&
            Enum.IsDefined(primary.Direction) &&
            FindSortableColumn(catalog, table, primary.ColumnName, primary.Target) is { } column
                ? new TableHeaderSortIndicator(column.Name, primary.Direction)
                : null;
    }

    public static string Describe(SortDescriptor descriptor)
    {
        string target = descriptor.Target == QueryValueTarget.RelatedLabel ? "related name" : "stored value";
        string direction = descriptor.Direction == SortDirection.Ascending ? "ascending" : "descending";
        return $"{descriptor.ColumnName} ({target}), {direction}";
    }

    private static ColumnSchema? FindSortableColumn(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        string? columnName,
        QueryValueTarget target)
    {
        if (!Enum.IsDefined(target))
        {
            return null;
        }

        var column = table.Columns.FirstOrDefault(candidate =>
            !candidate.IsHidden && candidate.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        return column is not null && (target == QueryValueTarget.StoredValue ||
            RelationshipMetadata.TryGetLabelRelation(catalog, table, column.Name, out _))
                ? column
                : null;
    }
}
