using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class CountryQuotaReviewProjectionTests
{
    [TestMethod]
    public void ScopesIncludeUnchangedQualifiersAndPreserveTheOriginalPreview()
    {
        CountryQuotaChange unchangedBoth = Change(
            70,
            "DEU",
            "DEU",
            700,
            1,
            1,
            new CountryQuotaValues(8, 2, 8, 2),
            new CountryQuotaValues(8, 2, 8, 2));
        CountryQuotaChange worldOnly = Change(
            50,
            "CAN",
            "CAN",
            500,
            2,
            null,
            Zero,
            new CountryQuotaValues(8, 2, 0, 0));
        CountryQuotaChange europeanOnly = Change(
            30,
            "LVA",
            "LVA",
            300,
            30,
            2,
            Zero,
            new CountryQuotaValues(0, 0, 8, 2));
        CountryQuotaChange both = Change(
            40,
            "FRA",
            "FRA",
            400,
            3,
            3,
            new CountryQuotaValues(6, 2, 6, 2),
            new CountryQuotaValues(8, 2, 8, 2));
        CountryQuotaChange reset = Change(
            10,
            "OLD",
            "OLD",
            0,
            0,
            null,
            new CountryQuotaValues(4, 2, 6, 2),
            Zero);
        CountryQuotaChange[] originalOrder = [reset, europeanOnly, worldOnly, unchangedBoth, both];
        var preview = new CountryQuotaPreview("snapshot-token", new DateOnly(2027, 11, 1), originalOrder, 3, 3);

        CountryQuotaReviewProjection projection = CountryQuotaReviewProjection.Create(preview);

        Assert.AreSame(preview, projection.Source);
        Assert.AreEqual(preview.SnapshotToken, projection.SnapshotToken);
        Assert.AreEqual(preview.CurrentDate, projection.CurrentDate);
        Assert.AreEqual(5, projection.TotalCountryCount);
        Assert.AreEqual(4, projection.ChangeCount);
        Assert.AreEqual(3, projection.WorldQualifierCount);
        Assert.AreEqual(3, projection.EuropeanQualifierCount);
        Assert.AreEqual(4, projection.GetCount(CountryQuotaReviewScope.Changes));
        CollectionAssert.AreEqual(
            new long[] { 70, 50, 40 },
            projection.GetRows(CountryQuotaReviewScope.WorldQualifiers)
                .Select(static row => row.CountryId)
                .ToArray());
        CollectionAssert.AreEqual(
            new long[] { 70, 30, 40 },
            projection.GetRows(CountryQuotaReviewScope.EuropeanQualifiers)
                .Select(static row => row.CountryId)
                .ToArray());
        CollectionAssert.AreEqual(
            new long[] { 50, 40, 30, 10 },
            projection.GetRows(CountryQuotaReviewScope.Changes)
                .Select(static row => row.CountryId)
                .ToArray());
        CollectionAssert.AreEqual(originalOrder, preview.Changes.ToArray());
        Assert.IsTrue(projection.AllRows.Zip(preview.Changes).All(pair =>
            ReferenceEquals(pair.First.Source, pair.Second)));
    }

    [TestMethod]
    public void DefaultsAreBestRankFirstForEachReviewScope()
    {
        Assert.AreEqual(
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.WorldRank, SortDirection.Ascending),
            CountryQuotaReviewProjection.GetDefaultSort(CountryQuotaReviewScope.Changes));
        Assert.AreEqual(
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.WorldRank, SortDirection.Ascending),
            CountryQuotaReviewProjection.GetDefaultSort(CountryQuotaReviewScope.WorldQualifiers));
        Assert.AreEqual(
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.EuropeanRank, SortDirection.Ascending),
            CountryQuotaReviewProjection.GetDefaultSort(CountryQuotaReviewScope.EuropeanQualifiers));
    }

    [TestMethod]
    public void SortsEveryFieldInBothDirectionsWithUnrankedCountriesLast()
    {
        var preview = Preview(
            Change(20, "z", "BBB", 10, 2, null),
            Change(10, "c", "aaa", 20, 0, 3),
            Change(30, "b", "CCC", 15, 1, 2),
            Change(40, "A2", "aaa", 20, 3, 1));
        CountryQuotaReviewProjection projection = CountryQuotaReviewProjection.Create(preview);

        AssertOrder(projection, CountryQuotaReviewSortField.CountryCode, SortDirection.Ascending, 40, 10, 20, 30);
        AssertOrder(projection, CountryQuotaReviewSortField.CountryCode, SortDirection.Descending, 30, 20, 40, 10);
        AssertOrder(projection, CountryQuotaReviewSortField.UciPoints, SortDirection.Ascending, 20, 30, 40, 10);
        AssertOrder(projection, CountryQuotaReviewSortField.UciPoints, SortDirection.Descending, 40, 10, 30, 20);
        AssertOrder(projection, CountryQuotaReviewSortField.WorldRank, SortDirection.Ascending, 30, 20, 40, 10);
        AssertOrder(projection, CountryQuotaReviewSortField.WorldRank, SortDirection.Descending, 40, 20, 30, 10);
        AssertOrder(projection, CountryQuotaReviewSortField.EuropeanRank, SortDirection.Ascending, 40, 30, 10, 20);
        AssertOrder(projection, CountryQuotaReviewSortField.EuropeanRank, SortDirection.Descending, 10, 30, 40, 20);
    }

    [TestMethod]
    public void StableTieBreaksUseCanonicalCodeThenRawCodeThenCountryId()
    {
        var preview = Preview(
            Change(8, "same", "alias", 10, 1, 1),
            Change(7, "SAME", "ALIAS", 10, 1, 1),
            Change(6, "aaa", "alias", 10, 1, 1),
            Change(5, "zzz", "beta", 10, 1, 1));
        CountryQuotaReviewProjection projection = CountryQuotaReviewProjection.Create(preview);

        IReadOnlyList<CountryQuotaReviewRow> first = projection.GetRows(
            CountryQuotaReviewScope.Changes,
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.UciPoints, SortDirection.Descending));
        IReadOnlyList<CountryQuotaReviewRow> second = projection.GetRows(
            CountryQuotaReviewScope.Changes,
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.UciPoints, SortDirection.Descending));

        CollectionAssert.AreEqual(new long[] { 6, 7, 8, 5 }, first.Select(static row => row.CountryId).ToArray());
        CollectionAssert.AreEqual(
            first.Select(static row => row.CountryId).ToArray(),
            second.Select(static row => row.CountryId).ToArray());
        CollectionAssert.AreEqual(new long[] { 8, 7, 6, 5 }, preview.Changes.Select(static row => row.CountryId).ToArray());
    }

    [TestMethod]
    public void RowsNormalizeRanksAndDescribeAliasesWithoutDuplicatingCodeArrows()
    {
        CountryQuotaChange alias = Change(
            1,
            "CHI",
            "CHN",
            1234.5,
            0,
            0,
            new CountryQuotaValues(6, 2, 4, 2),
            new CountryQuotaValues(8, 2, 0, 0));
        CountryQuotaReviewRow row = Assert.ContainsSingle(CountryQuotaReviewProjection.Create(
            Preview(alias)).AllRows);

        Assert.IsNull(row.WorldRank);
        Assert.IsNull(row.EuropeanRank);
        Assert.AreEqual("Not ranked", row.WorldRankText);
        Assert.AreEqual("Not ranked", row.EuropeanRankText);
        Assert.IsTrue(row.HasStoredCodeAlias);
        Assert.AreEqual("Stored code: CHI", row.StoredCodeLabel);
        string summary = row.BuildAccessibleSummary(CultureInfo.InvariantCulture);
        StringAssert.StartsWith(
            summary,
            "CHN. Stored code: CHI. UCI points 1,234.50.",
            StringComparison.Ordinal);
        StringAssert.Contains(
            summary,
            "World Championship: not ranked; road quota 6 to 8;",
            StringComparison.Ordinal);
        StringAssert.Contains(
            summary,
            "European Championship: not ranked; road quota 4 to 0;",
            StringComparison.Ordinal);
        Assert.IsFalse(summary.Contains("->", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("CHI to CHN", StringComparison.Ordinal));

        CountryQuotaReviewRow canonical = Assert.ContainsSingle(CountryQuotaReviewProjection.Create(
            Preview(Change(2, "DEU", "DEU", 1, 1, null))).AllRows);
        Assert.IsFalse(canonical.HasStoredCodeAlias);
        Assert.IsNull(canonical.StoredCodeLabel);
        Assert.IsFalse(canonical
            .BuildAccessibleSummary(CultureInfo.InvariantCulture)
            .Contains("Stored code", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QuotaValuesDistinguishUnchangedIncreaseDecreaseAndReset()
    {
        var unchanged = new CountryQuotaReviewValue(2, 2);
        var increase = new CountryQuotaReviewValue(4, 8);
        var decrease = new CountryQuotaReviewValue(8, 6);
        var reset = new CountryQuotaReviewValue(4, 0);

        Assert.AreEqual(CountryQuotaValueChangeKind.Unchanged, unchanged.ChangeKind);
        Assert.AreEqual(CountryQuotaValueChangeKind.Increase, increase.ChangeKind);
        Assert.AreEqual(CountryQuotaValueChangeKind.Decrease, decrease.ChangeKind);
        Assert.AreEqual(CountryQuotaValueChangeKind.Reset, reset.ChangeKind);
        Assert.AreEqual("2, unchanged", unchanged.DisplayText);
        Assert.AreEqual("4 → 8", increase.DisplayText);
        Assert.AreEqual("8 → 6", decrease.DisplayText);
        Assert.AreEqual("4 → 0", reset.DisplayText);
    }

    private static readonly CountryQuotaValues Zero = new(0, 0, 0, 0);

    private static CountryQuotaPreview Preview(params CountryQuotaChange[] changes) => new(
        "snapshot",
        new DateOnly(2027, 11, 1),
        changes,
        changes.Count(static change => change.WorldRank is >= 1 and <= 25),
        changes.Count(static change => change.EuropeanRank is >= 1 and <= 18));

    private static CountryQuotaChange Change(
        long id,
        string rawCode,
        string canonicalCode,
        double points,
        int worldRank,
        int? europeanRank,
        CountryQuotaValues? oldValues = null,
        CountryQuotaValues? newValues = null) => new(
            id,
            rawCode,
            canonicalCode,
            canonicalCode.Equals(rawCode, StringComparison.OrdinalIgnoreCase)
                ? canonicalCode
                : $"{canonicalCode} ({rawCode})",
            points,
            worldRank,
            europeanRank,
            oldValues ?? Zero,
            newValues ?? new CountryQuotaValues(1, 0, 0, 0));

    private static void AssertOrder(
        CountryQuotaReviewProjection projection,
        CountryQuotaReviewSortField field,
        SortDirection direction,
        params long[] expectedIds)
    {
        long[] actual = projection.GetRows(
                CountryQuotaReviewScope.Changes,
                new CountryQuotaReviewSort(field, direction))
            .Select(static row => row.CountryId)
            .ToArray();
        CollectionAssert.AreEqual(expectedIds, actual);
    }
}
