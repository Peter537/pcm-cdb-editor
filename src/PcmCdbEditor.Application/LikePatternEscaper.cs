using System.Text;

namespace PcmCdbEditor.Application;

public static class LikePatternEscaper
{
    public const char EscapeCharacter = '\\';

    public static string EscapeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is EscapeCharacter or '%' or '_')
            {
                result.Append(EscapeCharacter);
            }

            result.Append(character);
        }

        return result.ToString();
    }

    public static string ContainsLiteral(string value) => $"%{EscapeLiteral(value)}%";

    public static string StartsWithLiteral(string value) => $"{EscapeLiteral(value)}%";

    public static string EndsWithLiteral(string value) => $"%{EscapeLiteral(value)}";
}
