using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Sqlite;

public sealed class SqliteRelationshipLookup : IRelationshipLookup
{
    public Task<RelationshipLookupPage> SearchAsync(
        string sqlitePath, DatabaseSchemaCatalog catalog, string sourceTable, string sourceColumn,
        string? search, int limit, CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => SearchCoreAsync(sqlitePath, catalog, sourceTable, sourceColumn, search, limit, cancellationToken),
            cancellationToken);

    public Task<RelationshipLookupResult> ResolveAsync(
        string sqlitePath, DatabaseSchemaCatalog catalog, string sourceTable, string sourceColumn,
        SqliteValue rawKey, CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(async () =>
        {
            await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
            using var registration = await SqliteOperationRunner.OpenInterruptiblyAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return await ResolveCoreAsync(connection, null, catalog, sourceTable, sourceColumn, rawKey, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<RelationshipLookupPage> SearchCoreAsync(
        string sqlitePath, DatabaseSchemaCatalog catalog, string sourceTable, string sourceColumn,
        string? search, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Relationship search limits must be between 1 and 200.");
        }

        LookupTarget? target = GetTarget(catalog, sourceTable, sourceColumn, out var unavailable);
        if (target is null)
        {
            return new RelationshipLookupPage([], false, unavailable!.UnavailableReason);
        }

        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var registration = await SqliteOperationRunner.OpenInterruptiblyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        string key = TargetExpression(target.KeyColumn);
        var conditions = new List<string>
        {
            "__pcm_counts.__pcm_count = 1",
            $"typeof({key}) IN ('integer', 'real', 'text')",
            $"(typeof({key}) <> 'real' OR abs({key}) <= 1.7976931348623157e308)"
        };
        conditions.AddRange(target.Table.StableIdentity.Columns.Select(name => $"{TargetExpression(name)} IS NOT NULL"));
        if (!string.IsNullOrWhiteSpace(search))
        {
            command.Parameters.AddWithValue("$search", $"%{EscapeLike(search)}%");
            IEnumerable<string> searchable = new[] { key, target.LabelExpression }
                .Concat(target.ContextColumns.Select(column => TargetExpression(column.Name)));
            conditions.Add("(" + string.Join(" OR ", searchable.Select(expression =>
                $"(typeof({expression}) <> 'blob' AND CAST({expression} AS TEXT) LIKE $search ESCAPE '\\')")) + ")");
        }

        string identityOrder = target.Table.StableIdentity.Kind == StableIdentityKind.RowIdFallback
            ? "__pcm_target.rowid"
            : string.Join(", ", target.Table.StableIdentity.Columns.Select(TargetExpression));
        // Compute cardinality before searching labels: two targets with the same key
        // remain ambiguous even when only one of their names matches the search.
        command.CommandText = $"WITH __pcm_counts AS MATERIALIZED (SELECT {Quote(target.KeyColumn)} AS __pcm_key, COUNT(*) AS __pcm_count " +
            $"FROM main.{Quote(target.Table.Name)} GROUP BY {Quote(target.KeyColumn)}) " +
            $"SELECT {target.SelectColumns}, {target.LabelExpression} AS __pcm_label FROM main.{Quote(target.Table.Name)} AS __pcm_target " +
            $"JOIN __pcm_counts ON {key} = __pcm_counts.__pcm_key WHERE {string.Join(" AND ", conditions)} " +
            $"ORDER BY __pcm_label COLLATE NOCASE, {key}, typeof({key}), {identityOrder} LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit + 1);
        var options = new List<RelationshipLookupOption>(limit);
        bool hasMore = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Count == limit)
            {
                hasMore = true;
                break;
            }

            options.Add(target.ReadOption(reader)!);
        }

        return new RelationshipLookupPage(options, hasMore,
            options.Count == 0 ? "No selectable matches. Choices require a unique non-NULL, non-BLOB key and a stable target row." : null);
    }

    internal static async Task<RelationshipLookupResult> ResolveCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, DatabaseSchemaCatalog catalog,
        string sourceTable, string sourceColumn, SqliteValue rawKey, CancellationToken cancellationToken)
    {
        LookupTarget? target = GetTarget(catalog, sourceTable, sourceColumn, out var unavailable);
        if (target is null)
        {
            return unavailable!;
        }

        if (rawKey.Kind == SqliteValueKind.Null)
        {
            return new(RelationshipResolution.MissingTarget, UnavailableReason: "NULL does not reference a record.");
        }

        if (rawKey.Kind == SqliteValueKind.Blob || (rawKey.Kind == SqliteValueKind.Real && !double.IsFinite(rawKey.RealValue)))
        {
            return new(RelationshipResolution.TargetUnavailable,
                UnavailableReason: "BLOB and non-finite keys cannot be chosen or opened by the relationship editor. The stored value is preserved.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The parent key remains the left operand so SQLite applies its affinity and collation.
        command.CommandText = $"SELECT {target.SelectColumns}, {target.LabelExpression} AS __pcm_label " +
            $"FROM main.{Quote(target.Table.Name)} AS __pcm_target WHERE {TargetExpression(target.KeyColumn)} = $key LIMIT 2";
        command.Parameters.AddWithValue("$key", rawKey.ToClrValue() ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(RelationshipResolution.MissingTarget, UnavailableReason: "The referenced record could not be found for this stored key.");
        }

        RelationshipLookupOption? option = target.ReadOption(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(RelationshipResolution.AmbiguousKey, UnavailableReason: "More than one record matches this stored key.");
        }

        return option is null
            ? new(RelationshipResolution.TargetUnavailable, UnavailableReason: "The referenced record has no stable row identity.")
            : new(option.Label is null ? RelationshipResolution.LabelUnavailable : RelationshipResolution.Resolved, option);
    }

    internal static async Task ValidateChoicesAsync(
        SqliteConnection connection, SqliteTransaction transaction, DatabaseSchemaCatalog catalog, string sourceTable,
        IReadOnlyDictionary<string, SqliteValue> values,
        IReadOnlyDictionary<string, RelationshipLookupOption> choices,
        bool afterWrite, CancellationToken cancellationToken)
    {
        foreach (var (column, choice) in choices)
        {
            if (!values.TryGetValue(column, out SqliteValue key) || (!afterWrite && key != choice.Key))
            {
                throw new UnsafeRowMutationException("The chosen relationship no longer matches the field value. Choose the record again or enter a raw value.", column);
            }

            RelationshipLookupResult resolved = await ResolveCoreAsync(
                connection, transaction, catalog, sourceTable, column, key, cancellationToken).ConfigureAwait(false);
            RelationshipLookupOption? current = resolved.Option;
            if (current is null
                || !current.TargetTable.Equals(choice.TargetTable, StringComparison.OrdinalIgnoreCase)
                || !current.TargetColumn.Equals(choice.TargetColumn, StringComparison.OrdinalIgnoreCase)
                || !current.TargetIdentity.Equals(choice.TargetIdentity)
                || (!afterWrite && current.TargetRevision != choice.TargetRevision))
            {
                string explanation = afterWrite
                    ? "SQLite converted the stored key so it no longer identifies the chosen record."
                    : resolved.UnavailableReason ?? "The chosen record changed since it was selected.";
                throw new UnsafeRowMutationException($"{explanation} Choose the record again or enter a raw value.", column);
            }
        }
    }

    private static LookupTarget? GetTarget(
        DatabaseSchemaCatalog catalog, string sourceTable, string sourceColumn,
        out RelationshipLookupResult? unavailable)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGetTable(sourceTable, out TableSchema table))
        {
            throw new ArgumentException("The source table is not present in the discovered schema.", nameof(sourceTable));
        }

        if (!RelationshipMetadata.TryGetLookupRelation(catalog, table, sourceColumn, out var relation, out string? reason))
        {
            ForeignKeyRelation[] matches = table.Relationships.Where(candidate =>
                candidate.SourceColumn.Equals(sourceColumn, StringComparison.OrdinalIgnoreCase)).ToArray();
            RelationshipResolution resolution = matches.Length switch
            {
                0 => RelationshipResolution.UnknownRelationship,
                > 1 => RelationshipResolution.AmbiguousRelationship,
                _ when matches[0].IsComposite => RelationshipResolution.CompositeRelationship,
                _ => RelationshipResolution.TargetUnavailable
            };
            unavailable = new(resolution, UnavailableReason: reason);
            return null;
        }

        unavailable = null;
        catalog.TryGetTable(relation.TargetTable, out TableSchema targetTable);
        bool hasLabel = RelationshipMetadata.TryGetLabelRelation(catalog, table, sourceColumn, out _);
        return new LookupTarget(targetTable, relation.TargetColumn,
            hasLabel ? relation.DisplayColumn : null, hasLabel ? relation.AdditionalDisplayColumn : null);
    }

    private static string Quote(string name) => SqliteSupport.QuoteIdentifier(name);

    private static string TargetExpression(string columnName) => $"__pcm_target.{Quote(columnName)}";

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class LookupTarget
    {
        private readonly ColumnSchema[] _columns;

        public LookupTarget(TableSchema table, string keyColumn, string? displayColumn, string? additionalDisplayColumn)
        {
            Table = table;
            _columns = table.Columns.Where(static column => !column.IsHidden).ToArray();
            KeyColumn = _columns.Single(column => column.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase)).Name;
            LabelExpression = SqliteRelationshipSql.BuildLabelExpression(displayColumn, additionalDisplayColumn, "__pcm_target.");
            ContextColumns = _columns.Where(column => column.Name != KeyColumn && !column.IsPrimaryKey
                && !column.Name.Equals(displayColumn, StringComparison.OrdinalIgnoreCase)
                && !column.Name.Equals(additionalDisplayColumn, StringComparison.OrdinalIgnoreCase)
                && column.Affinity != SqliteAffinity.Blob).Take(3).ToArray();
            SelectColumns = string.Join(", ", _columns.Select((column, index) => $"{TargetExpression(column.Name)} AS __pcm_value{index}"));
            if (table.StableIdentity.Kind == StableIdentityKind.RowIdFallback)
            {
                SelectColumns += ", __pcm_target.rowid AS __pcm_rowid";
            }
        }

        public TableSchema Table { get; }

        public string KeyColumn { get; }

        public string SelectColumns { get; }

        public string LabelExpression { get; }

        public ColumnSchema[] ContextColumns { get; }

        public RelationshipLookupOption? ReadOption(SqliteDataReader reader)
        {
            var values = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < _columns.Length; index++)
            {
                values[_columns[index].Name] = ReadValue(reader, index);
            }

            RowIdentity? identity = Table.StableIdentity.Kind switch
            {
                StableIdentityKind.RowIdFallback => RowIdentity.FromRowId(reader.GetInt64(_columns.Length)),
                StableIdentityKind.DeclaredPrimaryKey when Table.StableIdentity.Columns.All(name => values[name].Kind != SqliteValueKind.Null)
                    => RowIdentity.FromPrimaryKey(Table.StableIdentity.Columns.Select(name => new RowIdentityComponent(name, values[name]))),
                _ => null
            };
            if (identity is null)
            {
                return null;
            }

            int labelOrdinal = _columns.Length + (Table.StableIdentity.Kind == StableIdentityKind.RowIdFallback ? 1 : 0);
            string? label = reader.IsDBNull(labelOrdinal) ? null : reader.GetString(labelOrdinal);
            IEnumerable<string> context = identity.Components.Select(component => $"{component.ColumnName}: {Format(component.Value)}")
                .Concat(ContextColumns.Select(column => $"{column.Name}: {Format(values[column.Name])}"));
            return new(values[KeyColumn], label, string.Join(" · ", context), Table.Name, KeyColumn, identity, RowRevision.Compute(values));
        }

        private static string Format(SqliteValue value)
        {
            string text = value.Kind == SqliteValueKind.Blob ? "BLOB" : value.Kind == SqliteValueKind.Null ? "NULL"
                : Convert.ToString(value.ToClrValue(), CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length <= 100 ? text : text[..100] + "…";
        }

        private static SqliteValue ReadValue(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
        {
            DBNull => SqliteValue.Null,
            long value => SqliteValue.Integer(value),
            double value => SqliteValue.Real(value),
            string value => SqliteValue.Text(value),
            byte[] value => SqliteValue.Blob(value),
            _ => throw new InvalidDataException("The referenced row contains an unsupported SQLite value.")
        };
    }
}
