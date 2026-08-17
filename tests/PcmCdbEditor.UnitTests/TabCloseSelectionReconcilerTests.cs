using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.App;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class TabCloseSelectionReconcilerTests
{
    [TestMethod]
    public void BackgroundCloseRejectsTransientRemovedSelectionAndKeepsCurrentTab()
    {
        TabCloseSelectionResolution result = TabCloseSelectionReconciler.Resolve(
            ["DB_STRUCTURE", "DYN_cyclist", "DYN_team"],
            closingTable: "DYN_cyclist",
            selectedBeforeClose: "DYN_team",
            selectedAfterRemoval: "DYN_cyclist",
            visibleTables: ["DB_STRUCTURE", "DYN_cyclist", "DYN_team"]);

        Assert.AreEqual("DYN_team", result.SelectedTable);
        Assert.AreEqual("DYN_team", result.SidebarTable);
    }

    [TestMethod]
    public void ForegroundCloseUsesAdjacentTabWhenFrameworkSelectionIsStillRemovedTab()
    {
        TabCloseSelectionResolution result = TabCloseSelectionReconciler.Resolve(
            ["DB_STRUCTURE", "DYN_cyclist", "DYN_team"],
            closingTable: "DYN_cyclist",
            selectedBeforeClose: "DYN_cyclist",
            selectedAfterRemoval: "DYN_cyclist",
            visibleTables: ["DB_STRUCTURE", "DYN_cyclist", "DYN_team"]);

        Assert.AreEqual("DYN_team", result.SelectedTable);
        Assert.AreEqual("DYN_team", result.SidebarTable);
    }

    [TestMethod]
    public void LastTabCloseClearsBothSelections()
    {
        TabCloseSelectionResolution result = TabCloseSelectionReconciler.Resolve(
            ["DYN_cyclist"],
            closingTable: "DYN_cyclist",
            selectedBeforeClose: "DYN_cyclist",
            selectedAfterRemoval: "DYN_cyclist",
            visibleTables: ["DYN_cyclist"]);

        Assert.IsNull(result.SelectedTable);
        Assert.IsNull(result.SidebarTable);
    }

    [TestMethod]
    public void FilteredCurrentTabRemainsSelectedButClearsSidebarSelection()
    {
        TabCloseSelectionResolution result = TabCloseSelectionReconciler.Resolve(
            ["DB_STRUCTURE", "DYN_cyclist", "DYN_team"],
            closingTable: "DYN_cyclist",
            selectedBeforeClose: "DYN_team",
            selectedAfterRemoval: "DYN_team",
            visibleTables: ["DYN_cyclist"]);

        Assert.AreEqual("DYN_team", result.SelectedTable);
        Assert.IsNull(result.SidebarTable);
    }
}
