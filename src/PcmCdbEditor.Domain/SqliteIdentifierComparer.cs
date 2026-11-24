namespace PcmCdbEditor.Domain;

/// <summary>SQLite folds ASCII letters when comparing schema identifiers.</summary>
public sealed class SqliteIdentifierComparer : StringComparer
{
    public static SqliteIdentifierComparer Instance { get; } = new();

    private SqliteIdentifierComparer() { }

    public override int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        for (int index = 0; index < Math.Min(x.Length, y.Length); index++)
        {
            int difference = Fold(x[index]) - Fold(y[index]);
            if (difference != 0)
            {
                return difference;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

    public override int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var hash = new HashCode();
        foreach (char character in obj)
        {
            hash.Add(Fold(character));
        }

        return hash.ToHashCode();
    }

    public static string ToLowerAscii(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.AsSpan().ContainsAnyInRange('A', 'Z'))
        {
            return identifier;
        }

        return string.Create(identifier.Length, identifier, static (buffer, name) =>
        {
            for (int index = 0; index < buffer.Length; index++)
            {
                buffer[index] = Fold(name[index]);
            }
        });
    }

    internal static char Fold(char character) =>
        character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
}
