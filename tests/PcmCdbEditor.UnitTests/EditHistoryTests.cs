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
    public void SnapshotTracksSavedStateAcrossUndoAndDropsAnAbandonedBranch()
    {
        var history = new InMemoryEditHistory();
        var first = Operation("first", 1);
        var saved = Operation("saved", 2);
        history.Record(first);
        Assert.AreEqual(0, history.GetSnapshot().SavedAppliedCount);
        history.Record(saved);
        history.MarkSavedBaseline();

        EditHistorySnapshot original = history.GetSnapshot();
        Assert.AreEqual(2, original.SavedAppliedCount);
        Assert.AreSame(saved, original.NextUndo);
        Assert.IsNull(original.NextRedo);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<EditOperation>)original.Applied).Clear());

        EditHistoryReplay pending = history.TakeUndoReplay();
        EditHistorySnapshot inFlight = history.GetSnapshot();
        Assert.IsTrue(inFlight.State.HasPendingReplay);
        Assert.IsNull(inFlight.SavedAppliedCount);
        Assert.IsNull(inFlight.NextUndo);
        Assert.IsNull(inFlight.NextRedo);

        history.CompleteUndo(pending, []);
        EditHistorySnapshot beforeSave = history.GetSnapshot();
        Assert.AreEqual(2, beforeSave.SavedAppliedCount);
        Assert.AreSame(first, beforeSave.NextUndo);
        Assert.AreSame(saved, beforeSave.NextRedo);

        var branched = Operation("branched", 3);
        history.Record(branched);
        EditHistorySnapshot branch = history.GetSnapshot();
        Assert.IsTrue(branch.State.IsDirty);
        Assert.IsNull(branch.SavedAppliedCount);
        Assert.AreSame(branched, branch.NextUndo);
        Assert.IsNull(branch.NextRedo);
        Assert.AreSame(saved, original.NextUndo);
        Assert.AreSame(saved, beforeSave.NextRedo);
    }

    [TestMethod]
    public void InspectionPreservesTypedValuesAndActualTablesForOneBatchRowAtATime()
    {
        RowIdentity rider = RowIdentity.FromPrimaryKey(
            [new RowIdentityComponent("IDcyclist", SqliteValue.Integer(7))]);
        RowIdentity contract = RowIdentity.FromRowId(9);
        var maintenance = new MaintenanceEditOperation(
            Guid.NewGuid(), "DYN_cyclist", DateTimeOffset.UnixEpoch,
            MaintenanceToolKind.RiderCreation, "Create rider and contract",
            [
                new MaintenanceRowChange("DYN_contract_cyclist", contract, null,
                    [KeyValuePair.Create("fkIDcyclist", SqliteValue.Integer(7))]),
                new MaintenanceRowChange("DYN_cyclist", rider,
                    [
                        KeyValuePair.Create("same", SqliteValue.Integer(7)),
                        KeyValuePair.Create("note", SqliteValue.Null)
                    ],
                    [
                        KeyValuePair.Create("same", SqliteValue.Integer(7)),
                        KeyValuePair.Create("note", SqliteValue.Text("NULL"))
                    ])
            ]);

        Assert.AreEqual("Create rider and contract", EditHistoryInspection.Describe(maintenance));
        Assert.AreEqual(2, EditHistoryInspection.GetRowCount(maintenance));
        EditHistoryRow first = EditHistoryInspection.GetRow(maintenance, 0);
        Assert.AreEqual("DYN_contract_cyclist", first.TableName);
        Assert.AreEqual(contract, first.Identity);
        Assert.IsNull(first.BeforeValues);
        Assert.AreEqual(SqliteValue.Integer(7), first.AfterValues!["fkIDcyclist"]);

        EditHistoryRow second = EditHistoryInspection.GetRow(maintenance, 1);
        Assert.AreEqual("DYN_cyclist", second.TableName);
        Assert.AreEqual(rider, second.Identity);
        Assert.AreEqual("note", Assert.ContainsSingle(second.ChangedColumns));
        Assert.AreEqual(SqliteValue.Null, second.BeforeValues!["note"]);
        Assert.AreEqual(SqliteValue.Text("NULL"), second.AfterValues!["note"]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditHistoryInspection.GetRow(maintenance, 2));
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
