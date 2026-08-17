using System.Collections.ObjectModel;
using System.Globalization;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

internal enum CountryQuotaReviewScope
{
    Changes,
    WorldQualifiers,
    EuropeanQualifiers
}

internal enum CountryQuotaReviewSortField
{
    CountryCode,
    UciPoints,
    WorldRank,
    EuropeanRank
}

internal enum CountryQuotaValueChangeKind
{
    Unchanged,
    Increase,
    Decrease,
    Reset
}

internal sealed record CountryQuotaReviewSort(
    CountryQuotaReviewSortField Field,
    SortDirection Direction);

internal sealed record CountryQuotaReviewValue(long OldValue, long NewValue)
{
    public bool IsChanged => OldValue != NewValue;

    public CountryQuotaValueChangeKind ChangeKind => (OldValue, NewValue) switch
    {
        var (oldValue, newValue) when oldValue == newValue => CountryQuotaValueChangeKind.Unchanged,
        (_, 0) => CountryQuotaValueChangeKind.Reset,
        var (oldValue, newValue) when newValue > oldValue => CountryQuotaValueChangeKind.Increase,
        _ => CountryQuotaValueChangeKind.Decrease
    };

    public string DisplayText => IsChanged
        ? $"{OldValue:N0} → {NewValue:N0}"
        : $"{NewValue:N0}, unchanged";

    public string AccessibleText => IsChanged
        ? $"{OldValue:N0} to {NewValue:N0}"
        : $"{NewValue:N0}, unchanged";
}

internal sealed class CountryQuotaReviewRow
{
    public CountryQuotaReviewRow(CountryQuotaChange source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        WorldRoadQuota = new CountryQuotaReviewValue(source.OldValues.WorldRoad, source.NewValues.WorldRoad);
        WorldTimeTrialQuota = new CountryQuotaReviewValue(
            source.OldValues.WorldTimeTrial,
            source.NewValues.WorldTimeTrial);
        EuropeanRoadQuota = new CountryQuotaReviewValue(
            source.OldValues.EuropeanRoad,
            source.NewValues.EuropeanRoad);
        EuropeanTimeTrialQuota = new CountryQuotaReviewValue(
            source.OldValues.EuropeanTimeTrial,
            source.NewValues.EuropeanTimeTrial);
    }

    public CountryQuotaChange Source { get; }

    public long CountryId => Source.CountryId;

    public string RawCode => Source.RawCode;

    public string CanonicalCode => Source.CanonicalCode;

    public double UciPoints => Source.UciPoints;

    public string UciPointsText => $"{UciPoints:N2}";

    public int? WorldRank => Source.WorldRank > 0 ? Source.WorldRank : null;

    public int? EuropeanRank => Source.EuropeanRank is > 0 ? Source.EuropeanRank : null;

    public bool HasStoredCodeAlias => !CanonicalCode.Equals(RawCode, StringComparison.OrdinalIgnoreCase);

    public string? StoredCodeLabel => HasStoredCodeAlias ? $"Stored code: {RawCode}" : null;

    public bool HasChanges => WorldRoadQuota.IsChanged ||
        WorldTimeTrialQuota.IsChanged ||
        EuropeanRoadQuota.IsChanged ||
        EuropeanTimeTrialQuota.IsChanged;

    public bool IsWorldQualifier => WorldRank is >= 1 and <= 25;

    public bool IsEuropeanQualifier => EuropeanRank is >= 1 and <= 18;

    public CountryQuotaReviewValue WorldRoadQuota { get; }

    public CountryQuotaReviewValue WorldTimeTrialQuota { get; }

    public CountryQuotaReviewValue EuropeanRoadQuota { get; }

    public CountryQuotaReviewValue EuropeanTimeTrialQuota { get; }

    public string WorldRankText => FormatRank(WorldRank);

    public string EuropeanRankText => FormatRank(EuropeanRank);

    public string AccessibleSummary => BuildAccessibleSummary(CultureInfo.CurrentCulture);

    public string BuildAccessibleSummary(IFormatProvider formatProvider)
    {
        ArgumentNullException.ThrowIfNull(formatProvider);
        string alias = HasStoredCodeAlias ? $" Stored code: {RawCode}." : string.Empty;
        return string.Format(
            formatProvider,
            "{0}.{1} UCI points {2:N2}. World Championship: {3}; road quota {4}; " +
            "time-trial quota {5}. European Championship: {6}; road quota {7}; " +
            "time-trial quota {8}.",
            CanonicalCode,
            alias,
            UciPoints,
            FormatAccessibleRank(WorldRank, formatProvider),
            FormatAccessibleValue(WorldRoadQuota, formatProvider),
            FormatAccessibleValue(WorldTimeTrialQuota, formatProvider),
            FormatAccessibleRank(EuropeanRank, formatProvider),
            FormatAccessibleValue(EuropeanRoadQuota, formatProvider),
            FormatAccessibleValue(EuropeanTimeTrialQuota, formatProvider));
    }

    private static string FormatRank(int? rank) => rank is null ? "Not ranked" : $"{rank.Value:N0}";

    private static string FormatAccessibleRank(int? rank, IFormatProvider formatProvider) => rank is null
        ? "not ranked"
        : string.Format(formatProvider, "rank {0:N0}", rank.Value);

    private static string FormatAccessibleValue(
        CountryQuotaReviewValue value,
        IFormatProvider formatProvider) => value.IsChanged
            ? string.Format(formatProvider, "{0:N0} to {1:N0}", value.OldValue, value.NewValue)
            : string.Format(formatProvider, "{0:N0}, unchanged", value.NewValue);
}

internal sealed class CountryQuotaReviewProjection
{
    private static readonly StringComparer CodeComparer = StringComparer.OrdinalIgnoreCase;
    private readonly ReadOnlyCollection<CountryQuotaReviewRow> _rows;

    private CountryQuotaReviewProjection(CountryQuotaPreview source)
    {
        Source = source;
        _rows = Array.AsReadOnly(source.Changes.Select(static change => new CountryQuotaReviewRow(change)).ToArray());
    }

    public CountryQuotaPreview Source { get; }

    public string SnapshotToken => Source.SnapshotToken;

    public DateOnly CurrentDate => Source.CurrentDate;

    public IReadOnlyList<CountryQuotaReviewRow> AllRows => _rows;

    public int TotalCountryCount => _rows.Count;

    public int ChangeCount => _rows.Count(static row => row.HasChanges);

    public int WorldQualifierCount => _rows.Count(static row => row.IsWorldQualifier);

    public int EuropeanQualifierCount => _rows.Count(static row => row.IsEuropeanQualifier);

    public static CountryQuotaReviewProjection Create(CountryQuotaPreview source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new CountryQuotaReviewProjection(source);
    }

    public static CountryQuotaReviewSort GetDefaultSort(CountryQuotaReviewScope scope) => scope switch
    {
        CountryQuotaReviewScope.Changes or CountryQuotaReviewScope.WorldQualifiers =>
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.WorldRank, SortDirection.Ascending),
        CountryQuotaReviewScope.EuropeanQualifiers =>
            new CountryQuotaReviewSort(CountryQuotaReviewSortField.EuropeanRank, SortDirection.Ascending),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public int GetCount(CountryQuotaReviewScope scope) => Filter(scope).Count();

    public IReadOnlyList<CountryQuotaReviewRow> GetRows(CountryQuotaReviewScope scope) =>
        GetRows(scope, GetDefaultSort(scope));

    public IReadOnlyList<CountryQuotaReviewRow> GetRows(
        CountryQuotaReviewScope scope,
        CountryQuotaReviewSort sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        if (!Enum.IsDefined(sort.Field))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        if (!Enum.IsDefined(sort.Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        CountryQuotaReviewRow[] rows = Filter(scope).ToArray();
        Array.Sort(rows, (left, right) => Compare(left, right, sort));
        return Array.AsReadOnly(rows);
    }

    private IEnumerable<CountryQuotaReviewRow> Filter(CountryQuotaReviewScope scope) => scope switch
    {
        CountryQuotaReviewScope.Changes => _rows.Where(static row => row.HasChanges),
        CountryQuotaReviewScope.WorldQualifiers => _rows.Where(static row => row.IsWorldQualifier),
        CountryQuotaReviewScope.EuropeanQualifiers => _rows.Where(static row => row.IsEuropeanQualifier),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private static int Compare(
        CountryQuotaReviewRow left,
        CountryQuotaReviewRow right,
        CountryQuotaReviewSort sort)
    {
        int primary = sort.Field switch
        {
            CountryQuotaReviewSortField.CountryCode => CompareDirected(
                left.CanonicalCode,
                right.CanonicalCode,
                sort.Direction,
                CodeComparer),
            CountryQuotaReviewSortField.UciPoints => CompareDirected(
                left.UciPoints,
                right.UciPoints,
                sort.Direction),
            CountryQuotaReviewSortField.WorldRank => CompareRank(
                left.WorldRank,
                right.WorldRank,
                sort.Direction),
            CountryQuotaReviewSortField.EuropeanRank => CompareRank(
                left.EuropeanRank,
                right.EuropeanRank,
                sort.Direction),
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        if (primary != 0)
        {
            return primary;
        }

        int canonical = CodeComparer.Compare(left.CanonicalCode, right.CanonicalCode);
        if (canonical != 0)
        {
            return canonical;
        }

        int raw = CodeComparer.Compare(left.RawCode, right.RawCode);
        return raw != 0 ? raw : left.CountryId.CompareTo(right.CountryId);
    }

    private static int CompareDirected<T>(
        T left,
        T right,
        SortDirection direction)
    {
        return direction == SortDirection.Ascending
            ? Comparer<T>.Default.Compare(left, right)
            : Comparer<T>.Default.Compare(right, left);
    }

    private static int CompareDirected(
        string left,
        string right,
        SortDirection direction,
        StringComparer comparer) => direction == SortDirection.Ascending
            ? comparer.Compare(left, right)
            : comparer.Compare(right, left);

    private static int CompareRank(int? left, int? right, SortDirection direction)
    {
        if (!left.HasValue)
        {
            return right.HasValue ? 1 : 0;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        return CompareDirected(left.Value, right.Value, direction);
    }
}
