using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public static class FilterExpressionLimits
{
    public const int MaximumTextLength = 4096;
    public const int MaximumRules = 128;
    public const int MaximumDepth = 16;
    public const int MaximumNodes = (MaximumRules * 2) - 1;

    public static void Validate(FilterExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var pending = new Stack<(FilterExpression Expression, int Depth)>();
        pending.Push((expression, 1));
        int nodes = 1;
        int rules = 0;
        while (pending.TryPop(out var current))
        {
            EnsureDepth(current.Depth);
            if (current.Expression is FilterCondition)
            {
                if (++rules > MaximumRules)
                {
                    throw TooManyRules();
                }
            }
            else if (current.Expression is FilterGroup group)
            {
                if (group.Children.Count > MaximumNodes - nodes)
                {
                    throw new FilterParseException($"A filter may contain at most {MaximumNodes} conditions and groups in total.");
                }

                nodes += group.Children.Count;
                foreach (FilterExpression child in group.Children)
                {
                    pending.Push((child, current.Depth + 1));
                }
            }
            else
            {
                throw new FilterParseException("Unsupported filter expression type.");
            }
        }
    }

    internal static void EnsureDepth(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new FilterParseException($"A filter may contain at most {MaximumDepth} levels of conditions and groups.");
        }
    }

    internal static FilterParseException TooManyRules() =>
        new($"A filter may contain at most {MaximumRules} rule occurrences.");
}
