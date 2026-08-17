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

    private static NumberedFilterRule[] Rules(int count) =>
        Enumerable.Range(1, count).Select(Rule).ToArray();

    private static NumberedFilterRule Rule(int number) =>
        new(number, new FilterCondition($"column{number}", FilterOperator.Equals, SqliteValue.Integer(number)));
}
