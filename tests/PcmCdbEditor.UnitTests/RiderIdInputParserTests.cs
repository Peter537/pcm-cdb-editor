using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class RiderIdInputParserTests
{
    [TestMethod]
    public void AcceptsAllDocumentedSeparatorsAndNormalizesDuplicates()
    {
        RiderIdParseResult result = RiderIdInputParser.Parse(" 9, 3;9\n7\t3 ");

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(new long[] { 3, 7, 9 }, result.RiderIds.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("1.5")]
    [DataRow("1 rider")]
    public void ReturnsFieldSpecificErrorsForInvalidInput(string input)
    {
        RiderIdParseResult result = RiderIdInputParser.Parse(input, "Recovery rider IDs");

        Assert.IsFalse(result.IsValid);
        StringAssert.StartsWith(result.Error, "Recovery rider IDs:", StringComparison.Ordinal);
        Assert.HasCount(0, result.RiderIds);
    }
}
