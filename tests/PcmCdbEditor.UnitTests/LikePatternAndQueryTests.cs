using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class LikePatternAndQueryTests
{
    [TestMethod]
    public void LikePatternsEscapeWildcardAndEscapeCharactersLiterally()
    {
        Assert.AreEqual(@"50\%\_done\\now", LikePatternEscaper.EscapeLiteral(@"50%_done\now"));
        Assert.AreEqual(@"%50\%\_done%", LikePatternEscaper.ContainsLiteral("50%_done"));
        Assert.AreEqual(@"abc\_%", LikePatternEscaper.StartsWithLiteral("abc_"));
        Assert.AreEqual(@"%\%abc", LikePatternEscaper.EndsWithLiteral("%abc"));
    }

    [TestMethod]
    public void PageRequestEnforcesBoundedQueries()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PageRequest(-1, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PageRequest(0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PageRequest(0, 10_001));

        var request = new PageRequest(1_000_000, 10_000);
        Assert.AreEqual(1_000_000L, request.Offset);
        Assert.AreEqual(10_000, request.Limit);
    }

    [TestMethod]
    public void FilterGroupFreezesItsChildren()
    {
        var source = new List<FilterExpression>
        {
            new FilterCondition("id", FilterOperator.Equals, SqliteValue.Integer(1))
        };
        var group = new FilterGroup(FilterGroupOperator.And, source);
        source.Add(new FilterCondition("id", FilterOperator.Equals, SqliteValue.Integer(2)));

        Assert.HasCount(1, group.Children);
    }

    [TestMethod]
    public void GlobalSearchFreezesEligibleColumns()
    {
        var columns = new List<string> { "name" };
        var search = new GlobalSearchRequest("needle", columns);
        columns.Add("later");

        Assert.HasCount(1, search.EligibleColumns);
        Assert.AreEqual("name", search.EligibleColumns[0]);
    }
}
