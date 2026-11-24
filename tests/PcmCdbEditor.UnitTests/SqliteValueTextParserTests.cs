using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SqliteValueTextParserTests
{
    [TestMethod]
    [DataRow("9007199254740993", 9_007_199_254_740_993L)]
    [DataRow("9223372036854775807", long.MaxValue)]
    [DataRow("-9223372036854775808", long.MinValue)]
    public void IntegerAndNumericInputsKeepAllInt64Digits(string text, long expected)
    {
        Assert.IsTrue(SqliteValueTextParser.TryParseFilterValue(
            SqliteAffinity.Numeric, FilterOperator.Equals, text, out SqliteValue filter));
        Assert.AreEqual(SqliteValue.Integer(expected), filter);
        Assert.IsTrue(SqliteValueTextParser.TryParseNumeric(SqliteAffinity.Integer, text, out SqliteValue value));
        Assert.AreEqual(SqliteValue.Integer(expected), value);
        Assert.AreEqual(expected > 0, SqliteValueTextParser.TryParsePositiveInteger(text, out long wage));
        Assert.AreEqual(expected, wage);
    }

    [TestMethod]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    [DataRow("15.0")]
    [DataRow("3e2")]
    [DataRow("Infinity")]
    [DataRow("1,234")]
    public void IntegerInputsRejectNonIntegralSyntaxAndOverflow(string text)
    {
        Assert.IsFalse(SqliteValueTextParser.TryParseNumeric(SqliteAffinity.Integer, text, out _));
        Assert.IsFalse(SqliteValueTextParser.TryParsePositiveInteger(text, out _));
    }

    [TestMethod]
    public void NumericRealInputRetainsFiniteRealFallbackAndExistingPatternBehavior()
    {
        Assert.IsTrue(SqliteValueTextParser.TryParseNumeric(SqliteAffinity.Numeric, "0.125", out SqliteValue value));
        Assert.AreEqual(SqliteValue.Real(0.125), value);
        Assert.IsFalse(SqliteValueTextParser.TryParseNumeric(SqliteAffinity.Numeric, "9e999", out _));
        Assert.IsTrue(SqliteValueTextParser.TryParseFilterValue(
            SqliteAffinity.Numeric, FilterOperator.Contains, "9007199254740993", out SqliteValue pattern));
        Assert.AreEqual(SqliteValue.Text("9007199254740993"), pattern);
    }
}
