using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class ForeignKeySortDescriptorMapperTests
{
    private static readonly string[] DisplayModeDescriptorNames =
        ["ID", "fkIDcountry", "fkIDcountry__display", "label", "fkIDambiguous"];

    private static readonly string[] RawModeDescriptorNames =
        ["ID", "fkIDcountry", "label", "fkIDambiguous"];

    [TestMethod]
    public void DisplayModesOfferDistinctRawAndDisplayedSortChoices()
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        TableSortOption[] names = ForeignKeySortDescriptorMapper.GetOptions(
            catalog,
            table,
            ForeignKeyDisplayMode.ResolvedName);
        TableSortOption[] rawAndNames = ForeignKeySortDescriptorMapper.GetOptions(
            catalog,
            table,
            ForeignKeyDisplayMode.RawAndName);

        CollectionAssert.AreEqual(
            DisplayModeDescriptorNames,
            names.Select(static option => option.DescriptorColumnName).ToArray());
        Assert.AreEqual("fkIDcountry (raw value)", names[1].Label);
        Assert.AreEqual("fkIDcountry (displayed name)", names[2].Label);
        Assert.AreEqual("fkIDcountry (displayed raw value and name)", rawAndNames[2].Label);
        Assert.IsFalse(names.Any(static option =>
            option.DescriptorColumnName.Equals("fkIDambiguous__display", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RawModeOffersOnlyPhysicalColumnsAndRejectsDisplayedDescriptors()
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        TableSortOption[] options = ForeignKeySortDescriptorMapper.GetOptions(
            catalog,
            table,
            ForeignKeyDisplayMode.RawValue);
        SortDescriptor[] restored = ForeignKeySortDescriptorMapper.Restore(
            catalog,
            table,
            ForeignKeyDisplayMode.RawValue,
            [
                new SortDescriptor("fkIDcountry__display", SortDirection.Ascending),
                new SortDescriptor("FKIDCOUNTRY", SortDirection.Descending),
            ]);

        CollectionAssert.AreEqual(
            RawModeDescriptorNames,
            options.Select(static option => option.DescriptorColumnName).ToArray());
        Assert.AreEqual("fkIDcountry", options[1].Label);
        CollectionAssert.AreEqual(
            new[] { new SortDescriptor("fkIDcountry", SortDirection.Descending) },
            restored);
    }

    [TestMethod]
    public void RestoreCanonicalizesValidMultiSortAndDropsInvalidOrDuplicateEntries()
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        SortDescriptor[] restored = ForeignKeySortDescriptorMapper.Restore(
            catalog,
            table,
            ForeignKeyDisplayMode.RawAndName,
            [
                new SortDescriptor("FKIDCOUNTRY__DISPLAY", SortDirection.Descending),
                new SortDescriptor("fkidcountry", SortDirection.Ascending),
                new SortDescriptor("LABEL", SortDirection.Descending),
                new SortDescriptor("label", SortDirection.Ascending),
                new SortDescriptor("unknown__display", SortDirection.Ascending),
                new SortDescriptor("ID", (SortDirection)99),
            ]);

        CollectionAssert.AreEqual(
            new[]
            {
                new SortDescriptor("fkIDcountry__display", SortDirection.Descending),
                new SortDescriptor("fkIDcountry", SortDirection.Ascending),
                new SortDescriptor("label", SortDirection.Descending),
            },
            restored);
    }

    private static (DatabaseSchemaCatalog Catalog, TableSchema Source) CreateCatalog()
    {
        var source = new TableSchema(
            "source",
            TableObjectKind.Table,
            [
                Column(0, "ID"),
                Column(1, "fkIDcountry"),
                Column(2, "label"),
                Column(3, "fkIDambiguous"),
                Column(4, "hidden", isHidden: true),
            ],
            [
                new ForeignKeyRelation(
                    "fkIDcountry",
                    "country",
                    "IDcountry",
                    "name",
                    true,
                    "declared"),
                new ForeignKeyRelation(
                    "fkIDambiguous",
                    "country",
                    "IDcountry",
                    "name",
                    false,
                    "inferred"),
                new ForeignKeyRelation(
                    "fkIDambiguous",
                    "alternate_country",
                    "IDcountry",
                    "name",
                    false,
                    "inferred"),
            ],
            new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["ID"]),
            TableEditCapability.Editable,
            estimatedRowCount: null,
            isWithoutRowId: false);
        var country = new TableSchema(
            "country",
            TableObjectKind.Table,
            [Column(0, "IDcountry"), Column(1, "name")],
            [],
            new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["IDcountry"]),
            TableEditCapability.Editable,
            estimatedRowCount: null,
            isWithoutRowId: false);
        var alternateCountry = new TableSchema(
            "alternate_country",
            TableObjectKind.Table,
            [Column(0, "IDcountry"), Column(1, "name")],
            [],
            new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["IDcountry"]),
            TableEditCapability.Editable,
            estimatedRowCount: null,
            isWithoutRowId: false);

        return (new DatabaseSchemaCatalog("fk-sort-schema", [source, country, alternateCountry]), source);
    }

    private static ColumnSchema Column(int ordinal, string name, bool isHidden = false) => new(
        ordinal,
        name,
        "INTEGER",
        SqliteAffinity.Integer,
        IsNullable: false,
        DefaultExpression: null,
        PrimaryKeyOrdinal: ordinal == 0 ? 1 : 0,
        IsGenerated: false,
        IsHidden: isHidden);
}
