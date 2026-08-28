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

    [TestMethod]
    [DataRow(
        ForeignKeyDisplayMode.ResolvedName,
        "fkIDcountry",
        "fkIDcountry__display")]
    [DataRow(
        ForeignKeyDisplayMode.RawAndName,
        "FKIDCOUNTRY",
        "fkIDcountry__display")]
    [DataRow(ForeignKeyDisplayMode.RawValue, "fkIDcountry", "fkIDcountry")]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, "fkIDambiguous", "fkIDambiguous")]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, "LABEL", "label")]
    public void PlanHeaderClickUsesTheDescriptorRepresentedByTheVisibleHeader(
        ForeignKeyDisplayMode displayMode,
        string headerColumnName,
        string expectedDescriptorColumnName)
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        SortDescriptor[] planned = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog,
            table,
            displayMode,
            descriptors: null,
            headerColumnName);

        CollectionAssert.AreEqual(
            new[]
            {
                new SortDescriptor(
                    expectedDescriptorColumnName,
                    SortDirection.Descending),
            },
            planned);
    }

    [TestMethod]
    public void PlanHeaderClickPromotesAndTogglesOnlyTheResolvedDescriptor()
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();
        SortDescriptor[] initial =
        [
            new SortDescriptor("ID", SortDirection.Ascending),
            new SortDescriptor("FKIDCOUNTRY__DISPLAY", SortDirection.Ascending),
            new SortDescriptor("fkIDcountry__display", SortDirection.Descending),
            new SortDescriptor("fkIDambiguous", SortDirection.Ascending),
        ];

        SortDescriptor[] descending = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog,
            table,
            ForeignKeyDisplayMode.ResolvedName,
            initial,
            "fkIDcountry");
        SortDescriptor[] ascending = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog,
            table,
            ForeignKeyDisplayMode.ResolvedName,
            descending,
            "FKIDCOUNTRY");
        SortDescriptor[] descendingAgain = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog,
            table,
            ForeignKeyDisplayMode.ResolvedName,
            ascending,
            "fkIDcountry");

        CollectionAssert.AreEqual(
            new[]
            {
                new SortDescriptor("fkIDcountry__display", SortDirection.Descending),
                new SortDescriptor("ID", SortDirection.Ascending),
                new SortDescriptor("fkIDambiguous", SortDirection.Ascending),
            },
            descending);
        CollectionAssert.AreEqual(
            new[]
            {
                new SortDescriptor("fkIDcountry__display", SortDirection.Ascending),
                new SortDescriptor("ID", SortDirection.Ascending),
                new SortDescriptor("fkIDambiguous", SortDirection.Ascending),
            },
            ascending);
        CollectionAssert.AreEqual(
            new[]
            {
                new SortDescriptor("fkIDcountry__display", SortDirection.Descending),
                new SortDescriptor("ID", SortDirection.Ascending),
                new SortDescriptor("fkIDambiguous", SortDirection.Ascending),
            },
            descendingAgain);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("unknown")]
    [DataRow("hidden")]
    public void PlanHeaderClickLeavesSortsUnchangedForInvalidOrHiddenHeaders(
        string? headerColumnName)
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();
        SortDescriptor[] current =
        [
            new SortDescriptor("label", SortDirection.Ascending),
            new SortDescriptor("ID", SortDirection.Descending),
        ];

        SortDescriptor[] planned = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog,
            table,
            ForeignKeyDisplayMode.ResolvedName,
            current,
            headerColumnName);

        CollectionAssert.AreEqual(current, planned);
    }

    [TestMethod]
    [DataRow(
        ForeignKeyDisplayMode.ResolvedName,
        "FKIDCOUNTRY__DISPLAY",
        SortDirection.Ascending,
        "fkIDcountry")]
    [DataRow(
        ForeignKeyDisplayMode.RawAndName,
        "fkIDcountry__display",
        SortDirection.Descending,
        "fkIDcountry")]
    [DataRow(
        ForeignKeyDisplayMode.ResolvedName,
        "FKIDCOUNTRY",
        SortDirection.Descending,
        "fkIDcountry")]
    [DataRow(
        ForeignKeyDisplayMode.RawValue,
        "LABEL",
        SortDirection.Ascending,
        "label")]
    public void GetPrimaryHeaderIndicatorMapsValidPrimarySortsToPhysicalHeaders(
        ForeignKeyDisplayMode displayMode,
        string descriptorColumnName,
        SortDirection direction,
        string expectedHeaderColumnName)
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        TableHeaderSortIndicator? indicator =
            ForeignKeySortDescriptorMapper.GetPrimaryHeaderIndicator(
                catalog,
                table,
                displayMode,
                [new SortDescriptor(descriptorColumnName, direction)]);

        Assert.AreEqual(
            new TableHeaderSortIndicator(expectedHeaderColumnName, direction),
            indicator);
    }

    [TestMethod]
    [DataRow(ForeignKeyDisplayMode.RawValue, "fkIDcountry__display", 0)]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, "hidden", 1)]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, "unknown", 0)]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, "label", 99)]
    public void GetPrimaryHeaderIndicatorRejectsInvalidOrHiddenPrimarySorts(
        ForeignKeyDisplayMode displayMode,
        string descriptorColumnName,
        int directionValue)
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        TableHeaderSortIndicator? indicator =
            ForeignKeySortDescriptorMapper.GetPrimaryHeaderIndicator(
                catalog,
                table,
                displayMode,
                [new SortDescriptor(descriptorColumnName, (SortDirection)directionValue)]);

        Assert.IsNull(indicator);
    }

    [TestMethod]
    public void GetPrimaryHeaderIndicatorDoesNotPromoteALaterValidSort()
    {
        (DatabaseSchemaCatalog catalog, TableSchema table) = CreateCatalog();

        TableHeaderSortIndicator? indicator =
            ForeignKeySortDescriptorMapper.GetPrimaryHeaderIndicator(
                catalog,
                table,
                ForeignKeyDisplayMode.ResolvedName,
                [
                    new SortDescriptor("unknown", SortDirection.Descending),
                    new SortDescriptor("label", SortDirection.Ascending),
                ]);

        Assert.IsNull(indicator);
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
