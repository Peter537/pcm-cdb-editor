using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.App;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class TableRowDisplayOptionsTests
{
    private static readonly int[] ExpectedFinitePageSizes = [100, 250, 500];
    private static readonly string[] ExpectedPickerLabels =
        ["100 rows", "250 rows", "500 rows", "All rows"];
    private static readonly string[] ExpectedStatusLabels =
        ["100 rows/page", "250 rows/page", "500 rows/page", "All rows"];
    private static readonly int?[] ExpectedPageSizes = [100, 250, 500, null];

    [TestMethod]
    public void FinitePreferencesAndToolbarChoicesHaveOneCanonicalOrder()
    {
        CollectionAssert.AreEqual(
            ExpectedFinitePageSizes,
            TableRowDisplayOptions.FinitePageSizes.ToArray());

        TableRowDisplayChoice[] choices = TableRowDisplayOptions.ToolbarChoices.ToArray();
        CollectionAssert.AreEqual(
            ExpectedPickerLabels,
            choices.Select(static choice => choice.PickerLabel).ToArray());
        CollectionAssert.AreEqual(
            ExpectedStatusLabels,
            choices.Select(static choice => choice.StatusLabel).ToArray());
        CollectionAssert.AreEqual(
            ExpectedPageSizes,
            choices.Select(static choice => choice.PageSize).ToArray());
    }

    [TestMethod]
    public void ToolbarIndexesRoundTripWithoutEncodingPageSizesInUiLogic()
    {
        for (var index = 0; index < TableRowDisplayOptions.ToolbarChoices.Count; index++)
        {
            TableRowDisplayChoice choice = TableRowDisplayOptions.FromToolbarIndex(index);
            Assert.AreEqual(index, TableRowDisplayOptions.GetToolbarIndex(choice));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TableRowDisplayOptions.FromToolbarIndex(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TableRowDisplayOptions.FromToolbarIndex(TableRowDisplayOptions.ToolbarChoices.Count));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TableRowDisplayChoice.FromPageSize(501));
    }
}
