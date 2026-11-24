using System.Globalization;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

internal static class SqliteValueTextParser
{
    public static bool TryParseInteger(string text, out long value) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryParsePositiveInteger(string text, out long value) =>
        TryParseInteger(text, out value) && value > 0;

    public static bool TryParseFilterValue(
        SqliteAffinity affinity,
        FilterOperator filterOperator,
        string text,
        out SqliteValue value)
    {
        if (filterOperator is FilterOperator.IsNull or FilterOperator.IsNotNull)
        {
            value = SqliteValue.Null;
            return true;
        }

        if (filterOperator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith ||
            affinity == SqliteAffinity.Text)
        {
            value = SqliteValue.Text(text);
            return true;
        }

        return TryParseNumeric(affinity, text, out value);
    }

    public static bool TryParseNumeric(SqliteAffinity affinity, string text, out SqliteValue value)
    {
        if (affinity is SqliteAffinity.Integer or SqliteAffinity.Numeric && TryParseInteger(text, out long integer))
        {
            value = SqliteValue.Integer(integer);
            return true;
        }

        if (affinity is SqliteAffinity.Real or SqliteAffinity.Numeric &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) &&
            double.IsFinite(real))
        {
            value = SqliteValue.Real(real);
            return true;
        }

        value = SqliteValue.Null;
        return false;
    }
}
