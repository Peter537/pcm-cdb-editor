using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class EditHistoryTests
{
    [TestMethod]
    public void RecordUndoRedoMaintainsCommandOrdering()
    {
        var history = new InMemoryEditHistory();
        var first = Operation("first", 1);
        var second = Operation("second", 2);
        history.Record(first);
        history.Record(second);

        var undo = history.TakeUndo();
        Assert.AreSame(second, undo);
        history.CompleteUndo(undo);
        Assert.IsTrue(history.State.CanRedo);

        var redo = history.TakeRedo();
        Assert.AreSame(second, redo);
        history.CompleteRedo(redo);
        Assert.AreEqual(2, history.State.UndoCount);
    }

    [TestMethod]
    public void NewRecordClearsRedoAndFailedReplayReturnsToOriginalStack()
    {
        var history = new InMemoryEditHistory();
        var first = Operation("first", 1);
        history.Record(first);
        var pending = history.TakeUndo();
        history.RestoreFailedUndo(pending);
        Assert.AreEqual(1, history.State.UndoCount);

        pending = history.TakeUndo();
        history.CompleteUndo(pending);
        Assert.IsTrue(history.State.CanRedo);
        history.Record(Operation("new", 2));
        Assert.IsFalse(history.State.CanRedo);
    }

    [TestMethod]
    public void HistoryRequiresPendingReplayToBeCompletedOrRestored()
    {
        var history = new InMemoryEditHistory();
        var operation = Operation("value", 1);
        history.Record(operation);
        _ = history.TakeUndo();

        Assert.ThrowsExactly<InvalidOperationException>(() => history.Record(Operation("next", 2)));
        Assert.ThrowsExactly<InvalidOperationException>(() => history.CompleteRedo(operation));
    }

    [TestMethod]
    public void UndoAndRedoCrossTheSavedBaselineInBothDirections()
    {
        var history = new InMemoryEditHistory();
        history.Record(Operation("edited", 1));
        Assert.IsTrue(history.State.IsDirty);

        EditHistoryReplay undoInitialEdit = history.TakeUndoReplay();
        history.CompleteUndo(undoInitialEdit, []);
        Assert.IsFalse(history.State.IsDirty, "Undoing the only edit must return to the initial baseline.");

        EditHistoryReplay redoInitialEdit = history.TakeRedoReplay();
        history.CompleteRedo(redoInitialEdit, []);
        history.MarkSavedBaseline();
        Assert.IsFalse(history.State.IsDirty);

        EditHistoryReplay undoSavedEdit = history.TakeUndoReplay();
        history.CompleteUndo(undoSavedEdit, []);
        Assert.IsTrue(history.State.IsDirty, "Undoing past a saved baseline creates unsaved work.");

        EditHistoryReplay redoSavedEdit = history.TakeRedoReplay();
        history.CompleteRedo(redoSavedEdit, []);
        Assert.IsFalse(history.State.IsDirty, "Redoing to the saved baseline must be clean again.");
    }

    [TestMethod]
    public void InsertionOperationSupportsDatabaseAssignedIdentityAndFreezesValues()
    {
        var values = new Dictionary<string, SqliteValue>
        {
            ["name"] = SqliteValue.Text("new row")
        };
        var pending = new RowInsertionOperation(Guid.NewGuid(), "sample", DateTimeOffset.UnixEpoch, values);
        values["name"] = SqliteValue.Text("changed");

        Assert.IsNull(pending.AssignedIdentity);
        Assert.AreEqual("new row", pending.Values["name"].TextValue);

        var replay = new RowInsertionOperation(
            pending.OperationId,
            pending.TableName,
            pending.CreatedAtUtc,
            pending.Values,
            RowIdentity.FromRowId(12));
        Assert.AreEqual(RowIdentity.FromRowId(12), replay.AssignedIdentity);
    }

    private static CellUpdateOperation Operation(string value, long revisionSeed) =>
        new(
            Guid.NewGuid(),
            "sample",
            DateTimeOffset.UnixEpoch,
            RowIdentity.FromRowId(1),
            "name",
            SqliteValue.Text("old"),
            SqliteValue.Text(value),
            RowRevision.Compute([KeyValuePair.Create("seed", SqliteValue.Integer(revisionSeed))]));
}
