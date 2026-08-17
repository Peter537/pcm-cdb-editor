using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Infrastructure.Sqlite;

public sealed class SqliteTableCatalog : ITableCatalog
{
    private static readonly string[] InferredTablePrefixes = ["DYN_", "STA_", "GAM_", "INF_"];
    private readonly Dictionary<string, string> _configuredDisplayColumns;

    public SqliteTableCatalog(IReadOnlyDictionary<string, string>? configuredDisplayColumns = null)
    {
        _configuredDisplayColumns = configuredDisplayColumns is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(configuredDisplayColumns, StringComparer.OrdinalIgnoreCase);
    }

    public Task<DatabaseSchemaCatalog> DiscoverAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => DiscoverCoreAsync(sqlitePath, cancellationToken),
            cancellationToken);

    private async Task<DatabaseSchemaCatalog> DiscoverCoreAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);

        var objects = await ReadObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        var mutable = new List<TableBuilder>(objects.Count);
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = await ReadColumnsAsync(connection, item.Name, cancellationToken).ConfigureAwait(false);
            var declaredRelationships = await ReadDeclaredForeignKeysAsync(
                    connection,
                    item.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            var identity = ResolveIdentity(item, columns);
            var editCapability = item.Kind == TableObjectKind.View
                ? TableEditCapability.ReadOnlyView
                : item.IsVirtual
                    ? TableEditCapability.UnsupportedSchema
                : identity.Kind == StableIdentityKind.None
                    ? TableEditCapability.MissingStableIdentity
                    : TableEditCapability.Editable;
            mutable.Add(new TableBuilder(item, columns, declaredRelationships, identity, editCapability));
        }

        var tablesByName = mutable.ToDictionary(static item => item.Object.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var table in mutable)
        {
            ResolveDeclaredRelationships(table, tablesByName);
            AddInferredRelationships(table, tablesByName);
        }

        var tables = mutable
            .Select(static item => new TableSchema(
                item.Object.Name,
                item.Object.Kind,
                item.Columns,
                item.Relationships,
                item.Identity,
                item.EditCapability,
                estimatedRowCount: null,
                item.Object.IsWithoutRowId))
            .ToArray();
        return new DatabaseSchemaCatalog(ComputeSignature(tables), tables);
    }

    private static async Task<List<SchemaObject>> ReadObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var objects = new List<SchemaObject>();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT name, type, COALESCE(sql, '')
FROM sqlite_schema
WHERE type IN ('table', 'view')
  AND name NOT LIKE 'sqlite_%'
ORDER BY name COLLATE NOCASE, name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var kind = reader.GetString(1).Equals("view", StringComparison.OrdinalIgnoreCase)
                ? TableObjectKind.View
                : TableObjectKind.Table;
            var sql = reader.GetString(2);
            objects.Add(new SchemaObject(
                name,
                kind,
                kind == TableObjectKind.Table
                && sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase),
                kind == TableObjectKind.Table
                && sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)));
        }

        return objects;
    }

    private static async Task<List<ColumnSchema>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<ColumnSchema>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo({SqliteSupport.QuoteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var declaredType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var primaryKeyOrdinal = reader.GetInt32(5);
            var hiddenValue = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;
            columns.Add(new ColumnSchema(
                reader.GetInt32(0),
                reader.GetString(1),
                declaredType,
                ResolveAffinity(declaredType),
                reader.GetInt32(3) == 0 && primaryKeyOrdinal == 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                primaryKeyOrdinal,
                IsGenerated: hiddenValue is 2 or 3,
                IsHidden: hiddenValue == 1));
        }

        return columns;
    }

    private static async Task<List<ForeignKeyRelation>> ReadDeclaredForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var relationships = new List<ForeignKeyRelation>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({SqliteSupport.QuoteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                continue;
            }

            relationships.Add(new ForeignKeyRelation(
                reader.GetString(3),
                reader.GetString(2),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                DisplayColumn: null,
                IsDeclared: true,
                Confidence: "declared"));
        }

        return relationships;
    }

    private static StableIdentityDefinition ResolveIdentity(
        SchemaObject schemaObject,
        IReadOnlyList<ColumnSchema> columns)
    {
        if (schemaObject.Kind == TableObjectKind.View || schemaObject.IsVirtual)
        {
            return new StableIdentityDefinition(StableIdentityKind.None);
        }

        var primaryKey = columns
            .Where(static column => column.PrimaryKeyOrdinal > 0)
            .OrderBy(static column => column.PrimaryKeyOrdinal)
            .Select(static column => column.Name)
            .ToArray();
        if (primaryKey.Length > 0)
        {
            return new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, primaryKey);
        }

        // SQLite's hidden rowid is addressable through the name "rowid" only when the
        // table has not declared a real column with that name. A declared rowid column
        // shadows the hidden value and can contain duplicate or NULL values, so treating
        // it as an identity could update or delete more than one physical row.
        bool hasShadowingRowIdColumn = columns.Any(static column =>
            column.Name.Equals("rowid", StringComparison.OrdinalIgnoreCase));
        if (schemaObject.Kind == TableObjectKind.Table
            && !schemaObject.IsWithoutRowId
            && !hasShadowingRowIdColumn)
        {
            return new StableIdentityDefinition(StableIdentityKind.RowIdFallback);
        }

        return new StableIdentityDefinition(StableIdentityKind.None);
    }

    private void ResolveDeclaredRelationships(
        TableBuilder source,
        IReadOnlyDictionary<string, TableBuilder> tablesByName)
    {
        for (var index = 0; index < source.Relationships.Count; index++)
        {
            var relationship = source.Relationships[index];
            if (!tablesByName.TryGetValue(relationship.TargetTable, out var target))
            {
                continue;
            }

            var targetColumn = relationship.TargetColumn;
            if (string.IsNullOrWhiteSpace(targetColumn))
            {
                var primaryKey = target.Columns
                    .Where(static column => column.PrimaryKeyOrdinal > 0)
                    .OrderBy(static column => column.PrimaryKeyOrdinal)
                    .ToArray();
                if (primaryKey.Length != 1)
                {
                    continue;
                }

                targetColumn = primaryKey[0].Name;
            }

            if (!target.Columns.Any(column => column.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            source.Relationships[index] = relationship with
            {
                TargetColumn = targetColumn,
                DisplayColumn = ResolveDisplayColumn(target.Object.Name, target.Columns)
            };
        }
    }

    private void AddInferredRelationships(
        TableBuilder source,
        IReadOnlyDictionary<string, TableBuilder> tablesByName)
    {
        var declaredColumns = source.Relationships
            .Select(static relationship => relationship.SourceColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in source.Columns)
        {
            if (declaredColumns.Contains(column.Name)
                || !column.Name.StartsWith("fkID", StringComparison.OrdinalIgnoreCase)
                || column.Name.Length <= 4)
            {
                continue;
            }

            var suffix = column.Name[4..];
            if (source.Object.Name.Equals("DYN_result_season", StringComparison.OrdinalIgnoreCase)
                && column.Name.Equals("fkIDresult_season_team", StringComparison.OrdinalIgnoreCase)
                && tablesByName.TryGetValue("DYN_result_season_team", out var configuredTarget))
            {
                var configuredColumn = configuredTarget.Columns.FirstOrDefault(candidate =>
                    candidate.Name.Equals("IDresult_season_team", StringComparison.OrdinalIgnoreCase));
                if (configuredColumn is not null)
                {
                    source.Relationships.Add(new ForeignKeyRelation(
                        column.Name,
                        configuredTarget.Object.Name,
                        configuredColumn.Name,
                        ResolveDisplayColumn(configuredTarget.Object.Name, configuredTarget.Columns),
                        IsDeclared: false,
                        Confidence: "configured"));
                    continue;
                }
            }

            var candidates = tablesByName.Values
                .Where(candidate => InferredTablePrefixes.Any(prefix =>
                    candidate.Object.Name.Equals($"{prefix}{suffix}", StringComparison.OrdinalIgnoreCase)))
                .Select(candidate => (Table: candidate, Target: candidate.Columns.FirstOrDefault(target =>
                    target.Name.Equals($"ID{suffix}", StringComparison.OrdinalIgnoreCase))))
                .Where(static candidate => candidate.Target is not null)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            source.Relationships.Add(new ForeignKeyRelation(
                column.Name,
                candidates[0].Table.Object.Name,
                candidates[0].Target!.Name,
                ResolveDisplayColumn(candidates[0].Table.Object.Name, candidates[0].Table.Columns),
                IsDeclared: false,
                Confidence: "conservative-name-match"));
        }
    }

    private string? ResolveDisplayColumn(string tableName, IReadOnlyList<ColumnSchema> columns)
    {
        if (_configuredDisplayColumns.TryGetValue(tableName, out var configured))
        {
            var configuredMatch = columns.FirstOrDefault(column =>
                column.Name.Equals(configured, StringComparison.OrdinalIgnoreCase));
            if (configuredMatch is not null)
            {
                return configuredMatch.Name;
            }
        }

        foreach (var candidate in new[]
                 {
                     "gene_sz_name",
                     "gene_sz_full_name",
                     "gene_sz_firstname",
                     "gene_sz_lastname",
                     "value_sz_name",
                     "name",
                     "gene_sz_code",
                     "CONSTANT"
                 })
        {
            var match = columns.FirstOrDefault(column => column.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Name;
            }
        }

        return columns.FirstOrDefault(static column => column.Affinity == SqliteAffinity.Text)?.Name;
    }

    private static SqliteAffinity ResolveAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return SqliteAffinity.Integer;
        }

        if (type.Contains("CHAR", StringComparison.Ordinal)
            || type.Contains("CLOB", StringComparison.Ordinal)
            || type.Contains("TEXT", StringComparison.Ordinal))
        {
            return SqliteAffinity.Text;
        }

        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0)
        {
            return SqliteAffinity.Blob;
        }

        if (type.Contains("REAL", StringComparison.Ordinal)
            || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal))
        {
            return SqliteAffinity.Real;
        }

        return SqliteAffinity.Numeric;
    }

    private static string ComputeSignature(IEnumerable<TableSchema> tables)
    {
        var builder = new StringBuilder();
        foreach (var table in tables.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(table.Name).Append('|').Append(table.ObjectKind).Append('|').Append(table.IsWithoutRowId);
            foreach (var column in table.Columns.OrderBy(static item => item.Ordinal))
            {
                builder.Append(';').Append(column.Ordinal).Append(':').Append(column.Name).Append(':')
                    .Append(column.DeclaredType).Append(':').Append(column.PrimaryKeyOrdinal).Append(':')
                    .Append(column.IsGenerated).Append(':').Append(column.IsHidden);
            }

            builder.AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed record SchemaObject(
        string Name,
        TableObjectKind Kind,
        bool IsWithoutRowId,
        bool IsVirtual);

    private sealed class TableBuilder(
        SchemaObject schemaObject,
        List<ColumnSchema> columns,
        List<ForeignKeyRelation> relationships,
        StableIdentityDefinition identity,
        TableEditCapability editCapability)
    {
        public SchemaObject Object { get; } = schemaObject;

        public List<ColumnSchema> Columns { get; } = columns;

        public List<ForeignKeyRelation> Relationships { get; } = relationships;

        public StableIdentityDefinition Identity { get; } = identity;

        public TableEditCapability EditCapability { get; } = editCapability;
    }
}
