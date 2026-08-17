using System.Collections.ObjectModel;

namespace PcmCdbEditor.Domain;

public enum SqliteAffinity
{
    Integer,
    Real,
    Text,
    Blob,
    Numeric
}

public enum TableObjectKind
{
    Table,
    View
}

public enum TableEditCapability
{
    Editable,
    ReadOnlyView,
    MissingStableIdentity,
    UnsupportedSchema
}

public enum StableIdentityKind
{
    DeclaredPrimaryKey,
    RowIdFallback,
    None
}

public sealed record ColumnSchema(
    int Ordinal,
    string Name,
    string DeclaredType,
    SqliteAffinity Affinity,
    bool IsNullable,
    string? DefaultExpression,
    int PrimaryKeyOrdinal,
    bool IsGenerated,
    bool IsHidden)
{
    public bool IsPrimaryKey => PrimaryKeyOrdinal > 0;
}

public sealed record StableIdentityDefinition
{
    public StableIdentityDefinition(StableIdentityKind kind, IEnumerable<string>? columns = null)
    {
        Kind = kind;
        Columns = ModelCollections.Freeze(columns);

        if (kind == StableIdentityKind.DeclaredPrimaryKey && Columns.Count == 0)
        {
            throw new ArgumentException("A declared primary key identity needs columns.", nameof(columns));
        }

        if (kind == StableIdentityKind.RowIdFallback && Columns.Count != 0)
        {
            throw new ArgumentException("A rowid fallback must not declare key columns.", nameof(columns));
        }
    }

    public StableIdentityKind Kind { get; }

    public IReadOnlyList<string> Columns { get; }
}

public sealed record ForeignKeyRelation(
    string SourceColumn,
    string TargetTable,
    string TargetColumn,
    string? DisplayColumn,
    bool IsDeclared,
    string Confidence);

public sealed class TableSchema
{
    public TableSchema(
        string name,
        TableObjectKind objectKind,
        IEnumerable<ColumnSchema> columns,
        IEnumerable<ForeignKeyRelation>? relationships,
        StableIdentityDefinition stableIdentity,
        TableEditCapability editCapability,
        long? estimatedRowCount,
        bool isWithoutRowId)
    {
        Name = name;
        ObjectKind = objectKind;
        Columns = ModelCollections.Freeze(columns);
        Relationships = ModelCollections.Freeze(relationships);
        StableIdentity = stableIdentity;
        EditCapability = editCapability;
        EstimatedRowCount = estimatedRowCount;
        IsWithoutRowId = isWithoutRowId;
    }

    public string Name { get; }

    public TableObjectKind ObjectKind { get; }

    public IReadOnlyList<ColumnSchema> Columns { get; }

    public IReadOnlyList<ForeignKeyRelation> Relationships { get; }

    public StableIdentityDefinition StableIdentity { get; }

    public TableEditCapability EditCapability { get; }

    public long? EstimatedRowCount { get; }

    public bool IsWithoutRowId { get; }
}

public sealed class DatabaseSchemaCatalog
{
    private readonly ReadOnlyDictionary<string, TableSchema> _tablesByName;

    public DatabaseSchemaCatalog(string schemaSignature, IEnumerable<TableSchema> tables)
    {
        if (string.IsNullOrWhiteSpace(schemaSignature))
        {
            throw new ArgumentException("A schema signature is required.", nameof(schemaSignature));
        }

        SchemaSignature = schemaSignature;
        Tables = ModelCollections.Freeze(tables);
        _tablesByName = ModelCollections.FreezeDictionary(Tables.Select(table => KeyValuePair.Create(table.Name, table)));
    }

    public string SchemaSignature { get; }

    public IReadOnlyList<TableSchema> Tables { get; }

    public bool TryGetTable(string name, out TableSchema table) => _tablesByName.TryGetValue(name, out table!);
}
