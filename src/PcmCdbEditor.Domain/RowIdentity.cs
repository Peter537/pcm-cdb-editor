using System.Text;

namespace PcmCdbEditor.Domain;

public enum RowIdentityKind
{
    DeclaredPrimaryKey,
    RowId
}

public sealed record RowIdentityComponent
{
    public RowIdentityComponent(string columnName, SqliteValue value)
    {
        if (string.IsNullOrWhiteSpace(columnName) || columnName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An identity column name is required.", nameof(columnName));
        }

        if (value.Kind == SqliteValueKind.Null)
        {
            throw new ArgumentException("Stable identity components cannot be NULL.", nameof(value));
        }

        ColumnName = columnName;
        Value = value;
    }

    public string ColumnName { get; }

    public SqliteValue Value { get; }
}

public sealed class RowIdentity : IEquatable<RowIdentity>
{
    private readonly string _canonicalKey;

    private RowIdentity(RowIdentityKind kind, IEnumerable<RowIdentityComponent> components)
    {
        Kind = kind;
        Components = ModelCollections.Freeze(components);
        if (Components.Count == 0)
        {
            throw new ArgumentException("A row identity needs at least one component.", nameof(components));
        }

        if (Components.Select(component => component.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Components.Count)
        {
            throw new ArgumentException("A row identity cannot repeat a column.", nameof(components));
        }

        _canonicalKey = BuildCanonicalKey();
    }

    public RowIdentityKind Kind { get; }

    public IReadOnlyList<RowIdentityComponent> Components { get; }

    public static RowIdentity FromPrimaryKey(IEnumerable<RowIdentityComponent> components) =>
        new(RowIdentityKind.DeclaredPrimaryKey, components);

    public static RowIdentity FromRowId(long rowId) =>
        new(RowIdentityKind.RowId, [new RowIdentityComponent("rowid", SqliteValue.Integer(rowId))]);

    public bool Equals(RowIdentity? other) =>
        other is not null && string.Equals(_canonicalKey, other._canonicalKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RowIdentity);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_canonicalKey);

    public override string ToString() => _canonicalKey;

    private string BuildCanonicalKey()
    {
        var value = new StringBuilder(Kind.ToString());
        foreach (var component in Components)
        {
            value.Append('|');
            value.Append(component.ColumnName);
            value.Append('=');
            value.Append(component.Value.ToCanonicalText());
        }

        return value.ToString();
    }
}
