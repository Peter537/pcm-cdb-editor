using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SessionAndViewModelTests
{
    [TestMethod]
    public void SessionWithExpressionProducesImmutableLifecycleTransition()
    {
        var original = new EditorSessionState(
            Guid.NewGuid(),
            "source.cdb",
            "source.cdb",
            "session",
            "working.cdb",
            "working.sqlite",
            false,
            EditorSessionLifecycle.Ready,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null);

        var changed = original with
        {
            IsDirty = true,
            Lifecycle = EditorSessionLifecycle.Saving,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1)
        };

        Assert.IsFalse(original.IsDirty);
        Assert.AreEqual(EditorSessionLifecycle.Ready, original.Lifecycle);
        Assert.IsTrue(changed.IsDirty);
        Assert.AreEqual(EditorSessionLifecycle.Saving, changed.Lifecycle);
    }

    [TestMethod]
    public void TableViewStateFreezesColumnsAndSortsUnderSchemaSignature()
    {
        var columns = new List<ColumnDisplayState>
        {
            new("name", 120, 0, true, true)
        };
        var sorts = new List<SortDescriptor>
        {
            new("name", SortDirection.Ascending)
        };
        var state = new TableViewState("schema-a", "people", columns, sorts, GridDensity.Compact, 1);
        columns.Clear();
        sorts.Clear();

        Assert.AreEqual("schema-a", state.SchemaSignature);
        Assert.HasCount(1, state.Columns);
        Assert.HasCount(1, state.Sorts);
        Assert.AreEqual(GridDensity.Compact, state.Density);
        Assert.AreEqual(1, state.FrozenColumnCount);
    }
}
