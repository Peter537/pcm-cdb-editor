using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class RowIdentityAndSchemaTests
{
    [TestMethod]
    public void CompositeIdentityPreservesKeyOrderAndHasValueEquality()
    {
        var first = RowIdentity.FromPrimaryKey(
        [
            new RowIdentityComponent("season", SqliteValue.Integer(2026)),
            new RowIdentityComponent("code", SqliteValue.Text("AA"))
        ]);
        var second = RowIdentity.FromPrimaryKey(
        [
            new RowIdentityComponent("season", SqliteValue.Integer(2026)),
            new RowIdentityComponent("code", SqliteValue.Text("AA"))
        ]);

        Assert.AreEqual(first, second);
        Assert.AreEqual(RowIdentityKind.DeclaredPrimaryKey, first.Kind);
        Assert.AreEqual(2, first.Components.Count);
    }

    [TestMethod]
    public void IdentityRejectsNullAndDuplicateComponentsButAllowsBlobKeys()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RowIdentityComponent("id", SqliteValue.Null));
        var blobIdentity = RowIdentity.FromPrimaryKey(
            [new RowIdentityComponent("id", SqliteValue.Blob([1]))]);
        Assert.AreEqual(SqliteValueKind.Blob, blobIdentity.Components[0].Value.Kind);
        Assert.ThrowsExactly<ArgumentException>(() => RowIdentity.FromPrimaryKey(
        [
            new RowIdentityComponent("id", SqliteValue.Integer(1)),
            new RowIdentityComponent("ID", SqliteValue.Integer(2))
        ]));
    }

    [TestMethod]
    public void CatalogPerformsCaseInsensitiveExactLookup()
    {
        var catalog = CreateCatalog();

        Assert.IsTrue(catalog.TryGetTable("PEOPLE", out var table));
        Assert.AreEqual("people", table.Name);
        Assert.IsFalse(catalog.TryGetTable("person", out _));
    }

    [TestMethod]
    public void IdentifierValidatorAcceptsOnlyDiscoveredTableAndColumns()
    {
        var catalog = CreateCatalog();
        var table = SchemaIdentifierValidator.RequireTable(catalog, "PEOPLE");

        Assert.AreEqual("Display Name", SchemaIdentifierValidator.RequireColumn(table, "display name").Name);
        Assert.ThrowsExactly<UnknownSchemaIdentifierException>(
            () => SchemaIdentifierValidator.RequireTable(catalog, "people; DROP TABLE people"));
        Assert.ThrowsExactly<UnknownSchemaIdentifierException>(
            () => SchemaIdentifierValidator.RequireColumn(table, "missing"));
        Assert.ThrowsExactly<UnknownSchemaIdentifierException>(
            () => SchemaIdentifierValidator.RequireColumn(table, "bad\0name"));
    }

    [TestMethod]
    public void IdentifierValidatorWalksSortFilterAndSearchIdentifiers()
    {
        var catalog = CreateCatalog();
        var valid = new TableQuery(
            "people",
            new PageRequest(0, 50),
            [new SortDescriptor("ID", SortDirection.Ascending)],
            new FilterCondition("Display Name", FilterOperator.Contains, SqliteValue.Text("a")),
            new GlobalSearchRequest("a", ["Display Name"]));
        SchemaIdentifierValidator.ValidateQuery(catalog, valid);

        var invalid = new TableQuery(
            "people",
            new PageRequest(0, 50),
            [new SortDescriptor("not discovered", SortDirection.Ascending)]);
        Assert.ThrowsExactly<UnknownSchemaIdentifierException>(
            () => SchemaIdentifierValidator.ValidateQuery(catalog, invalid));
    }

    private static DatabaseSchemaCatalog CreateCatalog()
    {
        var columns = new[]
        {
            new ColumnSchema(0, "ID", "INTEGER", SqliteAffinity.Integer, false, null, 1, false, false),
            new ColumnSchema(1, "Display Name", "TEXT", SqliteAffinity.Text, true, null, 0, false, false)
        };
        var table = new TableSchema(
            "people",
            TableObjectKind.Table,
            columns,
            [],
            new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["ID"]),
            TableEditCapability.Editable,
            12,
            false);
        return new DatabaseSchemaCatalog("schema-v1", [table]);
    }
}
