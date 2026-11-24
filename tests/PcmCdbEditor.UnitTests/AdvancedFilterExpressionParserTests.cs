using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class AdvancedFilterExpressionParserTests
{
    [TestMethod]
    public void ParseGivesAndHigherPrecedenceThanOr()
    {
        var result = AdvancedFilterExpressionParser.Parse("1 OR 2 AND 3", Rules(3));

        var root = Assert.IsInstanceOfType<FilterGroup>(result);
        Assert.AreEqual(FilterGroupOperator.Or, root.Operator);
        Assert.AreEqual(2, root.Children.Count);
        var right = Assert.IsInstanceOfType<FilterGroup>(root.Children[1]);
        Assert.AreEqual(FilterGroupOperator.And, right.Operator);
    }

    [TestMethod]
    public void ParseParenthesesOverridePrecedenceAndOperatorsIgnoreCase()
    {
        var result = AdvancedFilterExpressionParser.Parse("(1 or 2) aNd 3", Rules(3));

        var root = Assert.IsInstanceOfType<FilterGroup>(result);
        Assert.AreEqual(FilterGroupOperator.And, root.Operator);
        var left = Assert.IsInstanceOfType<FilterGroup>(root.Children[0]);
        Assert.AreEqual(FilterGroupOperator.Or, left.Operator);
    }

    [TestMethod]
    public void ParseFlattensAdjacentGroupsWithTheSameOperator()
    {
        var result = Assert.IsInstanceOfType<FilterGroup>(AdvancedFilterExpressionParser.Parse("1 AND 2 AND 3", Rules(3)));

        Assert.AreEqual(FilterGroupOperator.And, result.Operator);
        Assert.AreEqual(3, result.Children.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1 AND")]
    [DataRow("AND 1")]
    [DataRow("1 2")]
    [DataRow("1 (2)")]
    [DataRow("(1 OR 2")]
    [DataRow("1 XOR 2")]
    [DataRow("1 + 2")]
    [DataRow("()")]
    public void ParseRejectsInvalidSyntax(string expression)
    {
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse(expression, Rules(2)));
    }

    [TestMethod]
    public void ParseRejectsUndefinedAndDuplicateRuleNumbers()
    {
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse("2", Rules(1)));

        var duplicate = new[]
        {
            Rule(1),
            Rule(1)
        };
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse("1", duplicate));
    }

    [TestMethod]
    public void ParsesTheMaximumNumberOfRulesInOrderAndRejectsAdditionalOccurrences()
    {
        int max = FilterExpressionLimits.MaximumRules;
        var rules = Rules(max);
        string expression = string.Join(" AND ", Enumerable.Range(1, max).Reverse());

        var group = Assert.IsInstanceOfType<FilterGroup>(AdvancedFilterExpressionParser.Parse(expression, rules));
        CollectionAssert.AreEqual(rules.Reverse().Select(static rule => rule.Condition).ToArray(), group.Children.ToArray());
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse(expression + " AND 1", rules));
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse("1", Rules(max + 1)));
    }

    [TestMethod]
    public void RejectsOversizedTextAndDeepTextualOrLogicalNesting()
    {
        var rules = Rules(1);
        string text = new string(' ', FilterExpressionLimits.MaximumTextLength - 1) + "1";
        Assert.AreEqual(rules[0].Condition, AdvancedFilterExpressionParser.Parse(text, rules));
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse(text + " ", rules));

        int maxTextNesting = FilterExpressionLimits.MaximumRules;
        string redundant = new string('(', maxTextNesting) + "1" + new string(')', maxTextNesting);
        Assert.AreEqual(rules[0].Condition, AdvancedFilterExpressionParser.Parse(redundant, rules));
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse($"({redundant})", rules));

        string expression = "1";
        for (int depth = 1; depth < FilterExpressionLimits.MaximumDepth; depth++)
        {
            expression = depth % 2 == 0 ? $"1 AND ({expression})" : $"1 OR ({expression})";
        }

        FilterExpressionLimits.Validate(AdvancedFilterExpressionParser.Parse(expression, rules));
        Assert.ThrowsExactly<FilterParseException>(() => AdvancedFilterExpressionParser.Parse($"1 AND ({expression})", rules));
    }

    private static NumberedFilterRule[] Rules(int count) =>
        Enumerable.Range(1, count).Select(Rule).ToArray();

    private static NumberedFilterRule Rule(int number) =>
        new(number, new FilterCondition($"column{number}", FilterOperator.Equals, SqliteValue.Integer(number)));
}
