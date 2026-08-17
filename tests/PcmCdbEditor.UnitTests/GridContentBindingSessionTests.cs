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
    public void BulkRowSourcePublishesOnlyACompleteReplacement()
    {
        var source = new BulkRowSource<string>();
        source.Replace([0], static value => $"old-{value}");
        string[] original = source.Items;
        var projected = 0;

        source.Replace([1, 2, 3], value =>
        {
            Assert.AreSame(original, source.Items);
            projected++;
            return $"row-{value}";
        });

        Assert.AreEqual(3, projected);
        Assert.AreNotSame(original, source.Items);
        Assert.HasCount(3, source.Items);
        Assert.AreEqual("row-1", source.Items[0]);
        Assert.AreEqual("row-2", source.Items[1]);
        Assert.AreEqual("row-3", source.Items[2]);

        string[] completed = source.Items;
        Assert.Throws<InvalidOperationException>(() =>
            source.Replace([4, 5], value => value == 5
                ? throw new InvalidOperationException("projection failed")
                : $"row-{value}"));
        Assert.AreSame(completed, source.Items);
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

    private sealed record PresentedRow(RowIdentity? Identity, string Label);
}
