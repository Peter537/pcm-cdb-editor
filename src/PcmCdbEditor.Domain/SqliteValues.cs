using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PcmCdbEditor.Domain;

public enum SqliteValueKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob
}

public readonly record struct SqliteValue
{
    private SqliteValue(
        SqliteValueKind kind,
        long integerValue = default,
        double realValue = default,
        string? textValue = null,
        string? blobBase64 = null)
    {
        Kind = kind;
        IntegerValue = integerValue;
        RealValue = realValue;
        TextValue = textValue;
        BlobBase64 = blobBase64;
    }

    public SqliteValueKind Kind { get; }

    public long IntegerValue { get; }

    public double RealValue { get; }

    public string? TextValue { get; }

    public string? BlobBase64 { get; }

    public static SqliteValue Null => default;

    public static SqliteValue Integer(long value) => new(SqliteValueKind.Integer, integerValue: value);

    public static SqliteValue Real(double value) => new(SqliteValueKind.Real, realValue: value);

    public static SqliteValue Text(string value) =>
        new(SqliteValueKind.Text, textValue: value ?? throw new ArgumentNullException(nameof(value)));

    public static SqliteValue Blob(ReadOnlySpan<byte> value) =>
        new(SqliteValueKind.Blob, blobBase64: Convert.ToBase64String(value));

    public byte[] GetBlobBytes()
    {
        if (Kind != SqliteValueKind.Blob)
        {
            throw new InvalidOperationException("The SQLite value is not a blob.");
        }

        return Convert.FromBase64String(BlobBase64 ?? string.Empty);
    }

    public object? ToClrValue()
    {
        return Kind switch
        {
            SqliteValueKind.Null => null,
            SqliteValueKind.Integer => IntegerValue,
            SqliteValueKind.Real => RealValue,
            SqliteValueKind.Text => TextValue,
            SqliteValueKind.Blob => GetBlobBytes(),
            _ => throw new InvalidOperationException($"Unsupported SQLite value kind '{Kind}'.")
        };
    }

    internal string ToCanonicalText()
    {
        return Kind switch
        {
            SqliteValueKind.Null => "N",
            SqliteValueKind.Integer => $"I{IntegerValue.ToString(CultureInfo.InvariantCulture)}",
            SqliteValueKind.Real => $"R{BitConverter.DoubleToInt64Bits(RealValue):X16}",
            SqliteValueKind.Text => $"T{Convert.ToBase64String(Encoding.UTF8.GetBytes(TextValue ?? string.Empty))}",
            SqliteValueKind.Blob => $"B{BlobBase64}",
            _ => throw new InvalidOperationException($"Unsupported SQLite value kind '{Kind}'.")
        };
    }
}

public readonly record struct RowRevision
{
    public RowRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A row revision cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static RowRevision Compute(IEnumerable<KeyValuePair<string, SqliteValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var canonical = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Column names must be non-empty SQLite identifiers.", nameof(values));
            }

            canonical.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)));
            canonical.Append(':');
            canonical.Append(pair.Value.ToCanonicalText());
            canonical.Append(';');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new RowRevision(Convert.ToHexString(digest));
    }

    public override string ToString() => Value;
}

public sealed class TypedRow
{
    public TypedRow(RowIdentity? identity, IEnumerable<KeyValuePair<string, SqliteValue>> values)
    {
        Identity = identity;
        Values = ModelCollections.FreezeDictionary(values);
        Revision = RowRevision.Compute(Values);
    }

    /// <summary>
    /// Gets the verified database identity, or <see langword="null"/> for a
    /// read-only row from a view or other object without a safe identity.
    /// </summary>
    public RowIdentity? Identity { get; }

    public IReadOnlyDictionary<string, SqliteValue> Values { get; }

    public RowRevision Revision { get; }
}
