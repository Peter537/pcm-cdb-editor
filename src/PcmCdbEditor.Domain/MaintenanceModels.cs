namespace PcmCdbEditor.Domain;

public enum MaintenanceToolKind
{
    RiderRecovery,
    JanuaryFirstSeasonStageRepair,
    CountryChampionshipQuota
}

public sealed record MaintenanceCapability
{
    public MaintenanceCapability(
        MaintenanceToolKind tool,
        bool isEnabled,
        IEnumerable<string>? reasons = null,
        IEnumerable<string>? missingTables = null,
        IEnumerable<string>? missingColumns = null)
    {
        Tool = tool;
        IsEnabled = isEnabled;
        Reasons = ModelCollections.Freeze(reasons);
        MissingTables = ModelCollections.Freeze(missingTables);
        MissingColumns = ModelCollections.Freeze(missingColumns);
    }

    public MaintenanceToolKind Tool { get; }

    public bool IsEnabled { get; }

    public IReadOnlyList<string> Reasons { get; }

    public IReadOnlyList<string> MissingTables { get; }

    public IReadOnlyList<string> MissingColumns { get; }
}

public sealed record RiderRecoveryValues(
    double Fit,
    double Injury,
    long InjuryDays,
    double PhysicalFatigue,
    double Freshness,
    double Preparation)
{
    public static RiderRecoveryValues Default { get; } = new(99, 0, 0, 0, 100, 99);
}

public sealed record RiderRecoveryChange(long CyclistId, RiderRecoveryValues OldValues, RiderRecoveryValues NewValues);

public sealed record RiderRecoveryPreview
{
    public RiderRecoveryPreview(string snapshotToken, IEnumerable<long> cyclistIds, IEnumerable<RiderRecoveryChange> changes)
    {
        SnapshotToken = snapshotToken;
        CyclistIds = ModelCollections.Freeze(cyclistIds.Distinct().Order());
        Changes = ModelCollections.Freeze(changes);
    }

    public string SnapshotToken { get; }

    public IReadOnlyList<long> CyclistIds { get; }

    public IReadOnlyList<RiderRecoveryChange> Changes { get; }
}

public sealed record JanuaryFirstRepairPreview(string SnapshotToken, DateOnly CurrentDate, long RowCount);

public sealed record MaintenanceApplyResult(
    int AffectedRows,
    string Summary,
    MaintenanceEditOperation? HistoryOperation = null,
    IReadOnlyList<RowReplayGuard>? UndoGuards = null);

public sealed record CountryQuotaValues(long WorldRoad, long WorldTimeTrial, long EuropeanRoad, long EuropeanTimeTrial);

public sealed record CountryQuotaChange(
    long CountryId,
    string RawCode,
    string CanonicalCode,
    string DisplayName,
    double UciPoints,
    int WorldRank,
    int? EuropeanRank,
    CountryQuotaValues OldValues,
    CountryQuotaValues NewValues);

public sealed record CountryQuotaPreview
{
    public CountryQuotaPreview(
        string snapshotToken,
        DateOnly currentDate,
        IEnumerable<CountryQuotaChange> changes,
        int worldQualifierCount,
        int europeanQualifierCount)
    {
        SnapshotToken = snapshotToken;
        CurrentDate = currentDate;
        Changes = ModelCollections.Freeze(changes);
        WorldQualifierCount = worldQualifierCount;
        EuropeanQualifierCount = europeanQualifierCount;
    }

    public string SnapshotToken { get; }

    public DateOnly CurrentDate { get; }

    public IReadOnlyList<CountryQuotaChange> Changes { get; }

    public int WorldQualifierCount { get; }

    public int EuropeanQualifierCount { get; }
}
