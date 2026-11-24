using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

/// <summary>
/// A complete managed presentation ready to bind to the native grid.
/// </summary>
public sealed class GridPagePresentation
{
    private GridPagePresentation(
        TableSchema schema,
        ColumnSchema[] columns,
        GridRowPresentation[] rows)
    {
        Schema = schema;
        Columns = columns;
        Rows = rows;
    }

    internal TableSchema Schema { get; }

    internal ColumnSchema[] Columns { get; }

    internal GridRowPresentation[] Rows { get; }

    internal static Task<GridPagePresentation> PrepareAsync(
        TableSchema schema,
        IReadOnlyList<TypedRow> rows,
        ForeignKeyDisplayMode displayMode,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            ColumnSchema[] columns = schema.Columns
                .Where(static column => !column.IsHidden)
                .OrderBy(static column => column.Ordinal)
                .ToArray();
            var preparedRows = new GridRowPresentation[rows.Count];
            for (int index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                preparedRows[index] = new GridRowPresentation(schema.Name, rows[index], columns, displayMode, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new GridPagePresentation(schema, columns, preparedRows);
        }, cancellationToken);
}

public sealed class GridRowPresentation
{
    internal GridRowPresentation(
        string tableName,
        TypedRow row,
        IReadOnlyList<ColumnSchema> columns,
        ForeignKeyDisplayMode displayMode,
        CancellationToken cancellationToken)
    {
        TableName = tableName;
        Row = row;
        Identity = row.Identity;
        Revision = row.Revision;
        var cells = new GridCellPresentation[columns.Count];
        for (int index = 0; index < columns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ColumnSchema column = columns[index];
            cells[index] = new GridCellPresentation(
                column.Name,
                row.Values.TryGetValue(column.Name, out SqliteValue value)
                    ? value
                    : SqliteValue.Null,
                row.RelatedValues.GetValueOrDefault(column.Name),
                displayMode);
        }

        Cells = cells;
    }

    public string TableName { get; }

    public TypedRow Row { get; }

    public RowIdentity? Identity { get; }

    public RowRevision Revision { get; }

    public IReadOnlyList<GridCellPresentation> Cells { get; }

    public bool TryGetCell(string columnName, out GridCellPresentation cell)
    {
        cell = Cells.FirstOrDefault(candidate =>
            SqliteIdentifierComparer.Instance.Equals(candidate.ColumnName, columnName))!;
        return cell is not null;
    }
}

public sealed record GridCellPresentation
{
    public GridCellPresentation(
        string columnName,
        SqliteValue value,
        RelatedValue? relatedValue,
        ForeignKeyDisplayMode displayMode)
    {
        ColumnName = columnName;
        Value = value;
        string? label = relatedValue is { Resolution: RelationshipResolution.Resolved, Label: { } text } &&
            !string.IsNullOrWhiteSpace(text) ? text : null;
        IsDisplayProjection = label is not null &&
            displayMode is ForeignKeyDisplayMode.RawAndName or ForeignKeyDisplayMode.ResolvedName;
        CombinedLabel = displayMode == ForeignKeyDisplayMode.RawAndName ? label : null;
        Display = displayMode switch
        {
            ForeignKeyDisplayMode.RawAndName when label is not null => $"{label} · {Format(value)}",
            ForeignKeyDisplayMode.ResolvedName when label is not null => label,
            _ => Format(value),
        };
        RelationshipHelp = relatedValue?.Resolution switch
        {
            RelationshipResolution.TargetUnavailable => "The referenced table or key column is unavailable.",
            RelationshipResolution.MissingTarget => "The referenced record could not be found for this stored key.",
            RelationshipResolution.AmbiguousKey => "More than one record matches this stored key.",
            RelationshipResolution.LabelUnavailable => "No readable name is available for this relationship.",
            RelationshipResolution.Resolved when label is null => "No readable name is available for this relationship.",
            RelationshipResolution.UnknownRelationship => "No known relationship for this column.",
            RelationshipResolution.AmbiguousRelationship => "This column has more than one possible relationship.",
            RelationshipResolution.CompositeRelationship => "This relationship uses multiple columns. Showing the stored value.",
            _ => null,
        };
    }

    public string ColumnName { get; }
    public SqliteValue Value { get; }
    public bool IsDisplayProjection { get; }
    public string Display { get; }
    public string? CombinedLabel { get; }
    public string? RelationshipHelp { get; }

    private static string Format(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null => "NULL",
        SqliteValueKind.Integer => value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => value.RealValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        SqliteValueKind.Blob => $"BLOB · {GetBlobLength(value.BlobBase64):N0} bytes",
        _ => string.Empty,
    };

    private static int GetBlobLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        int padding = base64.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : base64.EndsWith('=')
                ? 1
                : 0;
        return checked((base64.Length / 4 * 3) - padding);
    }
}
