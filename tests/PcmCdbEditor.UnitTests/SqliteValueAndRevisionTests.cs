using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SqliteValueAndRevisionTests
{
    [TestMethod]
    public void TypedValuesPreserveAllSQLiteStorageClasses()
    {
        var blobSource = new byte[] { 1, 2, 3 };
        var blob = SqliteValue.Blob(blobSource);
        blobSource[0] = 99;

        Assert.AreEqual(SqliteValueKind.Null, SqliteValue.Null.Kind);
        Assert.AreEqual(42L, SqliteValue.Integer(42).IntegerValue);
        Assert.AreEqual(4.25, SqliteValue.Real(4.25).RealValue);
        Assert.AreEqual(string.Empty, SqliteValue.Text(string.Empty).TextValue);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, blob.GetBlobBytes());

        var returned = blob.GetBlobBytes();
        returned[1] = 88;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, blob.GetBlobBytes());
    }

    [TestMethod]
    public void RevisionIsIndependentOfDictionaryEnumerationOrder()
    {
        var first = RowRevision.Compute(
        [
            KeyValuePair.Create("name", SqliteValue.Text("Ada")),
            KeyValuePair.Create("score", SqliteValue.Integer(7))
        ]);
        var second = RowRevision.Compute(
        [
            KeyValuePair.Create("score", SqliteValue.Integer(7)),
            KeyValuePair.Create("name", SqliteValue.Text("Ada"))
        ]);

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Value.Length);
    }

    [TestMethod]
    public void RevisionDistinguishesSQLiteTypesAndFloatingPointBits()
    {
        var integer = RowRevision.Compute([KeyValuePair.Create("value", SqliteValue.Integer(1))]);
        var real = RowRevision.Compute([KeyValuePair.Create("value", SqliteValue.Real(1))]);
        var positiveZero = RowRevision.Compute([KeyValuePair.Create("value", SqliteValue.Real(0d))]);
        var negativeZero = RowRevision.Compute([KeyValuePair.Create("value", SqliteValue.Real(-0d))]);

        Assert.AreNotEqual(integer, real);
        Assert.AreNotEqual(positiveZero, negativeZero);
    }

    [TestMethod]
    public void TypedRowComputesRevisionAndFreezesValues()
    {
        var source = new Dictionary<string, SqliteValue>
        {
            ["ID"] = SqliteValue.Integer(3),
            ["value"] = SqliteValue.Text("before")
        };

        var row = new TypedRow(RowIdentity.FromRowId(3), source);
        source["value"] = SqliteValue.Text("after");

        Assert.AreEqual("before", row.Values["value"].TextValue);
        Assert.AreEqual(row.Revision, RowRevision.Compute(row.Values));
    }
}
