using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class GridContentBindingSessionTests
{
    [TestMethod]
    public void BindIfChangedExecutesOneContentBindForTheSameSnapshots()
    {
        var session = new GridContentBindingSession();
        var owner = new object();
        var rows = new object();
        var viewState = new object();
        var bindCount = 0;

        Assert.IsTrue(session.BindIfChanged(owner, rows, viewState, () => bindCount++));
        Assert.IsFalse(session.BindIfChanged(owner, rows, viewState, () => bindCount++));
        Assert.IsFalse(session.BindIfChanged(owner, rows, viewState, () => bindCount++));

        Assert.AreEqual(1, bindCount);
        Assert.IsTrue(session.IsBoundTo(owner));

        var replacementRows = new object();
        Assert.IsTrue(session.BindIfChanged(owner, replacementRows, viewState, () => bindCount++));
        Assert.AreEqual(2, bindCount);

        session.Reset();
        Assert.IsTrue(session.BindIfChanged(owner, replacementRows, viewState, () => bindCount++));
        Assert.AreEqual(3, bindCount);
    }

    [TestMethod]
    public void ResolveSelectionPreservesIdentityAndCanonicalCurrentColumn()
    {
        var first = new PresentedRow(RowIdentity.FromRowId(1), "first");
        var second = new PresentedRow(RowIdentity.FromRowId(2), "second");
        var rows = new[] { first, second, new PresentedRow(null, "unidentified") };
        var saved = new GridSelection(
            RowIdentity.FromRowId(2),
            "displayname",
            [
                RowIdentity.FromRowId(2),
                RowIdentity.FromRowId(1),
                RowIdentity.FromRowId(2),
                RowIdentity.FromRowId(99),
            ]);

        GridSelectionResolution<PresentedRow> resolved =
            GridContentBindingSession.ResolveSelection(
                saved,
                rows,
                static row => row.Identity,
                ["ID", "DisplayName"]);

        Assert.AreSame(second, resolved.CurrentRow);
        Assert.AreSame(second, resolved.CurrentCellRow);
        Assert.AreEqual("DisplayName", resolved.CurrentColumn);
        CollectionAssert.AreEqual(new[] { second, first }, resolved.SelectedRows.ToArray());

        GridSelectionResolution<PresentedRow> missing =
            GridContentBindingSession.ResolveSelection(
                saved with { CurrentRow = RowIdentity.FromRowId(99) },
                rows,
                static row => row.Identity,
                ["ID", "DisplayName"]);
        Assert.IsNull(missing.CurrentRow);
        Assert.IsNull(missing.CurrentColumn);
    }

    [TestMethod]
    public void ResolveSelectionRestoresCellAndCheckboxRowsIndependently()
    {
        RowIdentity selectedIdentity = RowIdentity.FromRowId(1);
        RowIdentity cellIdentity = RowIdentity.FromRowId(2);
        var selected = new PresentedRow(selectedIdentity, "selected");
        var focused = new PresentedRow(cellIdentity, "focused");
        var saved = new GridSelection(selectedIdentity, "relatedkey", [selectedIdentity])
        {
            CurrentCellRow = cellIdentity,
        };

        GridSelectionResolution<PresentedRow> resolved = GridContentBindingSession.ResolveSelection(
            saved, [focused, selected], static row => row.Identity, ["ID", "RelatedKey"]);
        Assert.AreSame(selected, resolved.CurrentRow);
        Assert.AreSame(focused, resolved.CurrentCellRow);
        Assert.AreEqual("RelatedKey", resolved.CurrentColumn);
        CollectionAssert.AreEqual(new[] { selected }, resolved.SelectedRows.ToArray());

        GridSelectionResolution<PresentedRow> focusOnly = GridContentBindingSession.ResolveSelection(
            saved with { CurrentRow = null, SelectedRows = [] },
            [focused, selected], static row => row.Identity, ["ID", "RelatedKey"]);
        Assert.IsNull(focusOnly.CurrentRow);
        Assert.IsEmpty(focusOnly.SelectedRows);
        Assert.AreSame(focused, focusOnly.CurrentCellRow);

        GridSelectionResolution<PresentedRow> missingCell = GridContentBindingSession.ResolveSelection(
            saved, [selected], static row => row.Identity, ["ID", "RelatedKey"]);
        Assert.AreSame(selected, missingCell.CurrentRow);
        Assert.IsNull(missingCell.CurrentCellRow);
        Assert.IsNull(missingCell.CurrentColumn);
    }

    private sealed record PresentedRow(RowIdentity? Identity, string Label);
}
