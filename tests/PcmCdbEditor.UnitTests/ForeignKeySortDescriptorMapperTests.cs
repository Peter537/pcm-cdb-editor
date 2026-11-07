using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class ForeignKeySortDescriptorMapperTests
{
    [TestMethod]
    public void LabelOptionAndPhysicalDisplaySuffixColumnRemainDistinct()
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();

        TableQueryColumnOption[] options = ForeignKeySortDescriptorMapper.GetOptions(catalog, source);

        CollectionAssert.AreEqual(
            new[]
            {
                ("ID", QueryValueTarget.StoredValue),
                ("fkIDcountry", QueryValueTarget.StoredValue),
                ("fkIDcountry", QueryValueTarget.RelatedLabel),
                ("fkIDcountry__display", QueryValueTarget.StoredValue),
                ("fkIDambiguous", QueryValueTarget.StoredValue),
                ("fkIDcomposite", QueryValueTarget.StoredValue),
            },
            options.Select(static option => (option.ColumnName, option.Target)).ToArray());
        Assert.AreEqual(SqliteAffinity.Integer, options[1].Affinity);
        Assert.AreEqual(SqliteAffinity.Text, options[2].Affinity);
    }

    [TestMethod]
    public void RestorePreservesBothTargetsAndDropsUnsupportedOrDuplicateSorts()
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();

        SortDescriptor[] restored = ForeignKeySortDescriptorMapper.Restore(catalog, source,
        [
            new("FKIDCOUNTRY", SortDirection.Ascending, QueryValueTarget.RelatedLabel),
            new("fkIDcountry", SortDirection.Descending),
            new("fkIDcountry", SortDirection.Descending, QueryValueTarget.RelatedLabel),
            new("fkIDcountry__display", SortDirection.Ascending),
            new("fkIDambiguous", SortDirection.Ascending, QueryValueTarget.RelatedLabel),
            new("fkIDcomposite", SortDirection.Ascending, QueryValueTarget.RelatedLabel),
            new("hidden", SortDirection.Ascending),
            new("missing", SortDirection.Ascending),
            new("ID", (SortDirection)99),
            new("ID", SortDirection.Ascending, (QueryValueTarget)99),
        ]);

        CollectionAssert.AreEqual(
            new SortDescriptor[]
            {
                new("fkIDcountry", SortDirection.Ascending, QueryValueTarget.RelatedLabel),
                new("fkIDcountry", SortDirection.Descending),
                new("fkIDcountry__display", SortDirection.Ascending),
            },
            restored);
    }

    [TestMethod]
    [DataRow(ForeignKeyDisplayMode.RawValue, QueryValueTarget.StoredValue)]
    [DataRow(ForeignKeyDisplayMode.ResolvedName, QueryValueTarget.RelatedLabel)]
    [DataRow(ForeignKeyDisplayMode.RawAndName, QueryValueTarget.RelatedLabel)]
    public void HeaderUsesModeForNewIntentAndTogglesOnlyThatTarget(
        ForeignKeyDisplayMode mode,
        QueryValueTarget target)
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();
        QueryValueTarget other = target == QueryValueTarget.RelatedLabel
            ? QueryValueTarget.StoredValue
            : QueryValueTarget.RelatedLabel;
        SortDescriptor[] initial =
        [
            new("fkIDcountry", SortDirection.Descending, other),
            new("ID", SortDirection.Ascending),
        ];

        SortDescriptor[] descending = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog, source, mode, initial, "FKIDCOUNTRY");
        SortDescriptor[] ascending = ForeignKeySortDescriptorMapper.PlanHeaderClick(
            catalog, source, mode, descending, "fkIDcountry");

        CollectionAssert.AreEqual(
            (SortDescriptor[])[new("fkIDcountry", SortDirection.Descending, target), .. initial],
            descending);
        CollectionAssert.AreEqual(
            (SortDescriptor[])[new("fkIDcountry", SortDirection.Ascending, target), .. initial],
            ascending);
    }

    [TestMethod]
    public void CommittedLabelIntentHasPhysicalHeaderAndAccurateSummary()
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();
        SortDescriptor[] sorts = [new("fkIDcountry", SortDirection.Ascending, QueryValueTarget.RelatedLabel)];

        Assert.AreEqual(
            new TableHeaderSortIndicator("fkIDcountry", SortDirection.Ascending),
            ForeignKeySortDescriptorMapper.GetPrimaryHeaderIndicator(catalog, source, sorts));
        Assert.AreEqual(
            "fkIDcountry (related name), ascending",
            ForeignKeySortDescriptorMapper.Describe(sorts[0]));
    }

    [TestMethod]
    public void UnsupportedRelationshipHeadersSortRawAndUnknownHeadersLeaveSortsAlone()
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();
        SortDescriptor[] initial = [new("ID", SortDirection.Ascending)];

        CollectionAssert.AreEqual(
            initial,
            ForeignKeySortDescriptorMapper.PlanHeaderClick(
                catalog, source, ForeignKeyDisplayMode.RawAndName, initial, "hidden"));
        CollectionAssert.AreEqual(
            (SortDescriptor[])[new("fkIDcomposite", SortDirection.Descending), .. initial],
            ForeignKeySortDescriptorMapper.PlanHeaderClick(
                catalog, source, ForeignKeyDisplayMode.RawAndName, initial, "fkIDcomposite"));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("hidden")]
    public void PrimaryHeaderIndicatorDoesNotPromoteASecondarySort(string primaryColumn)
    {
        (DatabaseSchemaCatalog catalog, TableSchema source) = CreateCatalog();
        Assert.IsNull(ForeignKeySortDescriptorMapper.GetPrimaryHeaderIndicator(catalog, source,
        [
            new(primaryColumn, SortDirection.Descending),
            new("ID", SortDirection.Ascending),
        ]));
    }

    private static (DatabaseSchemaCatalog Catalog, TableSchema Source) CreateCatalog()
    {
        var source = Table(
            "source",
            [
                Column(0, "ID"), Column(1, "fkIDcountry"), Column(2, "fkIDcountry__display"),
                Column(3, "fkIDambiguous"), Column(4, "fkIDcomposite"), Column(5, "hidden", isHidden: true),
            ],
            [
                new("fkIDcountry", "country", "IDcountry", "name", true, "declared"),
                new("fkIDambiguous", "country", "IDcountry", "name", false, "inferred"),
                new("fkIDambiguous", "alternate_country", "IDcountry", "name", false, "inferred"),
                new("fkIDcomposite", "country", "IDcountry", "name", true, "declared", IsComposite: true),
            ]);
        var country = Table("country", [Column(0, "IDcountry"), Column(1, "name", SqliteAffinity.Text)], []);
        var alternate = Table("alternate_country", country.Columns, []);
        return (new DatabaseSchemaCatalog("fk-sort-schema", [source, country, alternate]), source);
    }

    private static TableSchema Table(
        string name,
        IReadOnlyList<ColumnSchema> columns,
        IReadOnlyList<ForeignKeyRelation> relationships) => new(
        name, TableObjectKind.Table, columns, relationships,
        new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, [columns[0].Name]),
        TableEditCapability.Editable, estimatedRowCount: null, isWithoutRowId: false);

    private static ColumnSchema Column(
        int ordinal,
        string name,
        SqliteAffinity affinity = SqliteAffinity.Integer,
        bool isHidden = false) => new(
        ordinal, name, affinity == SqliteAffinity.Text ? "TEXT" : "INTEGER", affinity,
        IsNullable: false, DefaultExpression: null, PrimaryKeyOrdinal: ordinal == 0 ? 1 : 0,
        IsGenerated: false, IsHidden: isHidden);
}
