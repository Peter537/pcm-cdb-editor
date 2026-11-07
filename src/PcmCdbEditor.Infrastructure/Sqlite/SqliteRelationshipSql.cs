namespace PcmCdbEditor.Infrastructure.Sqlite;

internal static class SqliteRelationshipSql
{
    private static readonly string WhitespaceCharacters = $"char({string.Join(", ",
        Enumerable.Range(char.MinValue, char.MaxValue + 1).Where(value => char.IsWhiteSpace((char)value)))})";

    public static string BuildLabelExpression(string? displayColumn, string? additionalDisplayColumn, string qualifier = "")
    {
        if (displayColumn is null)
        {
            return "NULL";
        }

        string first = $"trim(CAST({qualifier}{SqliteSupport.QuoteIdentifier(displayColumn)} AS TEXT), {WhitespaceCharacters})";
        if (additionalDisplayColumn is not { } additional)
        {
            return $"NULLIF({first}, '')";
        }

        string last = $"trim(CAST({qualifier}{SqliteSupport.QuoteIdentifier(additional)} AS TEXT), {WhitespaceCharacters})";
        return $"NULLIF(trim(COALESCE({first}, '') || ' ' || COALESCE({last}, '')), '')";
    }
}
