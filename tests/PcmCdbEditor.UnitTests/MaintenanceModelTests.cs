using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MaintenanceModelTests
{
    private static readonly string[] MountainOnly = ["mountain"];

    private static readonly int[] ExpectedRoleCodes = [0, 1, 2, 3, 4, 5, 6];

    [TestMethod]
    public void RiderRecoveryDefaultMatchesApprovedPreset()
    {
        var preset = RiderRecoveryValues.Default;

        Assert.AreEqual(99d, preset.Fit);
        Assert.AreEqual(0d, preset.Injury);
        Assert.AreEqual(0L, preset.InjuryDays);
        Assert.AreEqual(0d, preset.PhysicalFatigue);
        Assert.AreEqual(100d, preset.Freshness);
        Assert.AreEqual(99d, preset.Preparation);
    }

    [TestMethod]
    public void RiderPreviewNormalizesIdsButRetainsTypedChangeSnapshots()
    {
        var oldValues = new RiderRecoveryValues(1, 2, 3, 4, 5, 6);
        var change = new RiderRecoveryChange(7, oldValues, RiderRecoveryValues.Default);
        var preview = new RiderRecoveryPreview("snapshot", [7, 7, 5], [change]);

        CollectionAssert.AreEqual(new long[] { 5, 7 }, preview.CyclistIds.ToArray());
        Assert.AreEqual(oldValues, preview.Changes[0].OldValues);
        Assert.AreEqual("snapshot", preview.SnapshotToken);
    }

    [TestMethod]
    public void RecoveryTargetsAreImmutableAndRequirePositiveUnambiguousIds()
    {
        var source = new List<long> { 9, 3, 9 };
        RiderRecoveryTarget ids = RiderRecoveryTarget.ForRiderIds(source);
        source.Add(12);

        CollectionAssert.AreEqual(new long[] { 3, 9 }, ids.RiderIds.ToArray());
        Assert.AreEqual(RiderRecoveryTargetKind.RiderIds, ids.Kind);
        Assert.AreEqual(44L, RiderRecoveryTarget.ForTeam(44).TeamId);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            RiderRecoveryTarget.ForRiderIds([1, 0]));
        Assert.ThrowsExactly<ArgumentException>(() => RiderRecoveryTarget.ForTeam(0));
    }

    [TestMethod]
    public void RiderPreviewReportsFoundMissingAndChangedRows()
    {
        var alreadyRecovered = new RiderRecoveryChange(
            3,
            RiderRecoveryValues.Default,
            RiderRecoveryValues.Default);
        var changed = new RiderRecoveryChange(
            7,
            new RiderRecoveryValues(1, 2, 3, 4, 5, 6),
            RiderRecoveryValues.Default);
        var preview = new RiderRecoveryPreview(
            "snapshot",
            RiderRecoveryTarget.ForRiderIds([3, 7, 11]),
            [3, 7, 11],
            [alreadyRecovered, changed]);

        CollectionAssert.AreEqual(new long[] { 3, 7 }, preview.FoundCyclistIds.ToArray());
        CollectionAssert.AreEqual(new long[] { 11 }, preview.MissingCyclistIds.ToArray());
        Assert.AreEqual(1, preview.RowsNeedingChanges);
    }

    [TestMethod]
    public void CapabilityFreezesDetailedMissingSchemaReasons()
    {
        var missingTables = new List<string> { "table_a" };
        var capability = new MaintenanceCapability(
            MaintenanceToolKind.RiderRecovery,
            false,
            ["Required schema is missing."],
            missingTables,
            ["table_a.column_b"]);
        missingTables.Add("later");

        Assert.IsFalse(capability.IsEnabled);
        Assert.HasCount(1, capability.MissingTables);
        Assert.AreEqual("table_a.column_b", capability.MissingColumns[0]);
    }

    [TestMethod]
    public void RiderAbilityInputEnforcesRequiredRangeAndNullableLimits()
    {
        var omittedLimit = new RiderAbilityInput("mountain", 72);
        var enteredLimit = new RiderAbilityInput("timetrial", 78, 75);

        Assert.IsNull(omittedLimit.Limit);
        Assert.AreEqual(75, enteredLimit.Limit);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RiderAbilityInput("plain", 49));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RiderAbilityInput("plain", 86));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RiderAbilityInput("plain", 70, 49));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RiderAbilityInput("plain", 70, 86));
    }

    [TestMethod]
    public void RiderCreationInputAndPreviewFreezeEveryTypedValue()
    {
        var abilities = CreateAbilities().ToList();
        var favoriteRaceIds = new List<long> { 11, 43, 25 };
        var favoriteRaces = new List<RiderLookupOption>
        {
            new(11, "Ronde van Vlaanderen", "Bel · Cwt majeures")
        };
        var riderValues = new Dictionary<string, SqliteValue>
        {
            ["gene_sz_firstname"] = SqliteValue.Text("Created")
        };
        var input = CreateInput(abilities, riderValues, favoriteRaceIds: favoriteRaceIds);
        abilities[0] = new RiderAbilityInput("plain", 85, 85);
        favoriteRaceIds.Add(87);
        riderValues["gene_sz_firstname"] = SqliteValue.Text("Changed later");
        var preview = new RiderCreationPreview(
            "token",
            input,
            "IDcyclist",
            "IDcontract_cyclist",
            8,
            21,
            ["mountain"],
            ["Current Mountain is above its entered Limit."],
            favoriteRaces,
            [KeyValuePair.Create("IDcyclist", SqliteValue.Integer(8))],
            [KeyValuePair.Create("IDcontract_cyclist", SqliteValue.Integer(21))]);
        favoriteRaces.Clear();

        Assert.AreEqual(70, input.Abilities[0].Current);
        CollectionAssert.AreEqual(new long[] { 11, 43, 25 }, input.FavoriteRaceIds.ToArray());
        Assert.AreEqual("Created", input.RiderAdvancedValues["gene_sz_firstname"].TextValue);
        Assert.AreEqual(8L, preview.RiderValues["IDcyclist"].IntegerValue);
        Assert.AreEqual(21L, preview.ContractValues["IDcontract_cyclist"].IntegerValue);
        CollectionAssert.AreEqual(MountainOnly, preview.MissingLimitKeys.ToArray());
        Assert.HasCount(1, preview.FavoriteRaces);
    }

    [TestMethod]
    public void RiderCreationRequiresIdentityProfileAndContractInputs()
    {
        IReadOnlyList<RiderAbilityInput> abilities = CreateAbilities();

        Assert.ThrowsExactly<ArgumentException>(() => CreateInput(abilities, firstName: " "));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInput(abilities, lastName: " "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, teamId: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, height: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, wage: 0));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInput(abilities, gameDisplayName: " "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, potential: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, potential: 6.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, potential: 2.75));
        Assert.ThrowsExactly<ArgumentException>(() => CreateInput(abilities, favoriteRaceIds: [11, 11]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(abilities, favoriteRaceIds: [0]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateInput(
            abilities,
            role: (RiderContractRole)7));
    }

    [TestMethod]
    public void RiderContractRoleCodesMatchTheProductMapping()
    {
        CollectionAssert.AreEqual(
            ExpectedRoleCodes,
            Enum.GetValues<RiderContractRole>().Select(static role => (int)role).ToArray());
    }

    [TestMethod]
    public void RiderLookupOptionRendersNameContextAndStoredId()
    {
        Assert.AreEqual("Denmark · 2801", new RiderLookupOption(2801, "Denmark").ToString());
        Assert.AreEqual(
            "Capital Region · Denmark · 2817",
            new RiderLookupOption(2817, "Capital Region", "Denmark").ToString());
    }

    [TestMethod]
    public void RiderGameDisplayNameAutoSyncsUntilOverriddenAndCanReset()
    {
        var state = new RiderGameDisplayNameState();

        Assert.IsTrue(state.UpdateNames("Øivind", "Hansen"));
        Assert.AreEqual("Hansen Ø.", state.Value);
        state.Override("Custom game name");
        Assert.IsFalse(state.UpdateNames("Peter", "Andersen"));
        Assert.AreEqual("Custom game name", state.Value);

        state.Reset("Peter", "Andersen");
        Assert.IsFalse(state.IsOverridden);
        Assert.AreEqual("Andersen P.", state.Value);
    }

    [TestMethod]
    public void FavoriteRaceListSerializesOrderedUniqueIdsExactly()
    {
        Assert.AreEqual("()", RiderFavoriteRaceList.Serialize([]));
        Assert.AreEqual("(11,43,25)", RiderFavoriteRaceList.Serialize([11, 43, 25]));
        Assert.ThrowsExactly<ArgumentException>(() => RiderFavoriteRaceList.Serialize([11, 11]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RiderFavoriteRaceList.Serialize([-1]));
    }

    [TestMethod]
    public void CreateCommandEnablesOnlyAfterBusyPreviewOperationEnds()
    {
        Assert.IsFalse(RiderCreationCommandAvailability.CanCreate(
            hasCurrentPreview: true,
            missingLimitCount: 0,
            missingLimitsAcknowledged: false,
            isBusy: true,
            hasExclusiveOperation: true));
        Assert.IsTrue(RiderCreationCommandAvailability.CanCreate(
            hasCurrentPreview: true,
            missingLimitCount: 0,
            missingLimitsAcknowledged: false,
            isBusy: false,
            hasExclusiveOperation: false));
        Assert.IsFalse(RiderCreationCommandAvailability.CanCreate(
            hasCurrentPreview: true,
            missingLimitCount: 1,
            missingLimitsAcknowledged: false,
            isBusy: false,
            hasExclusiveOperation: false));
        Assert.IsTrue(RiderCreationCommandAvailability.CanCreate(
            hasCurrentPreview: true,
            missingLimitCount: 1,
            missingLimitsAcknowledged: true,
            isBusy: false,
            hasExclusiveOperation: false));
    }

    private static IReadOnlyList<RiderAbilityInput> CreateAbilities() =>
    [
        new("plain", 70, 75),
        new("mountain", 70),
        new("medium_mountain", 70, 75),
        new("downhilling", 70, 75),
        new("cobble", 70, 75),
        new("timetrial", 70, 75),
        new("prologue", 70, 75),
        new("sprint", 70, 75),
        new("acceleration", 70, 75),
        new("endurance", 70, 75),
        new("resistance", 70, 75),
        new("recuperation", 70, 75),
        new("hill", 70, 75),
        new("baroudeur", 70, 75)
    ];

    private static RiderCreationInput CreateInput(
        IEnumerable<RiderAbilityInput> abilities,
        Dictionary<string, SqliteValue>? riderValues = null,
        string firstName = "Ada",
        string lastName = "Lovelace",
        long teamId = 11,
        int height = 172,
        long wage = 12000,
        RiderContractRole role = RiderContractRole.Leader,
        string? gameDisplayName = null,
        double potential = 3.0,
        IEnumerable<long>? favoriteRaceIds = null) =>
        new(
            firstName,
            lastName,
            teamId,
            regionId: 2801,
            riderTypeId: 3,
            new DateOnly(1998, 12, 10),
            height,
            weight: 61,
            photo: null,
            soundName: null,
            abilities,
            role,
            wage,
            contractEndYear: 2030,
            missingLimitsAcknowledged: true,
            riderAdvancedValues: riderValues,
            gameDisplayName: gameDisplayName,
            potential: potential,
            favoriteRaceIds: favoriteRaceIds);
}
