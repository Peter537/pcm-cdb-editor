using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class GridPagePresentationTests
{
    private static readonly TableSchema Schema = new(
        "sample",
        TableObjectKind.Table,
        [
            new ColumnSchema(1, "name", "TEXT", SqliteAffinity.Text, true, null, 0, false, false),
            new ColumnSchema(2, "internal", "TEXT", SqliteAffinity.Text, true, null, 0, false, true),
            new ColumnSchema(0, "id", "INTEGER", SqliteAffinity.Integer, false, null, 1, false, false)
        ],
        [],
        new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["id"]),
        TableEditCapability.Editable,
        null,
        isWithoutRowId: false);

    [TestMethod]
    public async Task PreparedPagePreservesTypedRowsAndProjectsOnlyVisibleSchemaColumnsInOrdinalOrder()
    {
        var row = new TypedRow(
            RowIdentity.FromRowId(9),
            [
                KeyValuePair.Create("id", SqliteValue.Integer(9)),
                KeyValuePair.Create("name", SqliteValue.Null),
                KeyValuePair.Create("internal", SqliteValue.Text("omitted"))
            ],
            [KeyValuePair.Create("id", new RelatedValue(RelationshipResolution.Resolved, "Euro"))]);

        GridPagePresentation page = await GridPagePresentation.PrepareAsync(
            Schema, [row], ForeignKeyDisplayMode.RawAndName, CancellationToken.None);

        Assert.AreSame(Schema, page.Schema);
        Assert.HasCount(2, page.Columns);
        Assert.AreEqual("id", page.Columns[0].Name);
        Assert.AreEqual("name", page.Columns[1].Name);
        GridRowPresentation model = page.Rows.Single();
        Assert.AreSame(row, model.Row);
        Assert.AreEqual(row.Identity, model.Identity);
        Assert.AreEqual(row.Revision, model.Revision);
        Assert.AreEqual("Euro · 9", model.Cells[0].Display);
        Assert.AreEqual(SqliteValue.Integer(9), model.Cells[0].Value);
        Assert.AreEqual("NULL", model.Cells[1].Display);
        Assert.IsFalse(model.TryGetCell("internal", out _));
    }

    [TestMethod]
    public async Task CancellationDuringPreparationDoesNotPublishAPartialPageOrContinueReadingRows()
    {
        var row = new TypedRow(RowIdentity.FromRowId(9), [KeyValuePair.Create("id", SqliteValue.Integer(9))]);
        using var cancellation = new CancellationTokenSource();
        GridPagePresentation previous = await GridPagePresentation.PrepareAsync(
            Schema, [row], ForeignKeyDisplayMode.RawValue, CancellationToken.None);
        GridPagePresentation published = previous;
        int accesses = 0;
        var incoming = new ControlledRows(10, index =>
        {
            accesses++;
            if (index == 1)
            {
                cancellation.Cancel();
            }

            return row;
        });

        try
        {
            published = await GridPagePresentation.PrepareAsync(
                Schema, incoming, ForeignKeyDisplayMode.RawValue, cancellation.Token);
            Assert.Fail("Preparation published a cancelled page.");
        }
        catch (OperationCanceledException exception)
        {
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        }

        Assert.AreEqual(2, accesses);
        Assert.AreSame(previous, published);
        Assert.AreSame(row, published.Rows.Single().Row);
    }

    private sealed class ControlledRows(int count, Func<int, TypedRow> read) : IReadOnlyList<TypedRow>
    {
        public int Count => count;

        public TypedRow this[int index] => read(index);

        public IEnumerator<TypedRow> GetEnumerator() =>
            Enumerable.Range(0, count).Select(read).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
