using System.Globalization;
using System.Text;

namespace PcmCdbEditor.Domain;

public enum MaintenanceToolKind
{
    RiderRecovery,
    RiderCreation,
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

public enum RiderRecoveryTargetKind
{
    RiderIds,
    Team
}

public sealed record RiderRecoveryTarget
{
    private RiderRecoveryTarget(
        RiderRecoveryTargetKind kind,
        IEnumerable<long>? riderIds,
        long? teamId)
    {
        Kind = kind;
        RiderIds = ModelCollections.Freeze((riderIds ?? []).Distinct().Order());
        TeamId = teamId;
        if (kind == RiderRecoveryTargetKind.RiderIds && TeamId is not null)
        {
            throw new ArgumentException("A rider-ID target cannot include a team.", nameof(teamId));
        }

        if (kind == RiderRecoveryTargetKind.Team && (TeamId is null or <= 0 || RiderIds.Count != 0))
        {
            throw new ArgumentException("A team target requires one positive team ID and no rider IDs.", nameof(teamId));
        }

        if (RiderIds.Any(static id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(riderIds), "Rider IDs must be positive.");
        }
    }

    public RiderRecoveryTargetKind Kind { get; }

    public IReadOnlyList<long> RiderIds { get; }

    public long? TeamId { get; }

    public static RiderRecoveryTarget ForRiderIds(IEnumerable<long> riderIds) =>
        new(RiderRecoveryTargetKind.RiderIds, riderIds, null);

    public static RiderRecoveryTarget ForTeam(long teamId) =>
        new(RiderRecoveryTargetKind.Team, [], teamId);
}

public sealed record RiderTeamOption(long TeamId, string DisplayName, long RiderCount)
{
    public override string ToString() => $"{DisplayName} · {TeamId} ({RiderCount:N0} riders)";
}

public enum RiderContractRole
{
    AbsoluteLeader = 0,
    AbsoluteSprinter = 1,
    Leader = 2,
    Sprinter = 3,
    ImportantRider = 4,
    LuxuryTeammate = 5,
    Teammate = 6
}

public sealed record RiderAbilityDefinition(
    string Key,
    string Label,
    string CurrentColumn,
    string LimitColumn);

public sealed record RiderAbilityInput
{
    public RiderAbilityInput(string key, int current, int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("An ability key is required.", nameof(key));
        }

        if (current is < 50 or > 85)
        {
            throw new ArgumentOutOfRangeException(nameof(current), "Current ability must be between 50 and 85.");
        }

        if (limit is < 50 or > 85)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Ability limit must be between 50 and 85 when entered.");
        }

        Key = key.Trim();
        Current = current;
        Limit = limit;
    }

    public string Key { get; }

    public int Current { get; }

    public int? Limit { get; }
}

public sealed record RiderLookupTarget(
    string SourceTable,
    string SourceColumn,
    string TargetTable,
    string TargetColumn,
    string? DisplayColumn,
    string Label);

public sealed record RiderLookupOption(long Id, string DisplayName, string? Context = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Context)
        ? $"{DisplayName} · {Id}"
        : $"{DisplayName} · {Context} · {Id}";
}

public static class RiderGameDisplayName
{
    public static string Generate(string? firstName, string? lastName)
    {
        string normalizedFirstName = firstName?.Trim() ?? string.Empty;
        string normalizedLastName = lastName?.Trim() ?? string.Empty;
        if (normalizedFirstName.Length == 0 || normalizedLastName.Length == 0)
        {
            return string.Empty;
        }

        Rune firstRune = normalizedFirstName.EnumerateRunes().First();
        return $"{normalizedLastName} {firstRune}.";
    }
}

public static class RiderFavoriteRaceList
{
    public static string Serialize(IEnumerable<long>? raceIds)
    {
        long[] ids = (raceIds ?? []).ToArray();
        Validate(ids);
        return ids.Length == 0
            ? "()"
            : $"({string.Join(',', ids.Select(static id => id.ToString(CultureInfo.InvariantCulture)))})";
    }

    public static void Validate(IEnumerable<long>? raceIds)
    {
        var seen = new HashSet<long>();
        foreach (long id in raceIds ?? [])
        {
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(raceIds), "Favorite race IDs must be positive.");
            }

            if (!seen.Add(id))
            {
                throw new ArgumentException("Favorite race IDs must be unique.", nameof(raceIds));
            }
        }
    }
}

public sealed record RiderCreationField(
    string TableName,
    ColumnSchema Column,
    string Label,
    bool IsLocked,
    bool UsesDatabaseDefault,
    SqliteValue Value,
    RiderLookupTarget? LookupTarget = null)
{
    public bool IsEditable => !IsLocked && !Column.IsGenerated && !Column.IsHidden &&
        Column.Affinity != SqliteAffinity.Blob;
}

public sealed record RiderCreationDraft
{
    public RiderCreationDraft(
        DateOnly saveDate,
        string riderIdentityColumn,
        string contractIdentityColumn,
        IEnumerable<RiderAbilityDefinition> abilities,
        IEnumerable<RiderCreationField> fields,
        RiderLookupTarget favoriteRaceLookupTarget,
        int? observedMinimumHeight = null,
        int? observedMaximumHeight = null,
        int? observedMinimumWeight = null,
        int? observedMaximumWeight = null)
    {
        SaveDate = saveDate;
        RiderIdentityColumn = riderIdentityColumn;
        ContractIdentityColumn = contractIdentityColumn;
        Abilities = ModelCollections.Freeze(abilities);
        Fields = ModelCollections.Freeze(fields);
        FavoriteRaceLookupTarget = favoriteRaceLookupTarget
            ?? throw new ArgumentNullException(nameof(favoriteRaceLookupTarget));
        ObservedMinimumHeight = observedMinimumHeight;
        ObservedMaximumHeight = observedMaximumHeight;
        ObservedMinimumWeight = observedMinimumWeight;
        ObservedMaximumWeight = observedMaximumWeight;
    }

    public DateOnly SaveDate { get; }

    public int SaveYear => SaveDate.Year;

    public string RiderIdentityColumn { get; }

    public string ContractIdentityColumn { get; }

    public IReadOnlyList<RiderAbilityDefinition> Abilities { get; }

    public IReadOnlyList<RiderCreationField> Fields { get; }

    public RiderLookupTarget FavoriteRaceLookupTarget { get; }

    public int? ObservedMinimumHeight { get; }

    public int? ObservedMaximumHeight { get; }

    public int? ObservedMinimumWeight { get; }

    public int? ObservedMaximumWeight { get; }
}

public sealed record RiderCreationInput
{
    public RiderCreationInput(
        string firstName,
        string lastName,
        long teamId,
        long regionId,
        long riderTypeId,
        DateOnly birthDate,
        int height,
        int weight,
        string? photo,
        string? soundName,
        IEnumerable<RiderAbilityInput> abilities,
        RiderContractRole role,
        long wage,
        int contractEndYear,
        bool missingLimitsAcknowledged = false,
        IEnumerable<KeyValuePair<string, SqliteValue>>? riderAdvancedValues = null,
        IEnumerable<KeyValuePair<string, SqliteValue>>? contractAdvancedValues = null,
        string? gameDisplayName = null,
        double potential = 3.0,
        IEnumerable<long>? favoriteRaceIds = null)
    {
        if (string.IsNullOrWhiteSpace(firstName))
        {
            throw new ArgumentException("A first name is required.", nameof(firstName));
        }

        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new ArgumentException("A last name is required.", nameof(lastName));
        }

        if (teamId <= 0 || regionId <= 0 || riderTypeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamId), "Team, region, and rider type IDs must be positive.");
        }

        if (height <= 0 || weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height and weight must be positive.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "The contract role is not supported.");
        }

        if (wage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wage), "Contract wage must be positive.");
        }

        if (contractEndYear <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contractEndYear), "A positive contract end year is required.");
        }

        if (gameDisplayName is not null && string.IsNullOrWhiteSpace(gameDisplayName))
        {
            throw new ArgumentException("A rider game display name is required.", nameof(gameDisplayName));
        }

        if (!double.IsFinite(potential)
            || potential is < 0.5 or > 6.0
            || Math.Abs((potential * 2) - Math.Round(potential * 2)) > 0.0000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(potential),
                "Potential must be from 0.5 to 6.0 in 0.5 increments.");
        }

        long[] frozenFavoriteRaceIds = (favoriteRaceIds ?? []).ToArray();
        RiderFavoriteRaceList.Validate(frozenFavoriteRaceIds);

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        TeamId = teamId;
        RegionId = regionId;
        RiderTypeId = riderTypeId;
        BirthDate = birthDate;
        Height = height;
        Weight = weight;
        Photo = photo?.Trim() ?? string.Empty;
        SoundName = soundName?.Trim() ?? string.Empty;
        Abilities = ModelCollections.Freeze(abilities);
        Role = role;
        Wage = wage;
        ContractEndYear = contractEndYear;
        MissingLimitsAcknowledged = missingLimitsAcknowledged;
        RiderAdvancedValues = ModelCollections.FreezeDictionary(riderAdvancedValues);
        ContractAdvancedValues = ModelCollections.FreezeDictionary(contractAdvancedValues);
        GameDisplayName = gameDisplayName?.Trim()
            ?? RiderGameDisplayName.Generate(FirstName, LastName);
        Potential = potential;
        FavoriteRaceIds = ModelCollections.Freeze(frozenFavoriteRaceIds);
    }

    public string FirstName { get; }

    public string LastName { get; }

    public long TeamId { get; }

    public long RegionId { get; }

    public long RiderTypeId { get; }

    public DateOnly BirthDate { get; }

    public int Height { get; }

    public int Weight { get; }

    public string Photo { get; }

    public string SoundName { get; }

    public IReadOnlyList<RiderAbilityInput> Abilities { get; }

    public RiderContractRole Role { get; }

    public long Wage { get; }

    public int ContractEndYear { get; }

    public bool MissingLimitsAcknowledged { get; }

    public IReadOnlyDictionary<string, SqliteValue> RiderAdvancedValues { get; }

    public IReadOnlyDictionary<string, SqliteValue> ContractAdvancedValues { get; }

    public string GameDisplayName { get; }

    public double Potential { get; }

    public IReadOnlyList<long> FavoriteRaceIds { get; }
}

public sealed record RiderCreationPreview
{
    public RiderCreationPreview(
        string snapshotToken,
        RiderCreationInput input,
        string riderIdentityColumn,
        string contractIdentityColumn,
        long newCyclistId,
        long newContractId,
        IEnumerable<string> missingLimitKeys,
        IEnumerable<string> warnings,
        IEnumerable<RiderLookupOption> favoriteRaces,
        IEnumerable<KeyValuePair<string, SqliteValue>> riderValues,
        IEnumerable<KeyValuePair<string, SqliteValue>> contractValues)
    {
        SnapshotToken = string.IsNullOrWhiteSpace(snapshotToken)
            ? throw new ArgumentException("A rider-creation snapshot token is required.", nameof(snapshotToken))
            : snapshotToken;
        Input = input ?? throw new ArgumentNullException(nameof(input));
        RiderIdentityColumn = riderIdentityColumn;
        ContractIdentityColumn = contractIdentityColumn;
        NewCyclistId = newCyclistId;
        NewContractId = newContractId;
        MissingLimitKeys = ModelCollections.Freeze(missingLimitKeys);
        Warnings = ModelCollections.Freeze(warnings);
        FavoriteRaces = ModelCollections.Freeze(favoriteRaces);
        RiderValues = ModelCollections.FreezeDictionary(riderValues);
        ContractValues = ModelCollections.FreezeDictionary(contractValues);
    }

    public string SnapshotToken { get; }

    public RiderCreationInput Input { get; }

    public string RiderIdentityColumn { get; }

    public string ContractIdentityColumn { get; }

    public long NewCyclistId { get; }

    public long NewContractId { get; }

    public IReadOnlyList<string> MissingLimitKeys { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<RiderLookupOption> FavoriteRaces { get; }

    public IReadOnlyDictionary<string, SqliteValue> RiderValues { get; }

    public IReadOnlyDictionary<string, SqliteValue> ContractValues { get; }
}

public sealed record RiderRecoveryChange(long CyclistId, RiderRecoveryValues OldValues, RiderRecoveryValues NewValues);

public sealed record RiderRecoveryPreview
{
    public RiderRecoveryPreview(string snapshotToken, IEnumerable<long> cyclistIds, IEnumerable<RiderRecoveryChange> changes)
        : this(
            snapshotToken,
            RiderRecoveryTarget.ForRiderIds(cyclistIds),
            cyclistIds,
            changes)
    {
    }

    public RiderRecoveryPreview(
        string snapshotToken,
        RiderRecoveryTarget target,
        IEnumerable<long> cyclistIds,
        IEnumerable<RiderRecoveryChange> changes)
    {
        SnapshotToken = snapshotToken;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        CyclistIds = ModelCollections.Freeze(cyclistIds.Distinct().Order());
        Changes = ModelCollections.Freeze(changes);
        FoundCyclistIds = ModelCollections.Freeze(Changes.Select(static change => change.CyclistId).Distinct().Order());
        MissingCyclistIds = ModelCollections.Freeze(CyclistIds.Except(FoundCyclistIds).Order());
    }

    public string SnapshotToken { get; }

    public RiderRecoveryTarget Target { get; }

    public IReadOnlyList<long> CyclistIds { get; }

    public IReadOnlyList<RiderRecoveryChange> Changes { get; }

    public IReadOnlyList<long> FoundCyclistIds { get; }

    public IReadOnlyList<long> MissingCyclistIds { get; }

    public int RowsNeedingChanges => Changes.Count(static change => change.OldValues != change.NewValues);
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
