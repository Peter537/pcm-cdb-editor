using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class FilterParseException : FormatException
{
    public FilterParseException(string message)
        : base(message)
    {
    }
}

public static class AdvancedFilterExpressionParser
{
    public static FilterExpression Parse(string expression, IEnumerable<NumberedFilterRule> rules)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FilterParseException("An advanced filter expression is required.");
        }

        ArgumentNullException.ThrowIfNull(rules);

        var ruleMap = new Dictionary<int, FilterCondition>();
        foreach (var rule in rules)
        {
            if (rule.Number <= 0)
            {
                throw new FilterParseException("Rule numbers must be positive integers.");
            }

            if (!ruleMap.TryAdd(rule.Number, rule.Condition))
            {
                throw new FilterParseException($"Rule {rule.Number} is defined more than once.");
            }
        }

        if (ruleMap.Count == 0)
        {
            throw new FilterParseException("At least one filter rule is required.");
        }

        var values = new Stack<FilterExpression>();
        var operators = new Stack<Token>();
        var expectingOperand = true;

        foreach (var token in Tokenize(expression))
        {
            switch (token.Kind)
            {
                case TokenKind.Rule:
                    if (!expectingOperand)
                    {
                        throw new FilterParseException($"An operator is required before rule {token.RuleNumber}.");
                    }

                    if (!ruleMap.TryGetValue(token.RuleNumber, out var condition))
                    {
                        throw new FilterParseException($"Expression references undefined rule {token.RuleNumber}.");
                    }

                    values.Push(condition);
                    expectingOperand = false;
                    break;

                case TokenKind.LeftParenthesis:
                    if (!expectingOperand)
                    {
                        throw new FilterParseException("An operator is required before '('.");
                    }

                    operators.Push(token);
                    break;

                case TokenKind.RightParenthesis:
                    if (expectingOperand)
                    {
                        throw new FilterParseException("A rule is required before ')'.");
                    }

                    while (operators.Count > 0 && operators.Peek().Kind != TokenKind.LeftParenthesis)
                    {
                        ApplyOperator(values, operators.Pop());
                    }

                    if (operators.Count == 0)
                    {
                        throw new FilterParseException("Advanced filter parentheses are mismatched.");
                    }

                    operators.Pop();
                    expectingOperand = false;
                    break;

                case TokenKind.And:
                case TokenKind.Or:
                    if (expectingOperand)
                    {
                        throw new FilterParseException($"A rule is required before '{token.Text}'.");
                    }

                    while (operators.Count > 0
                           && operators.Peek().Kind != TokenKind.LeftParenthesis
                           && Precedence(operators.Peek()) >= Precedence(token))
                    {
                        ApplyOperator(values, operators.Pop());
                    }

                    operators.Push(token);
                    expectingOperand = true;
                    break;

                default:
                    throw new FilterParseException($"Unsupported token '{token.Text}'.");
            }
        }

        if (expectingOperand)
        {
            throw new FilterParseException("The advanced filter expression ends before a rule.");
        }

        while (operators.Count > 0)
        {
            var current = operators.Pop();
            if (current.Kind == TokenKind.LeftParenthesis)
            {
                throw new FilterParseException("Advanced filter parentheses are mismatched.");
            }

            ApplyOperator(values, current);
        }

        if (values.Count != 1)
        {
            throw new FilterParseException("The advanced filter expression is invalid.");
        }

        return values.Pop();
    }

    private static IEnumerable<Token> Tokenize(string expression)
    {
        for (var index = 0; index < expression.Length;)
        {
            var current = expression[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '(')
            {
                yield return new Token(TokenKind.LeftParenthesis, "(");
                index++;
                continue;
            }

            if (current == ')')
            {
                yield return new Token(TokenKind.RightParenthesis, ")");
                index++;
                continue;
            }

            if (char.IsAsciiDigit(current))
            {
                var start = index;
                while (index < expression.Length && char.IsAsciiDigit(expression[index]))
                {
                    index++;
                }

                var text = expression[start..index];
                if (!int.TryParse(text, out var ruleNumber) || ruleNumber <= 0)
                {
                    throw new FilterParseException($"Invalid rule number '{text}'.");
                }

                yield return new Token(TokenKind.Rule, text, ruleNumber);
                continue;
            }

            if (char.IsAsciiLetter(current))
            {
                var start = index;
                while (index < expression.Length && char.IsAsciiLetter(expression[index]))
                {
                    index++;
                }

                var text = expression[start..index];
                if (text.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Token(TokenKind.And, text);
                }
                else if (text.Equals("OR", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Token(TokenKind.Or, text);
                }
                else
                {
                    throw new FilterParseException($"Unsupported token '{text}'.");
                }

                continue;
            }

            throw new FilterParseException($"Unsupported character '{current}' at position {index + 1}.");
        }
    }

    private static int Precedence(Token token) => token.Kind == TokenKind.And ? 2 : 1;

    private static void ApplyOperator(Stack<FilterExpression> values, Token token)
    {
        if (values.Count < 2)
        {
            throw new FilterParseException($"Operator '{token.Text}' is missing a rule.");
        }

        var right = values.Pop();
        var left = values.Pop();
        var groupOperator = token.Kind == TokenKind.And ? FilterGroupOperator.And : FilterGroupOperator.Or;
        var children = new List<FilterExpression>();
        AddFlattened(children, left, groupOperator);
        AddFlattened(children, right, groupOperator);
        values.Push(new FilterGroup(groupOperator, children));
    }

    private static void AddFlattened(
        List<FilterExpression> destination,
        FilterExpression expression,
        FilterGroupOperator parentOperator)
    {
        if (expression is FilterGroup group && group.Operator == parentOperator)
        {
            foreach (var child in group.Children)
            {
                destination.Add(child);
            }

            return;
        }

        destination.Add(expression);
    }

    private enum TokenKind
    {
        Rule,
        And,
        Or,
        LeftParenthesis,
        RightParenthesis
    }

    private readonly record struct Token(TokenKind Kind, string Text, int RuleNumber = 0);
}
