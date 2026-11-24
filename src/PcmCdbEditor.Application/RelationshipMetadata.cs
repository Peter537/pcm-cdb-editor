using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public static class RelationshipMetadata
{
    public static bool TryGetLookupRelation(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        string sourceColumn,
        out ForeignKeyRelation relation,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        relation = null!;
        ForeignKeyRelation[] matches = table.Relationships.Where(candidate =>
            SqliteIdentifierComparer.Instance.Equals(candidate.SourceColumn, sourceColumn)).ToArray();
        reason = matches.Length switch
        {
            0 => "No known relationship for this column.",
            > 1 => "More than one relationship is defined for this column.",
            _ when matches[0].IsComposite => "This relationship uses multiple columns. Enter its stored values directly.",
            _ => null
        };
        if (reason is not null)
        {
            return false;
        }

        ForeignKeyRelation match = matches[0];
        if (!HasVisibleColumn(table, sourceColumn)
            || !catalog.TryGetTable(match.TargetTable, out TableSchema target)
            || !HasVisibleColumn(target, match.TargetColumn))
        {
            reason = "The referenced table or key column is unavailable.";
            return false;
        }

        if (target.StableIdentity.Kind == StableIdentityKind.None)
        {
            reason = "The referenced table has no stable row identity for choosing or opening a specific record.";
            return false;
        }

        relation = match;
        return true;
    }

    public static bool TryGetLabelRelation(
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        string sourceColumn,
        out ForeignKeyRelation relation)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        relation = null!;
        if (!HasVisibleColumn(table, sourceColumn))
        {
            return false;
        }

        ForeignKeyRelation? match = null;
        foreach (ForeignKeyRelation candidate in table.Relationships)
        {
            if (!SqliteIdentifierComparer.Instance.Equals(candidate.SourceColumn, sourceColumn))
            {
                continue;
            }

            if (match is not null || candidate.IsComposite)
            {
                return false;
            }

            match = candidate;
        }

        if (match is null || !catalog.TryGetTable(match.TargetTable, out TableSchema target)
            || !HasVisibleColumn(target, match.TargetColumn)
            || !HasVisibleColumn(target, match.DisplayColumn)
            || (match.AdditionalDisplayColumn is { } additional && !HasVisibleColumn(target, additional)))
        {
            return false;
        }

        relation = match;
        return true;
    }

    private static bool HasVisibleColumn(TableSchema table, string? name) =>
        !string.IsNullOrWhiteSpace(name) && table.Columns.Any(column =>
            !column.IsHidden && SqliteIdentifierComparer.Instance.Equals(column.Name, name));
}
