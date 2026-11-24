using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Maintenance;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class RiderCreationServiceTests
{
    [TestMethod]
    public async Task PersistedWageAndAdvancedNumericValuesKeepTheFullIntegerRange()
    {
        const long exactNumeric = 9_007_199_254_740_993L;
        await using var database = await CreateDatabaseAsync(
            riderExtraColumn: "edge_integer INTEGER, edge_numeric NUMERIC").ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationInput input = CreateInput(true,
            riderAdvanced:
            [
                KeyValuePair.Create("edge_integer", SqliteValue.Integer(long.MinValue)),
                KeyValuePair.Create("edge_numeric", SqliteValue.Integer(exactNumeric)),
            ],
            wage: long.MaxValue);
        RiderCreationPreview preview = await service.PreviewAsync(database.Path, input, CancellationToken.None)
            .ConfigureAwait(false);
        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(long.MinValue, await database.ScalarAsync<long>(
            "SELECT edge_integer FROM DYN_cyclist WHERE edge_integer IS NOT NULL").ConfigureAwait(false));
        Assert.AreEqual(exactNumeric, await database.ScalarAsync<long>(
            "SELECT edge_numeric FROM DYN_cyclist WHERE edge_numeric IS NOT NULL").ConfigureAwait(false));
        Assert.AreEqual(long.MaxValue, await database.ScalarAsync<long>(
            "SELECT MAX(finan_i_period_wage) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    private static readonly string[] AbilityKeys =
    [
        "plain", "mountain", "medium_mountain", "downhilling", "cobble", "timetrial",
        "prologue", "sprint", "acceleration", "endurance", "resistance", "recuperation",
        "hill", "baroudeur"
    ];

    private static readonly string[] PlainOnly = ["plain"];

    [TestMethod]
    public async Task PrepareAndLookupExposeCleanDraftWithBoundedNamedReferences()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        var service = new RiderCreationService();

        MaintenanceCapability capability = await service.CheckCapabilityAsync(
            database.Path,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(capability.IsEnabled, string.Join(" ", capability.Reasons));

        RiderCreationDraft draft = await service.PrepareAsync(
            database.Path,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new DateOnly(2026, 7, 15), draft.SaveDate);
        Assert.AreEqual(180, draft.ObservedMinimumHeight);
        Assert.AreEqual(180, draft.ObservedMaximumHeight);
        Assert.AreEqual(70, draft.ObservedMinimumWeight);
        Assert.AreEqual(70, draft.ObservedMaximumWeight);
        Assert.AreEqual(14, draft.Abilities.Count);
        CollectionAssert.AreEqual(
            AbilityKeys,
            draft.Abilities.Select(static ability => ability.Key).ToArray());
        Assert.IsTrue(draft.Fields.Single(field => field.Column.Name == "fkIDcontract").IsLocked);
        Assert.IsFalse(draft.Fields.Single(field => field.Column.Name == "payload").IsEditable);
        Assert.IsTrue(draft.Fields.Single(field => field.Column.Name == "gene_sz_firstlastname").IsLocked);
        Assert.IsTrue(draft.Fields.Single(field => field.Column.Name == "value_f_potentiel").IsLocked);
        Assert.IsTrue(draft.Fields.Single(field => field.Column.Name == "gene_ilist_fkIDfavorite_races").IsLocked);
        Assert.IsTrue(draft.Fields.Single(field => field.Column.Name == "value_f_current_ability").IsEditable);
        Assert.IsNotNull(draft.Fields.Single(field => field.Column.Name == "fkIDcyclist_state").LookupTarget);

        RiderLookupTarget teamTarget = Lookup(draft, "DYN_cyclist", "fkIDteam");
        RiderLookupOption team = Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, teamTarget, "destination", 10, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("Destination Team · 11", team.ToString());
        Assert.AreEqual(team, Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, teamTarget, "11", 10, CancellationToken.None).ConfigureAwait(false)));

        RiderLookupTarget regionTarget = Lookup(draft, "DYN_cyclist", "fkIDregion");
        RiderLookupOption region = Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, regionTarget, "capital", 10, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("CAPITAL_REGION · DENMARK · 2801", region.ToString());

        RiderLookupTarget typeTarget = Lookup(draft, "DYN_cyclist", "fkIDtype_rider");
        Assert.AreEqual("CLIMBER · 3", Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, typeTarget, "climber", 10, CancellationToken.None).ConfigureAwait(false)).ToString());

        RiderLookupOption race = Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path,
            draft.FavoriteRaceLookupTarget,
            "rvv",
            10,
            CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("Ronde van Vlaanderen · BELGIUM · CWT_MAJEURES · 11", race.ToString());
        Assert.AreEqual(race, Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path,
            draft.FavoriteRaceLookupTarget,
            "11",
            10,
            CancellationToken.None).ConfigureAwait(false)));

        RiderLookupTarget preferenceTarget = Lookup(
            draft,
            "DYN_cyclist",
            "fkIDcontract_preference_preset");
        RiderLookupOption preference = Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, preferenceTarget, "4", 10, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("Preset 4 · Salary 60, nationality 20, role 20 · 4", preference.ToString());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.SearchLookupAsync(
            database.Path, teamTarget, string.Empty, 51, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PreviewBuildsCompleteCleanRowsAndPreservesNullableLimits()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationInput input = CreateInput(acknowledgeMissingLimits: false);

        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path,
            input,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(8L, preview.NewCyclistId);
        Assert.AreEqual(21L, preview.NewContractId);
        Assert.AreEqual(new RiderLookupOption(11, "Destination Team"), preview.Team);
        Assert.AreEqual(new RiderLookupOption(2801, "CAPITAL_REGION"), preview.Region);
        Assert.AreEqual(new RiderLookupOption(3, "CLIMBER"), preview.RiderType);
        CollectionAssert.AreEqual(
            AbilityKeys,
            preview.AbilityDefinitions.Select(static ability => ability.Key).ToArray());
        AssertSqliteInteger(preview.RiderValues, "IDcyclist", 8);
        AssertSqliteInteger(preview.RiderValues, "fkIDcontract", 21);
        AssertSqliteInteger(preview.RiderValues, "fkIDteam", 11);
        AssertSqliteInteger(preview.RiderValues, "fkIDregion", 2801);
        AssertSqliteInteger(preview.RiderValues, "fkIDcyclist_state", 2);
        AssertSqliteInteger(preview.RiderValues, "fkIDtype_rider", 3);
        AssertSqliteInteger(preview.RiderValues, "gene_i_birthdate", 19981210);
        AssertSqliteInteger(preview.RiderValues, "gene_i_size", 172);
        AssertSqliteInteger(preview.RiderValues, "gene_i_weight", 61);
        AssertSqliteInteger(preview.RiderValues, "fkIDyear_progression", 8);
        AssertSqliteInteger(preview.RiderValues, "fkIDstate_roster", 3);
        AssertSqliteInteger(preview.RiderValues, "gene_i_date_last_breakaway", 101);
        AssertSqliteInteger(preview.RiderValues, "fkIDworkplan", 1);
        AssertSqliteInteger(preview.RiderValues, "gene_i_ptmap", 25343);
        AssertSqliteInteger(preview.RiderValues, "fkIDcontract_preference_preset", 4);
        AssertSqliteInteger(preview.RiderValues, "iContract_fidelity", 3);
        Assert.AreEqual("Ada", preview.RiderValues["gene_sz_firstname"].TextValue);
        Assert.AreEqual("Lovelace", preview.RiderValues["gene_sz_lastname"].TextValue);
        Assert.AreEqual("Lovelace A.", preview.RiderValues["gene_sz_firstlastname"].TextValue);
        Assert.AreEqual("(11,43,25)", preview.RiderValues["gene_ilist_fkIDfavorite_races"].TextValue);
        Assert.AreEqual(3d, preview.RiderValues["value_f_potentiel"].RealValue);
        CollectionAssert.AreEqual(new long[] { 11, 43, 25 }, preview.FavoriteRaces.Select(static race => race.Id).ToArray());
        Assert.IsTrue(preview.UsesAutomaticCurrentAbility);
        Assert.AreEqual(990d / 14d, preview.RiderValues[RiderCurrentAbility.ColumnName].RealValue, 0.000001);
        AssertSqliteInteger(preview.RiderValues, "charac_i_mountain", 80);
        AssertSqliteInteger(preview.RiderValues, "limit_i_mountain", 75);
        Assert.AreEqual(SqliteValueKind.Null, preview.RiderValues["limit_i_plain"].Kind);
        Assert.AreEqual(SqliteValueKind.Null, preview.RiderValues["unknown_note"].Kind);
        Assert.AreEqual(SqliteValueKind.Null, preview.RiderValues["payload"].Kind);
        Assert.IsFalse(preview.RiderValues.ContainsKey("database_default"));
        CollectionAssert.AreEqual(PlainOnly, preview.MissingLimitKeys.ToArray());
        Assert.IsTrue(preview.Warnings.Any(static warning => warning.Contains("Mountain", StringComparison.Ordinal)));
        Assert.IsTrue(preview.Warnings.Any(static warning =>
            warning.Contains("NULL in the working copy", StringComparison.Ordinal) &&
            warning.Contains("may reopen as zero", StringComparison.Ordinal)));
        Assert.IsFalse(preview.Warnings.Any(static warning => warning.Contains("No favorite races", StringComparison.Ordinal)));

        RiderCreationPreview emptyFavorites = await service.PreviewAsync(
            database.Path,
            CreateInput(
                acknowledgeMissingLimits: false,
                gameDisplayName: "Custom A.",
                potential: 4.5,
                favoriteRaceIds: []),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("()", emptyFavorites.RiderValues["gene_ilist_fkIDfavorite_races"].TextValue);
        Assert.AreEqual("Custom A.", emptyFavorites.RiderValues["gene_sz_firstlastname"].TextValue);
        Assert.AreEqual(4.5d, emptyFavorites.RiderValues["value_f_potentiel"].RealValue);
        Assert.IsTrue(emptyFavorites.Warnings.Any(static warning =>
            warning.Contains("No favorite races", StringComparison.Ordinal)));

        AssertSqliteInteger(preview.ContractValues, "IDcontract_cyclist", 21);
        AssertSqliteInteger(preview.ContractValues, "fkIDcyclist", 8);
        AssertSqliteInteger(preview.ContractValues, "fkIDteam", 11);
        AssertSqliteInteger(preview.ContractValues, "fkIDprevteam", 11);
        AssertSqliteInteger(preview.ContractValues, "finan_i_period_wage", 12000);
        AssertSqliteInteger(preview.ContractValues, "iYearBegin", 0);
        AssertSqliteInteger(preview.ContractValues, "iYearEnd", 2030);
        AssertSqliteInteger(preview.ContractValues, "gene_b_active_contract", 1);
        AssertSqliteInteger(preview.ContractValues, "iRole", 5);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            database.Path,
            preview,
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PreviewUsesTheLookupIdWhenNoFriendlyLabelIsAvailable()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        await database.ExecuteAsync("""
            UPDATE DYN_team SET gene_sz_name = '  ' WHERE IDteam = 11;
            ALTER TABLE STA_type_rider DROP COLUMN CONSTANT;
            """).ConfigureAwait(false);
        var service = new RiderCreationService();

        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path,
            CreateInput(acknowledgeMissingLimits: false),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new RiderLookupOption(11, "Team"), preview.Team);
        Assert.AreEqual(new RiderLookupOption(3, "Rider type"), preview.RiderType);
    }

    [TestMethod]
    public async Task ApplyCreatesBothRowsAndHistoryUndoesThenRedoesAtomically()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path,
            CreateInput(acknowledgeMissingLimits: false),
            CancellationToken.None).ConfigureAwait(false);
        RiderCreationPreview acknowledged = preview.AcknowledgeMissingLimits();
        Assert.IsFalse(preview.Input.MissingLimitsAcknowledged);
        Assert.IsTrue(acknowledged.Input.MissingLimitsAcknowledged);
        Assert.AreEqual(preview.SnapshotToken, acknowledged.SnapshotToken);
        Assert.AreSame(preview.RiderValues, acknowledged.RiderValues);
        Assert.AreSame(preview.ContractValues, acknowledged.ContractValues);

        MaintenanceApplyResult applied = await service.ApplyAsync(
            database.Path,
            acknowledged,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, applied.AffectedRows);
        Assert.AreEqual("Lovelace A.", await database.ScalarAsync<string>(
            "SELECT gene_sz_firstlastname FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
        Assert.AreEqual("(11,43,25)", await database.ScalarAsync<string>(
            "SELECT gene_ilist_fkIDfavorite_races FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
        Assert.AreEqual(9L, await database.ScalarAsync<long>(
            "SELECT database_default FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
        Assert.AreEqual(11L, await database.ScalarAsync<long>(
            "SELECT fkIDprevteam FROM DYN_contract_cyclist WHERE IDcontract_cyclist=21").ConfigureAwait(false));

        MaintenanceEditOperation history = Assert.IsInstanceOfType<MaintenanceEditOperation>(applied.HistoryOperation);
        Assert.AreEqual(MaintenanceToolKind.RiderCreation, history.Tool);
        Assert.HasCount(2, history.Changes);
        Assert.AreEqual("DYN_contract_cyclist", history.Changes[0].TableName);
        Assert.AreEqual("DYN_cyclist", history.Changes[1].TableName);
        Assert.HasCount(2, applied.UndoGuards!);

        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var replayer = new SqliteEditOperationReplayer();
        EditReplayResult undone = await replayer.ReplayAsync(
            database.Path,
            catalog,
            new EditHistoryReplay(history, EditReplayDirection.Undo, applied.UndoGuards),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_contract_cyclist WHERE IDcontract_cyclist=21").ConfigureAwait(false));

        EditReplayResult redone = await replayer.ReplayAsync(
            database.Path,
            catalog,
            new EditHistoryReplay(history, EditReplayDirection.Redo, undone.OppositeGuards),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, redone.AffectedRows);
        Assert.AreEqual(21L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
        Assert.AreEqual(8L, await database.ScalarAsync<long>(
            "SELECT fkIDcyclist FROM DYN_contract_cyclist WHERE IDcontract_cyclist=21").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CreationOmitsAbsentOptionalPreferenceAndDoesNotRequireItsLookupTable()
    {
        await using var database = await CreateDatabaseAsync(preferenceColumnDefinition: null).ConfigureAwait(false);
        await database.ExecuteAsync("DROP TABLE INF_contract_preference_preset").ConfigureAwait(false);
        var service = new RiderCreationService();

        MaintenanceCapability capability = await service.CheckCapabilityAsync(
            database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(capability.IsEnabled, string.Join(" ", capability.Reasons));
        RiderCreationDraft draft = await service.PrepareAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(draft.Fields.Any(static field => field.Column.Name == "fkIDcontract_preference_preset"));
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(preview.RiderValues.ContainsKey("fkIDcontract_preference_preset"));

        MaintenanceApplyResult result = await service.ApplyAsync(
            database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, result.AffectedRows);
        Assert.AreEqual(2L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(4))]),
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("4", 4L, false)]
    [DataRow("((+5))", 5L, false)]
    [DataRow("NULL", 0L, true)]
    public async Task CreationResolvesAndPersistsEffectivePreferenceLiteralDefault(
        string expression,
        long expectedId,
        bool expectNull)
    {
        await using var database = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: $"INTEGER DEFAULT {expression} REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)")
            .ConfigureAwait(false);
        await database.ExecuteAsync("INSERT INTO INF_contract_preference_preset VALUES(5,20,40,40)").ConfigureAwait(false);
        if (expectedId != 4)
        {
            await database.ExecuteAsync("DELETE FROM INF_contract_preference_preset WHERE IDcontract_preference_preset=4")
                .ConfigureAwait(false);
        }

        var service = new RiderCreationService();
        RiderCreationDraft draft = await service.PrepareAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        RiderCreationField field = draft.Fields.Single(static field => field.Column.Name == "fkIDcontract_preference_preset");
        Assert.IsTrue(field.UsesDatabaseDefault);
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        SqliteValue expected = expectNull ? SqliteValue.Null : SqliteValue.Integer(expectedId);
        Assert.AreEqual(expected, field.Value);
        Assert.AreEqual(expected, preview.RiderValues["fkIDcontract_preference_preset"]);

        if (!expectNull)
        {
            await database.ExecuteAsync(
                $"UPDATE INF_contract_preference_preset SET iWeight_Salary=99 WHERE IDcontract_preference_preset={expectedId}")
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
                database.Path, preview, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
            Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
            preview = await service.PreviewAsync(database.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        }

        MaintenanceApplyResult applied = await service.ApplyAsync(
            database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        MaintenanceEditOperation history = Assert.IsInstanceOfType<MaintenanceEditOperation>(applied.HistoryOperation);
        Assert.AreEqual(expected, history.Changes.Single(static change => change.TableName == "DYN_cyclist")
            .AfterValues!["fkIDcontract_preference_preset"]);
        Assert.AreEqual(expectNull ? 1L : 0L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract_preference_preset IS NULL FROM DYN_cyclist").ConfigureAwait(false));
        if (!expectNull)
        {
            Assert.AreEqual(expectedId, await database.ScalarAsync<long>(
                "SELECT fkIDcontract_preference_preset FROM DYN_cyclist").ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task DefaultedPreferenceUsesOverrideAndGuardsItsSelectedLookupRevision()
    {
        await using var database = await CreateDatabaseAsync(
            preferenceColumnDefinition: "INTEGER DEFAULT 4 REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)")
            .ConfigureAwait(false);
        await database.ExecuteAsync("INSERT INTO INF_contract_preference_preset VALUES(5,20,40,40)").ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationInput input = CreateInput(
            true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(5))]);
        RiderCreationPreview preview = await service.PreviewAsync(database.Path, input, CancellationToken.None).ConfigureAwait(false);
        AssertSqliteInteger(preview.RiderValues, "fkIDcontract_preference_preset", 5);
        await database.ExecuteAsync("UPDATE INF_contract_preference_preset SET iWeight_Salary=61 WHERE IDcontract_preference_preset=4")
            .ConfigureAwait(false);
        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(5L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract_preference_preset FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));

        RiderCreationPreview stale = await service.PreviewAsync(database.Path, input, CancellationToken.None).ConfigureAwait(false);
        await database.ExecuteAsync("UPDATE INF_contract_preference_preset SET iWeight_Salary=21 WHERE IDcontract_preference_preset=5")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
            database.Path, stale, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        Assert.AreEqual(2L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreferenceOverrideCanReplaceMissingUnusedDefaultBeforeOrAfterPreview(bool removeAfterPreview)
    {
        await using var database = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: "INTEGER DEFAULT 4 REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)")
            .ConfigureAwait(false);
        await database.ExecuteAsync("INSERT INTO INF_contract_preference_preset VALUES(5,20,40,40)").ConfigureAwait(false);
        if (!removeAfterPreview)
        {
            await database.ExecuteAsync("DELETE FROM INF_contract_preference_preset WHERE IDcontract_preference_preset=4")
                .ConfigureAwait(false);
        }

        var service = new RiderCreationService();
        MaintenanceCapability capability = await service.CheckCapabilityAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(capability.IsEnabled, string.Join(" ", capability.Reasons));
        RiderCreationDraft draft = await service.PrepareAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Integer(4), draft.Fields.Single(static field =>
            field.Column.Name == "fkIDcontract_preference_preset").Value);
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path,
            CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(5))]),
            CancellationToken.None).ConfigureAwait(false);
        if (removeAfterPreview)
        {
            await database.ExecuteAsync("DELETE FROM INF_contract_preference_preset WHERE IDcontract_preference_preset=4")
                .ConfigureAwait(false);
        }

        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(5L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract_preference_preset FROM DYN_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingEffectivePreferenceOverrideIsRejectedBeforeEitherRowIsCreated(bool removeAfterPreview)
    {
        await using var database = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: "INTEGER DEFAULT 4 REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)")
            .ConfigureAwait(false);
        await database.ExecuteAsync("INSERT INTO INF_contract_preference_preset VALUES(5,20,40,40)").ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationInput input = CreateInput(
            true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(5))]);
        RiderCreationPreview? preview = removeAfterPreview
            ? await service.PreviewAsync(database.Path, input, CancellationToken.None).ConfigureAwait(false)
            : null;
        await database.ExecuteAsync("DELETE FROM INF_contract_preference_preset WHERE IDcontract_preference_preset=5")
            .ConfigureAwait(false);

        if (preview is null)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(
                database.Path, input, CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
                database.Path, preview, CancellationToken.None)).ConfigureAwait(false);
        }

        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NullablePreferenceAcceptsExplicitNullWithOrWithoutDatabaseDefault(bool useDatabaseDefault)
    {
        string definition = "INTEGER "
            + (useDatabaseDefault ? "DEFAULT 4 " : string.Empty)
            + "REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)";
        await using var database = await CreateDatabaseAsync(preferenceColumnDefinition: definition).ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path,
            CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Null)]),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SqliteValueKind.Null, preview.RiderValues["fkIDcontract_preference_preset"].Kind);
        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract_preference_preset IS NULL FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PreferenceOverridesRejectNullForRequiredFieldsInvalidTypesAndMissingReferencesBeforeMutation()
    {
        await using var database = await CreateDatabaseAsync(
            preferenceColumnDefinition: "INTEGER NOT NULL DEFAULT 4 REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)")
            .ConfigureAwait(false);
        var service = new RiderCreationService();
        foreach (SqliteValue invalid in new[] { SqliteValue.Null, SqliteValue.Text("4"), SqliteValue.Real(4), SqliteValue.Integer(999) })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(
                database.Path,
                CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", invalid)]),
                CancellationToken.None)).ConfigureAwait(false);
        }

        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("(2 + 2)")]
    [DataRow("(abs(-4))")]
    [DataRow("'4'")]
    [DataRow("CURRENT_TIMESTAMP")]
    [DataRow("9223372036854775808")]
    public async Task CapabilityReportsUnresolvablePreferenceDefaultBeforePrepareOrPreview(string expression)
    {
        await using var database = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: $"INTEGER DEFAULT {expression}").ConfigureAwait(false);
        var service = new RiderCreationService();
        MaintenanceCapability capability = await service.CheckCapabilityAsync(
            database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(capability.IsEnabled);
        Assert.IsTrue(capability.Reasons.Any(static reason =>
            reason.Contains("cannot safely resolve the database default expression", StringComparison.Ordinal)
            && reason.Contains("DYN_cyclist.fkIDcontract_preference_preset", StringComparison.Ordinal)));
        await Assert.ThrowsExactlyAsync<MaintenanceCapabilityException>(() => service.PrepareAsync(
            database.Path, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<MaintenanceCapabilityException>(() => service.PreviewAsync(
            database.Path, CreateInput(true), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PresentPreferenceRequiresItsLookupSchemaButChecksEffectiveDefaultsAtPreview()
    {
        var service = new RiderCreationService();
        await using var missingLookup = await CreateDatabaseAsync(preferenceColumnDefinition: "INTEGER").ConfigureAwait(false);
        await missingLookup.ExecuteAsync("DROP TABLE INF_contract_preference_preset").ConfigureAwait(false);
        MaintenanceCapability missingCapability = await service.CheckCapabilityAsync(
            missingLookup.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(missingCapability.IsEnabled);
        CollectionAssert.Contains(missingCapability.MissingTables.ToArray(), "INF_contract_preference_preset");

        await using var missingDefault = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: "INTEGER DEFAULT 999").ConfigureAwait(false);
        MaintenanceCapability defaultCapability = await service.CheckCapabilityAsync(
            missingDefault.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(defaultCapability.IsEnabled, string.Join(" ", defaultCapability.Reasons));
        await service.PrepareAsync(missingDefault.Path, CancellationToken.None).ConfigureAwait(false);
        InvalidDataException defaultFailure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(
            missingDefault.Path, CreateInput(true), CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(defaultFailure.Message, "effective default", StringComparison.Ordinal);
        StringAssert.Contains(defaultFailure.Message, "999", StringComparison.Ordinal);
        StringAssert.Contains(defaultFailure.Message, "Choose an existing", StringComparison.Ordinal);
        await service.PreviewAsync(
            missingDefault.Path,
            CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(4))]),
            CancellationToken.None).ConfigureAwait(false);

        await using var requiredNull = await CreateDatabaseAsync(
            includeSeedRows: false,
            preferenceColumnDefinition: "INTEGER NOT NULL DEFAULT NULL").ConfigureAwait(false);
        MaintenanceCapability nullCapability = await service.CheckCapabilityAsync(
            requiredNull.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(nullCapability.IsEnabled, string.Join(" ", nullCapability.Reasons));
        await service.PrepareAsync(requiredNull.Path, CancellationToken.None).ConfigureAwait(false);
        InvalidDataException nullFailure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(
            requiredNull.Path, CreateInput(true), CancellationToken.None)).ConfigureAwait(false);
        StringAssert.Contains(nullFailure.Message, "NULL is not allowed", StringComparison.Ordinal);
        await service.PreviewAsync(
            requiredNull.Path,
            CreateInput(true, riderAdvanced: [KeyValuePair.Create("fkIDcontract_preference_preset", SqliteValue.Integer(4))]),
            CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("INTEGER REFERENCES DYN_team(IDteam)", "", "declared foreign key")]
    [DataRow("INTEGER REFERENCES INF_contract_preference_preset(iWeight_Salary)", "", "declared foreign key")]
    [DataRow("INTEGER", "FOREIGN KEY(fkIDcontract_preference_preset, iContract_fidelity) REFERENCES INF_contract_preference_preset(IDcontract_preference_preset, iWeight_Role)", "composite foreign key")]
    public async Task CapabilityRejectsPreferenceRelationshipsOutsideSupportedSingleColumnTarget(
        string columnDefinition,
        string constraint,
        string expectedReason)
    {
        await using var database = await CreateDatabaseAsync(
            includeSeedRows: false,
            riderExtraColumn: constraint,
            preferenceColumnDefinition: columnDefinition).ConfigureAwait(false);
        await database.ExecuteAsync("""
            CREATE UNIQUE INDEX preference_salary ON INF_contract_preference_preset(iWeight_Salary);
            CREATE UNIQUE INDEX preference_pair ON INF_contract_preference_preset(IDcontract_preference_preset, iWeight_Role)
            """).ConfigureAwait(false);
        var service = new RiderCreationService();
        MaintenanceCapability capability = await service.CheckCapabilityAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(capability.IsEnabled);
        Assert.IsTrue(capability.Reasons.Any(reason => reason.Contains(expectedReason, StringComparison.Ordinal)
            && reason.Contains("DYN_cyclist.fkIDcontract_preference_preset", StringComparison.Ordinal)));
        await Assert.ThrowsExactlyAsync<MaintenanceCapabilityException>(() => service.PrepareAsync(
            database.Path, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<MaintenanceCapabilityException>(() => service.PreviewAsync(
            database.Path, CreateInput(true), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PreferenceCanReferenceTheSupportedImplicitSingleColumnPrimaryKey()
    {
        await using var database = await CreateDatabaseAsync(
            preferenceColumnDefinition: "INTEGER REFERENCES INF_contract_preference_preset").ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationPreview preview = await service.PreviewAsync(database.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        AssertSqliteInteger(preview.RiderValues, "fkIDcontract_preference_preset", 4);
        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(4L, await database.ScalarAsync<long>(
            "SELECT fkIDcontract_preference_preset FROM DYN_cyclist WHERE IDcyclist=8").ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("FOREIGN KEY(fkIDworkplan, iContract_fidelity) REFERENCES STA_workplan(IDworkplan, variant)")]
    [DataRow("FOREIGN KEY(fkIDworkplan) REFERENCES STA_workplan(IDworkplan), FOREIGN KEY(fkIDworkplan) REFERENCES alternate_plan(IDplan)")]
    public async Task AdvancedRiderFieldWithCompositeOrAmbiguousRelationshipRemainsRaw(string constraint)
    {
        await using var database = await CreateDatabaseAsync(
            riderExtraColumn: constraint, includeSeedRows: false).ConfigureAwait(false);
        await database.ExecuteAsync("""
            CREATE TABLE STA_workplan(IDworkplan INTEGER UNIQUE, variant INTEGER, name TEXT, PRIMARY KEY(IDworkplan, variant));
            CREATE TABLE alternate_plan(IDplan INTEGER PRIMARY KEY, name TEXT)
            """).ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationDraft draft = await service.PrepareAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        RiderCreationField workPlan = draft.Fields.Single(static field => field.Column.Name == "fkIDworkplan");
        Assert.IsTrue(workPlan.IsEditable);
        Assert.IsNull(workPlan.LookupTarget);

        var stalePicker = new RiderLookupTarget(
            "DYN_cyclist", "fkIDworkplan", "STA_workplan", "IDworkplan", "name", "Work plan");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SearchLookupAsync(
            database.Path, stalePicker, string.Empty, 10, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreationRejectsStaleLookupsAndMaximaWithoutPartialRows()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationPreview changedLookup = await service.PreviewAsync(
            database.Path,
            CreateInput(acknowledgeMissingLimits: false),
            CancellationToken.None).ConfigureAwait(false);
        await database.ExecuteAsync("UPDATE DYN_team SET gene_sz_name='Renamed' WHERE IDteam=11")
            .ConfigureAwait(false);
        Assert.AreEqual("Destination Team", changedLookup.Team.DisplayName);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
            database.Path, changedLookup.AcknowledgeMissingLimits(), CancellationToken.None)).ConfigureAwait(false);

        RiderCreationPreview changedAdvancedLookup = await service.PreviewAsync(
            database.Path,
            CreateInput(
                true,
                riderAdvanced:
                [
                    KeyValuePair.Create(RiderCurrentAbility.ColumnName.ToUpperInvariant(), SqliteValue.Real(77.5)),
                    KeyValuePair.Create("fkIDcyclist_state", SqliteValue.Integer(1))
                ],
                gameDisplayName: "Custom A."),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Custom A.", changedAdvancedLookup.RiderValues["gene_sz_firstlastname"].TextValue);
        Assert.IsFalse(changedAdvancedLookup.UsesAutomaticCurrentAbility);
        Assert.AreEqual(77.5, changedAdvancedLookup.RiderValues[RiderCurrentAbility.ColumnName].RealValue);
        AssertSqliteInteger(changedAdvancedLookup.RiderValues, "fkIDcyclist_state", 1);
        await database.ExecuteAsync("UPDATE STA_cyclist_state SET CONSTANT='AVAILABLE' WHERE IDcyclist_state=1")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
            database.Path, changedAdvancedLookup, CancellationToken.None)).ConfigureAwait(false);

        RiderCreationPreview changedFavoriteRace = await service.PreviewAsync(
            database.Path,
            CreateInput(true),
            CancellationToken.None).ConfigureAwait(false);
        await database.ExecuteAsync("UPDATE STA_race SET gene_sz_race_name='Renamed classic' WHERE IDrace=11")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
            database.Path, changedFavoriteRace, CancellationToken.None)).ConfigureAwait(false);

        RiderCreationPreview changedMaximum = await service.PreviewAsync(
            database.Path,
            CreateInput(true),
            CancellationToken.None).ConfigureAwait(false);
        await database.ExecuteAsync(NewMaximumRows).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() => service.ApplyAsync(
            database.Path, changedMaximum, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_cyclist WHERE IDcyclist=9").ConfigureAwait(false));
        Assert.AreEqual(0L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_contract_cyclist WHERE IDcontract_cyclist=22").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CreationRollsBackAndRejectsUnsafeTriggersOrDeleteCascades()
    {
        var service = new RiderCreationService();
        await using var rollbackDatabase = await CreateDatabaseAsync(
            riderIdentitySuffix: " CHECK (IDcyclist < 8)").ConfigureAwait(false);
        RiderCreationPreview rollbackPreview = await service.PreviewAsync(
            rollbackDatabase.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => service.ApplyAsync(
            rollbackDatabase.Path, rollbackPreview, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L, await rollbackDatabase.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_contract_cyclist WHERE IDcontract_cyclist=21").ConfigureAwait(false));

        await using var triggerDatabase = await CreateDatabaseAsync().ConfigureAwait(false);
        RiderCreationPreview triggerPreview = await service.PreviewAsync(
            triggerDatabase.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        await triggerDatabase.ExecuteAsync(
            "CREATE TRIGGER unsafe_insert AFTER INSERT ON DYN_contract_cyclist BEGIN UPDATE DYN_team SET gene_sz_name='side effect' WHERE IDteam=11; END")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() => service.ApplyAsync(
            triggerDatabase.Path, triggerPreview, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("Destination Team", await triggerDatabase.ScalarAsync<string>(
            "SELECT gene_sz_name FROM DYN_team WHERE IDteam=11").ConfigureAwait(false));

        await using var cascadeDatabase = await CreateDatabaseAsync().ConfigureAwait(false);
        await cascadeDatabase.ExecuteAsync(
            "CREATE TABLE rider_child(ID INTEGER PRIMARY KEY, rider_id INTEGER REFERENCES DYN_cyclist(IDcyclist) ON DELETE CASCADE)")
            .ConfigureAwait(false);
        RiderCreationPreview cascadePreview = await service.PreviewAsync(
            cascadeDatabase.Path, CreateInput(true), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<UnsafeRowMutationException>(() => service.ApplyAsync(
            cascadeDatabase.Path, cascadePreview, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreationAbortsReplacementConflictAndPreservesTheExistingPair()
    {
        await using var database = await CreateDatabaseAsync(
            riderExtraColumn: "UNIQUE(gene_sz_firstlastname) ON CONFLICT REPLACE",
            contractRiderForeignKey: false).ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path, CreateInput(true, gameDisplayName: "Rider S."), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<SqliteException>(() => service.ApplyAsync(
            database.Path, preview, CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(7L, await database.ScalarAsync<long>(
            "SELECT IDcyclist FROM DYN_cyclist WHERE gene_sz_firstlastname='Rider S.' AND fkIDcontract=20").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_contract_cyclist WHERE IDcontract_cyclist=20 AND fkIDcyclist=7").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_cyclist").ConfigureAwait(false));
        Assert.AreEqual(1L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM DYN_contract_cyclist").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AmbiguousOptionalRaceContextDoesNotDuplicateOrInvalidateTheRace()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        await database.ExecuteAsync("""
            ALTER TABLE STA_race_class RENAME TO seed_race_class;
            CREATE VIEW STA_race_class AS
                SELECT * FROM seed_race_class UNION ALL SELECT * FROM seed_race_class WHERE IDrace_class=1;
            """).ConfigureAwait(false);
        var service = new RiderCreationService();
        RiderCreationDraft draft = await service.PrepareAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        RiderLookupOption option = Assert.ContainsSingle(await service.SearchLookupAsync(
            database.Path, Lookup(draft, "DYN_cyclist", "gene_ilist_fkIDfavorite_races"),
            "11", 10, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("Ronde van Vlaanderen · BELGIUM · 11", option.ToString());

        RiderCreationPreview preview = await service.PreviewAsync(
            database.Path, CreateInput(true, favoriteRaceIds: [11]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(option, Assert.ContainsSingle(preview.FavoriteRaces));
    }

    [TestMethod]
    public async Task CapabilityRejectsUnsafeShapesButAllowsNullableUnknownsAndReadableViews()
    {
        var service = new RiderCreationService();
        await using var requiredUnknown = await CreateDatabaseAsync(
            riderExtraColumn: "mystery INTEGER NOT NULL",
            includeSeedRows: false).ConfigureAwait(false);
        MaintenanceCapability requiredCapability = await service.CheckCapabilityAsync(
            requiredUnknown.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(requiredCapability.IsEnabled);
        Assert.IsTrue(requiredCapability.Reasons.Any(static reason =>
            reason.Contains("unfamiliar required column", StringComparison.Ordinal)));

        await using var mutationViews = await CreateDatabaseAsync().ConfigureAwait(false);
        await mutationViews.ExecuteAsync(
            "ALTER TABLE DYN_cyclist RENAME TO cyclist_source; ALTER TABLE DYN_contract_cyclist RENAME TO contract_source; CREATE VIEW DYN_cyclist AS SELECT * FROM cyclist_source; CREATE VIEW DYN_contract_cyclist AS SELECT * FROM contract_source")
            .ConfigureAwait(false);
        MaintenanceCapability viewCapability = await service.CheckCapabilityAsync(
            mutationViews.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(viewCapability.IsEnabled);

        await using var missingRace = await CreateDatabaseAsync().ConfigureAwait(false);
        await missingRace.ExecuteAsync("DROP TABLE STA_race").ConfigureAwait(false);
        MaintenanceCapability missingRaceCapability = await service.CheckCapabilityAsync(
            missingRace.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(missingRaceCapability.IsEnabled);
        CollectionAssert.Contains(missingRaceCapability.MissingTables.ToArray(), "STA_race");

        await using var readableTeamView = await CreateDatabaseAsync().ConfigureAwait(false);
        await readableTeamView.ExecuteAsync(
            "ALTER TABLE DYN_team RENAME TO team_source; CREATE VIEW DYN_team AS SELECT * FROM team_source")
            .ConfigureAwait(false);
        MaintenanceCapability sourceViewCapability = await service.CheckCapabilityAsync(
            readableTeamView.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(sourceViewCapability.IsEnabled, string.Join(" ", sourceViewCapability.Reasons));

        await using var overflow = await CreateDatabaseAsync().ConfigureAwait(false);
        await overflow.ExecuteAsync(MaximumRows).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OverflowException>(() => service.PreviewAsync(
            overflow.Path, CreateInput(true), CancellationToken.None)).ConfigureAwait(false);

        await using var cancelled = await CreateDatabaseAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.PreviewAsync(
            cancelled.Path, CreateInput(true), cancellation.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CreationRejectsIncompleteAbilitiesFutureDatesAndLockedOrBlobOverrides()
    {
        await using var database = await CreateDatabaseAsync().ConfigureAwait(false);
        var service = new RiderCreationService();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(true, abilities: CreateAbilities().Skip(1)),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(true, birthDate: new DateOnly(2027, 1, 1)),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(true, contractEndYear: 2025),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(
                true,
                riderAdvanced: [KeyValuePair.Create("fkIDteam", SqliteValue.Integer(10))]),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(
                true,
                riderAdvanced: [KeyValuePair.Create("gene_sz_firstlastname", SqliteValue.Text("Locked A."))]),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(
                true,
                riderAdvanced: [KeyValuePair.Create("payload", SqliteValue.Blob([1, 2, 3]))]),
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync(
            database.Path,
            CreateInput(true, favoriteRaceIds: [9999]),
            CancellationToken.None)).ConfigureAwait(false);
    }

    private static RiderLookupTarget Lookup(RiderCreationDraft draft, string table, string column) =>
        draft.Fields.Single(field =>
            field.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)
            && field.Column.Name.Equals(column, StringComparison.OrdinalIgnoreCase)).LookupTarget
        ?? throw new AssertFailedException($"{table}.{column} should expose a lookup target.");

    [TestMethod]
    public async Task DistinctNonAsciiAdvancedColumnsRemainSeparateThroughPreviewAndApply()
    {
        await using var database = await CreateDatabaseAsync(riderExtraColumn: """
            "É" TEXT, "é" TEXT
            """).ConfigureAwait(false);
        var service = new RiderCreationService();
        var input = CreateInput(acknowledgeMissingLimits: true,
            riderAdvanced: [KeyValuePair.Create("É", SqliteValue.Text("upper")), KeyValuePair.Create("é", SqliteValue.Text("lower"))]);
        RiderCreationPreview preview = await service.PreviewAsync(database.Path, input, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(SqliteValue.Text("upper"), preview.RiderValues["É"]);
        Assert.AreEqual(SqliteValue.Text("lower"), preview.RiderValues["é"]);

        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("upper|lower", await database.ScalarAsync<string>("""
            SELECT "É" || '|' || "é" FROM DYN_cyclist WHERE IDcyclist = (SELECT MAX(IDcyclist) FROM DYN_cyclist)
            """).ConfigureAwait(false));
    }

    private static RiderCreationInput CreateInput(
        bool acknowledgeMissingLimits,
        IEnumerable<RiderAbilityInput>? abilities = null,
        DateOnly? birthDate = null,
        int contractEndYear = 2030,
        IEnumerable<KeyValuePair<string, SqliteValue>>? riderAdvanced = null,
        string? gameDisplayName = null,
        double potential = 3.0,
        IEnumerable<long>? favoriteRaceIds = null,
        long wage = 12000) =>
        new(
            "Ada",
            "Lovelace",
            teamId: 11,
            regionId: 2801,
            riderTypeId: 3,
            birthDate ?? new DateOnly(1998, 12, 10),
            height: 172,
            weight: 61,
            photo: "ada_lovelace",
            soundName: "ada",
            abilities ?? CreateAbilities(),
            RiderContractRole.LuxuryTeammate,
            wage,
            contractEndYear,
            acknowledgeMissingLimits,
            riderAdvanced,
            gameDisplayName: gameDisplayName,
            potential: potential,
            favoriteRaceIds: favoriteRaceIds ?? [11, 43, 25]);

    private static RiderAbilityInput[] CreateAbilities() =>
        AbilityKeys.Select(key => key switch
        {
            "plain" => new RiderAbilityInput(key, 70),
            "mountain" => new RiderAbilityInput(key, 80, 75),
            _ => new RiderAbilityInput(key, 70, 75)
        }).ToArray();

    private static void AssertSqliteInteger(
        IReadOnlyDictionary<string, SqliteValue> values,
        string column,
        long expected)
    {
        Assert.AreEqual(SqliteValueKind.Integer, values[column].Kind, column);
        Assert.AreEqual(expected, values[column].IntegerValue, column);
    }

    private static Task<SqliteTestDatabase> CreateDatabaseAsync(
        string riderIdentitySuffix = "",
        string riderExtraColumn = "",
        bool includeSeedRows = true,
        string? preferenceColumnDefinition = "INTEGER REFERENCES INF_contract_preference_preset(IDcontract_preference_preset)",
        bool contractRiderForeignKey = true)
    {
        string extra = string.IsNullOrWhiteSpace(riderExtraColumn)
            ? string.Empty
            : $", {riderExtraColumn}";
        string riderSchema = RiderSchema
            .Replace("__IDENTITY_SUFFIX__", riderIdentitySuffix, StringComparison.Ordinal)
            .Replace("__PREFERENCE_COLUMN__", preferenceColumnDefinition is null
                ? string.Empty
                : $"fkIDcontract_preference_preset {preferenceColumnDefinition},", StringComparison.Ordinal)
            .Replace("__EXTRA_COLUMN__", extra, StringComparison.Ordinal)
            .Replace("__CONTRACT_RIDER_FK__", contractRiderForeignKey ? " REFERENCES DYN_cyclist(IDcyclist)" : string.Empty,
                StringComparison.Ordinal);
        return SqliteTestDatabase.CreateAsync(
            riderSchema,
            SupportingSchema,
            includeSeedRows ? SeedRows : string.Empty);
    }

    private const string RiderSchema = """
        CREATE TABLE DYN_cyclist(
            IDcyclist INTEGER PRIMARY KEY__IDENTITY_SUFFIX__,
            gene_sz_firstname TEXT NOT NULL,
            gene_sz_lastname TEXT NOT NULL,
            gene_sz_firstlastname TEXT NOT NULL,
            fkIDcontract INTEGER NOT NULL UNIQUE REFERENCES DYN_contract_cyclist(IDcontract_cyclist),
            fkIDteam INTEGER NOT NULL REFERENCES DYN_team(IDteam),
            fkIDregion INTEGER NOT NULL REFERENCES STA_region(IDregion),
            fkIDcyclist_state INTEGER NOT NULL REFERENCES STA_cyclist_state(IDcyclist_state),
            fkIDtype_rider INTEGER NOT NULL REFERENCES STA_type_rider(IDtype_rider),
            gene_sz_photo TEXT NOT NULL,
            gene_sz_soundname TEXT NOT NULL,
            gene_i_birthdate INTEGER NOT NULL,
            gene_i_size INTEGER NOT NULL,
            gene_i_weight INTEGER NOT NULL,
            value_f_current_ability REAL NOT NULL,
            fkIDyear_progression INTEGER NOT NULL,
            charac_i_plain INTEGER NOT NULL, limit_i_plain INTEGER,
            charac_i_mountain INTEGER NOT NULL, limit_i_mountain INTEGER,
            charac_i_medium_mountain INTEGER NOT NULL, limit_i_medium_mountain INTEGER,
            charac_i_downhilling INTEGER NOT NULL, limit_i_downhilling INTEGER,
            charac_i_cobble INTEGER NOT NULL, limit_i_cobble INTEGER,
            charac_i_timetrial INTEGER NOT NULL, limit_i_timetrial INTEGER,
            charac_i_prologue INTEGER NOT NULL, limit_i_prologue INTEGER,
            charac_i_sprint INTEGER NOT NULL, limit_i_sprint INTEGER,
            charac_i_acceleration INTEGER NOT NULL, limit_i_acceleration INTEGER,
            charac_i_endurance INTEGER NOT NULL, limit_i_endurance INTEGER,
            charac_i_resistance INTEGER NOT NULL, limit_i_resistance INTEGER,
            charac_i_recuperation INTEGER NOT NULL, limit_i_recuperation INTEGER,
            charac_i_hill INTEGER NOT NULL, limit_i_hill INTEGER,
            charac_i_baroudeur INTEGER NOT NULL, limit_i_baroudeur INTEGER,
            fkIDstate_roster INTEGER,
            gene_i_date_last_breakaway INTEGER,
            fkIDworkplan INTEGER,
            gene_i_ptmap INTEGER,
            __PREFERENCE_COLUMN__
            iContract_fidelity INTEGER,
            value_f_potentiel REAL,
            gene_ilist_fkIDfavorite_races TEXT,
            gene_i_nb_total_victory INTEGER,
            unknown_note TEXT,
            payload BLOB,
            database_default INTEGER NOT NULL DEFAULT 9
            __EXTRA_COLUMN__);
        CREATE TABLE DYN_contract_cyclist(
            IDcontract_cyclist INTEGER PRIMARY KEY,
            fkIDcyclist INTEGER NOT NULL UNIQUE__CONTRACT_RIDER_FK__,
            fkIDteam INTEGER NOT NULL REFERENCES DYN_team(IDteam),
            fkIDprevteam INTEGER NOT NULL REFERENCES DYN_team(IDteam),
            finan_i_period_wage INTEGER NOT NULL,
            iYearBegin INTEGER NOT NULL,
            iYearEnd INTEGER NOT NULL,
            gene_b_active_contract INTEGER NOT NULL,
            iRole INTEGER NOT NULL)
        """;

    private const string SupportingSchema = """
        CREATE TABLE DYN_team(IDteam INTEGER PRIMARY KEY, gene_sz_name TEXT NOT NULL);
        INSERT INTO DYN_team VALUES(10,'Seed Team'),(11,'Destination Team');
        CREATE TABLE STA_country(IDcountry INTEGER PRIMARY KEY, CONSTANT TEXT NOT NULL);
        INSERT INTO STA_country VALUES(28,'DENMARK'),(56,'BELGIUM'),(250,'FRANCE'),(380,'ITALY');
        CREATE TABLE STA_region(
            IDregion INTEGER PRIMARY KEY,
            CONSTANT TEXT NOT NULL,
            fkIDcountry INTEGER REFERENCES STA_country(IDcountry));
        INSERT INTO STA_region VALUES(2801,'CAPITAL_REGION',28);
        CREATE TABLE STA_type_rider(IDtype_rider INTEGER PRIMARY KEY, CONSTANT TEXT NOT NULL);
        INSERT INTO STA_type_rider VALUES(3,'CLIMBER');
        CREATE TABLE STA_cyclist_state(IDcyclist_state INTEGER PRIMARY KEY, CONSTANT TEXT NOT NULL);
        INSERT INTO STA_cyclist_state VALUES(1,'ACTIVE'),(2,'FREE');
        CREATE TABLE GAM_config(gene_i_date INTEGER NOT NULL);
        INSERT INTO GAM_config VALUES(20260715);
        CREATE TABLE INF_contract_preference_preset(
            IDcontract_preference_preset INTEGER PRIMARY KEY,
            iWeight_Salary INTEGER,
            iWeight_Nationality INTEGER,
            iWeight_Role INTEGER);
        INSERT INTO INF_contract_preference_preset VALUES(4,60,20,20);
        CREATE TABLE STA_race_class(IDrace_class INTEGER PRIMARY KEY, CONSTANT TEXT NOT NULL);
        INSERT INTO STA_race_class VALUES(1,'CWT_MAJEURES'),(2,'CWT_GRAND_TOUR');
        CREATE TABLE STA_race(
            IDrace INTEGER PRIMARY KEY,
            gene_sz_race_name TEXT NOT NULL,
            gene_sz_abbreviation TEXT,
            CONSTANT TEXT,
            fkIDcountry INTEGER,
            fkIDrace_class INTEGER);
        INSERT INTO STA_race VALUES
            (11,'Ronde van Vlaanderen','RVV','RONDE_VAN_VLAANDEREN',56,1),
            (43,'Strade Bianche','SB','STRADE_BIANCHE',380,1),
            (25,'Tour de France','TDF','TOUR_DE_FRANCE',250,2)
        """;

    private const string SeedRows = """
        BEGIN;
        PRAGMA defer_foreign_keys = ON;
        INSERT INTO DYN_contract_cyclist VALUES(20,7,10,10,500,0,2027,1,6);
        INSERT INTO DYN_cyclist(
            IDcyclist, gene_sz_firstname, gene_sz_lastname, gene_sz_firstlastname,
            fkIDcontract, fkIDteam, fkIDregion, fkIDcyclist_state, fkIDtype_rider,
            gene_sz_photo, gene_sz_soundname, gene_i_birthdate, gene_i_size, gene_i_weight,
            value_f_current_ability, fkIDyear_progression,
            charac_i_plain, charac_i_mountain, charac_i_medium_mountain, charac_i_downhilling,
            charac_i_cobble, charac_i_timetrial, charac_i_prologue, charac_i_sprint,
            charac_i_acceleration, charac_i_endurance, charac_i_resistance,
            charac_i_recuperation, charac_i_hill, charac_i_baroudeur)
        VALUES(
            7,'Seed','Rider','Rider S.',20,10,2801,1,3,'','',19900101,180,70,70,7,
            70,70,70,70,70,70,70,70,70,70,70,70,70,70);
        COMMIT
        """;

    private const string NewMaximumRows = """
        BEGIN;
        PRAGMA defer_foreign_keys = ON;
        INSERT INTO DYN_contract_cyclist VALUES(21,8,10,10,500,0,2027,1,6);
        INSERT INTO DYN_cyclist(
            IDcyclist, gene_sz_firstname, gene_sz_lastname, gene_sz_firstlastname,
            fkIDcontract, fkIDteam, fkIDregion, fkIDcyclist_state, fkIDtype_rider,
            gene_sz_photo, gene_sz_soundname, gene_i_birthdate, gene_i_size, gene_i_weight,
            value_f_current_ability, fkIDyear_progression,
            charac_i_plain, charac_i_mountain, charac_i_medium_mountain, charac_i_downhilling,
            charac_i_cobble, charac_i_timetrial, charac_i_prologue, charac_i_sprint,
            charac_i_acceleration, charac_i_endurance, charac_i_resistance,
            charac_i_recuperation, charac_i_hill, charac_i_baroudeur)
        VALUES(
            8,'Other','Rider','Rider O.',21,10,2801,1,3,'','',19900101,180,70,70,8,
            70,70,70,70,70,70,70,70,70,70,70,70,70,70);
        COMMIT
        """;

    private const string MaximumRows = """
        PRAGMA foreign_keys = OFF;
        UPDATE DYN_cyclist
        SET IDcyclist=9223372036854775807,
            fkIDcontract=9223372036854775807,
            fkIDyear_progression=9223372036854775807
        WHERE IDcyclist=7;
        UPDATE DYN_contract_cyclist
        SET IDcontract_cyclist=9223372036854775807,
            fkIDcyclist=9223372036854775807
        WHERE IDcontract_cyclist=20
        """;
}
