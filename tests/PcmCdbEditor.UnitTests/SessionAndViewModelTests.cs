using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SessionAndViewModelTests
{
    [TestMethod]
    public void TableViewStateFreezesOnlyPersistentColumnLayoutUnderSchemaSignature()
    {
        var columns = new List<ColumnDisplayState>
        {
            new("name", 120, 0, true, true)
        };
        var state = new TableViewState("schema-a", "people", columns, GridDensity.Compact, 1);
        columns.Clear();

        Assert.AreEqual("schema-a", state.SchemaSignature);
        Assert.HasCount(1, state.Columns);
        Assert.IsNull(
            typeof(TableViewState).GetProperty("Sorts"),
            "Session-only sort descriptors must not be exposed by persisted table view state.");
        Assert.AreEqual(GridDensity.Compact, state.Density);
        Assert.AreEqual(1, state.FrozenColumnCount);
    }
}
