namespace PcmCdbEditor.Application;

public sealed record RiderIdParseResult(IReadOnlyList<long> RiderIds, string? Error)
{
    public bool IsValid => Error is null;
}

public static class RiderIdInputParser
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static RiderIdParseResult Parse(string? text, string fieldName = "Rider IDs")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        string[] tokens = (text ?? string.Empty).Split(
            Separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return new RiderIdParseResult([], $"{fieldName}: enter at least one positive rider ID.");
        }

        var ids = new SortedSet<long>();
        foreach (string token in tokens)
        {
            if (!long.TryParse(
                    token,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long id) ||
                id <= 0)
            {
                return new RiderIdParseResult(
                    [],
                    $"{fieldName}: '{token}' is not a positive whole-number rider ID.");
            }

            ids.Add(id);
        }

        return new RiderIdParseResult(ids.ToArray(), null);
    }
}
