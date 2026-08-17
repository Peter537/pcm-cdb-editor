using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Maintenance;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class MaintenanceServiceTests
{
    [TestMethod]
    public async Task CapabilitiesReportMissingTablesAndColumnsForEveryPreset()
    {
        await using var riderDatabase = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE DYN_cyclist_fitness(IDcyclist INTEGER PRIMARY KEY)")
            .ConfigureAwait(false);
        MaintenanceCapability rider = await new RiderRecoveryService()
            .CheckCapabilityAsync(riderDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsFalse(rider.IsEnabled);
        Assert.HasCount(0, rider.MissingTables);
        CollectionAssert.Contains(rider.MissingColumns.ToArray(), "DYN_cyclist_fitness.value_f_FIT");

        await using var januaryDatabase = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE GAM_config(other INTEGER)",
            "CREATE TABLE DYN_result_season_stage(IDresult_season_stage INTEGER)")
            .ConfigureAwait(false);
        MaintenanceCapability january = await new JanuaryFirstRepairService()
            .CheckCapabilityAsync(januaryDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsFalse(january.IsEnabled);
        Assert.HasCount(0, january.MissingTables);
        Assert.AreEqual("GAM_config.gene_i_date", Assert.ContainsSingle(january.MissingColumns));

        await using var quotaDatabase = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE GAM_config(other INTEGER)")
            .ConfigureAwait(false);
        MaintenanceCapability quota = await new CountryQuotaMaintenanceService()
            .CheckCapabilityAsync(quotaDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsFalse(quota.IsEnabled);
        CollectionAssert.Contains(quota.MissingTables.ToArray(), "STA_country");
        CollectionAssert.Contains(quota.MissingColumns.ToArray(), "GAM_config.gene_i_date");
    }

    [TestMethod]
    public async Task RiderRecoveryIsSelectionScopedStaleSafeAndCreatesTypedUndo()
    {
        await using var database = await CreateRiderDatabaseAsync().ConfigureAwait(false);
        var service = new RiderRecoveryService();
        MaintenanceCapability capability = await service.CheckCapabilityAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsTrue(capability.IsEnabled);

        RiderRecoveryPreview preview = await service.PreviewAsync(database.Path, [1, 1], CancellationToken.None)
            .ConfigureAwait(false);
        Assert.HasCount(1, preview.Changes);
        MaintenanceApplyResult result = await service.ApplyAsync(database.Path, preview, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, result.AffectedRows);
        Assert.AreEqual(99D, await database.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=1").ConfigureAwait(false));
        Assert.AreEqual(11D, await database.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=2").ConfigureAwait(false));

        MaintenanceEditOperation operation = Assert.IsInstanceOfType<MaintenanceEditOperation>(result.HistoryOperation);
        Assert.AreEqual(MaintenanceToolKind.RiderRecovery, operation.Tool);
        MaintenanceRowChange historyChange = Assert.ContainsSingle(operation.Changes);
        Assert.AreEqual(6, historyChange.BeforeValues!.Count);
        Assert.AreEqual(SqliteValueKind.Real, historyChange.BeforeValues["value_f_FIT"].Kind);
        Assert.AreEqual(SqliteValueKind.Integer, historyChange.BeforeValues["value_i_injury_num_days"].Kind);
        Assert.AreEqual(99D, historyChange.AfterValues!["value_f_FIT"].RealValue);
        RowReplayGuard undoGuard = Assert.ContainsSingle(result.UndoGuards!);
        Assert.AreEqual(RowReplayExpectation.PresentWithRevision, undoGuard.Expectation);
        Assert.IsNotNull(undoGuard.ExpectedRevision);

        RiderRecoveryPreview stale = await service.PreviewAsync(database.Path, [2], CancellationToken.None)
            .ConfigureAwait(false);
        await database.ExecuteAsync("UPDATE DYN_cyclist_fitness SET value_f_FIT=42 WHERE IDcyclist=2")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() =>
            service.ApplyAsync(database.Path, stale, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(42D, await database.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=2").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RiderRecoveryEmptyAndAlreadyRecoveredSelectionsAreCleanNoOps()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            RiderSchema,
            "INSERT INTO DYN_cyclist_fitness VALUES(1,99,0,0,0,100,99)")
            .ConfigureAwait(false);
        var service = new RiderRecoveryService();

        RiderRecoveryPreview emptyPreview = await service.PreviewAsync(database.Path, [], CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult empty = await service.ApplyAsync(database.Path, emptyPreview, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0, empty.AffectedRows);
        Assert.IsNull(empty.HistoryOperation);

        RiderRecoveryPreview recoveredPreview = await service.PreviewAsync(database.Path, [1], CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult recovered = await service.ApplyAsync(
            database.Path,
            recoveredPreview,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, recovered.AffectedRows);
        Assert.IsNull(recovered.HistoryOperation);
    }

    [TestMethod]
    public async Task RiderRecoveryRollsBackEveryRowWhenAnUpdateFails()
    {
        await using var database = await CreateRiderDatabaseAsync().ConfigureAwait(false);
        var service = new RiderRecoveryService();
        RiderRecoveryPreview preview = await service.PreviewAsync(database.Path, [1, 2], CancellationToken.None)
            .ConfigureAwait(false);
        await database.ExecuteAsync(
            "CREATE TRIGGER reject_second_rider BEFORE UPDATE ON DYN_cyclist_fitness " +
            "WHEN OLD.IDcyclist=2 BEGIN SELECT RAISE(ABORT,'synthetic failure'); END")
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            service.ApplyAsync(database.Path, preview, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(10D, await database.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=1").ConfigureAwait(false));
        Assert.AreEqual(11D, await database.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=2").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task JanuaryRepairRequiresOneJanuaryFirstDateAndRejectsStalePreview()
    {
        await using var database = await CreateJanuaryDatabaseAsync().ConfigureAwait(false);
        var service = new JanuaryFirstRepairService();
        MaintenanceCapability capability = await service.CheckCapabilityAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsTrue(capability.IsEnabled);

        JanuaryFirstRepairPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(2L, preview.RowCount);
        await database.ExecuteAsync(
            "UPDATE DYN_result_season_stage SET value='changed' WHERE IDresult_season_stage=1")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() =>
            service.ApplyAsync(database.Path, preview, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_result_season_stage").ConfigureAwait(false));

        await database.ExecuteAsync("UPDATE GAM_config SET gene_i_date='20270102'").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);
        await database.ExecuteAsync("INSERT INTO GAM_config VALUES('20270101')").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task JanuaryRepairCapturesCompleteTypedRowsAndEmptyTableIsANoOp()
    {
        await using var typedDatabase = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE GAM_config(gene_i_date TEXT); INSERT INTO GAM_config VALUES('20270101')",
            @"CREATE TABLE DYN_result_season_stage(
              IDresult_season_stage INTEGER PRIMARY KEY, null_value, integer_value, real_value,
              text_value, blob_value)",
            "INSERT INTO DYN_result_season_stage VALUES(1,NULL,7,1.5,'neutral',X'0102')")
            .ConfigureAwait(false);
        var service = new JanuaryFirstRepairService();
        JanuaryFirstRepairPreview preview = await service.PreviewAsync(typedDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult applied = await service.ApplyAsync(
            typedDatabase.Path,
            preview,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, applied.AffectedRows);
        MaintenanceEditOperation operation = Assert.IsInstanceOfType<MaintenanceEditOperation>(
            applied.HistoryOperation);
        MaintenanceRowChange change = Assert.ContainsSingle(operation.Changes);
        Assert.IsNull(change.AfterValues);
        Assert.AreEqual(SqliteValueKind.Null, change.BeforeValues!["null_value"].Kind);
        Assert.AreEqual(SqliteValueKind.Integer, change.BeforeValues["integer_value"].Kind);
        Assert.AreEqual(SqliteValueKind.Real, change.BeforeValues["real_value"].Kind);
        Assert.AreEqual(SqliteValueKind.Text, change.BeforeValues["text_value"].Kind);
        Assert.AreEqual(SqliteValueKind.Blob, change.BeforeValues["blob_value"].Kind);
        Assert.AreEqual(RowReplayExpectation.Absent, Assert.ContainsSingle(applied.UndoGuards!).Expectation);

        await using var emptyDatabase = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE GAM_config(gene_i_date TEXT); INSERT INTO GAM_config VALUES('20270101')",
            "CREATE TABLE DYN_result_season_stage(IDresult_season_stage INTEGER PRIMARY KEY)")
            .ConfigureAwait(false);
        JanuaryFirstRepairPreview emptyPreview = await service.PreviewAsync(emptyDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult empty = await service.ApplyAsync(
            emptyDatabase.Path,
            emptyPreview,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, empty.AffectedRows);
        Assert.IsNull(empty.HistoryOperation);
    }

    [TestMethod]
    public async Task JanuaryRepairRollsBackAllDeletesWhenATriggerRejectsOneRow()
    {
        await using var database = await CreateJanuaryDatabaseAsync().ConfigureAwait(false);
        var service = new JanuaryFirstRepairService();
        JanuaryFirstRepairPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        await database.ExecuteAsync(
            "CREATE TRIGGER reject_second_delete BEFORE DELETE ON DYN_result_season_stage " +
            "WHEN OLD.IDresult_season_stage=2 BEGIN SELECT RAISE(ABORT,'synthetic failure'); END")
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            service.ApplyAsync(database.Path, preview, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2L, await database.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_result_season_stage").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task QuotasUseLockedWorldAndEuropeanBandBoundaries()
    {
        await using var database = await CreateQuotaBandDatabaseAsync().ConfigureAwait(false);
        var service = new CountryQuotaMaintenanceService();
        CountryQuotaPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(25, preview.WorldQualifierCount);
        Assert.AreEqual(18, preview.EuropeanQualifierCount);
        AssertQuota(preview, 10, 10, 8, 2, 10, 8, 2);
        AssertQuota(preview, 11, 11, 6, 2, 11, 6, 2);
        AssertQuota(preview, 19, 19, 6, 2, 19, 0, 0);
        AssertQuota(preview, 20, 20, 4, 2, null, 0, 0);
        AssertQuota(preview, 25, 25, 4, 2, null, 0, 0);
        AssertQuota(preview, 26, 26, 0, 0, null, 0, 0);
        CountryQuotaChange unranked = preview.Changes.Single(change => change.CountryId == 27);
        Assert.AreEqual(0, unranked.WorldRank, "Rank 252 must be explicitly treated as unranked.");
        Assert.AreEqual(0D, unranked.UciPoints);
    }

    [TestMethod]
    public async Task QuotaTieBreaksUseRawCodeThenCountryIdAndAliasesArePresentationOnly()
    {
        await using var database = await CreateQuotaTieDatabaseAsync().ConfigureAwait(false);
        var service = new CountryQuotaMaintenanceService();
        CountryQuotaPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);

        CountryQuotaChange chinaAlias = preview.Changes.Single(change => change.RawCode == "CHI");
        CountryQuotaChange competingRawCode = preview.Changes.Single(change => change.RawCode == "CHM");
        Assert.AreEqual("CHN", chinaAlias.CanonicalCode);
        Assert.AreEqual(1, chinaAlias.WorldRank);
        Assert.AreEqual(2, competingRawCode.WorldRank);
        Assert.AreEqual(3, preview.Changes.Single(change => change.CountryId == 3).WorldRank);
        Assert.AreEqual(4, preview.Changes.Single(change => change.CountryId == 4).WorldRank);
        Assert.IsTrue(preview.Changes.All(change => change.EuropeanRank == change.WorldRank));

        await service.ApplyAsync(database.Path, preview, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("CHI", await database.ScalarAsync<string>(
            "SELECT CONSTANT FROM STA_country WHERE IDcountry=10").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task QuotasTrimEuropaCaseInsensitivelyAndRoundTttSharesToEven()
    {
        await using var database = await CreateTttQuotaDatabaseAsync().ConfigureAwait(false);
        var service = new CountryQuotaMaintenanceService();
        CountryQuotaPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        CountryQuotaChange change = Assert.ContainsSingle(preview.Changes);

        Assert.AreEqual(0.96D, change.UciPoints, 0.0000001D,
            "Eight 1/8 point shares must each round from 0.125 to 0.12 using midpoint-to-even.");
        Assert.AreEqual(1, change.EuropeanRank);
        Assert.AreEqual(8L, change.NewValues.EuropeanRoad);
    }

    [TestMethod]
    public async Task QuotasRejectWrongDateNoPositivePointsAndMalformedPointScales()
    {
        await using var database = await CreateQuotaDatabaseAsync().ConfigureAwait(false);
        var service = new CountryQuotaMaintenanceService();

        await database.ExecuteAsync("UPDATE GAM_config SET gene_i_date='20271201'").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);

        await database.ExecuteAsync(
            "UPDATE GAM_config SET gene_i_date='20271115'; " +
            "UPDATE DYN_result_season SET gene_i_rank_stage_time=252")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);

        await database.ExecuteAsync(
            "UPDATE DYN_result_season SET gene_i_rank_stage_time=1; " +
            "UPDATE STA_race_bonus SET gene_ilist_bonus='(10,x,5)' WHERE fkIDclassification_source=1")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);

        await database.ExecuteAsync(
            "UPDATE STA_race_bonus SET gene_ilist_bonus='(10,5)' WHERE fkIDclassification_source=1; " +
            "INSERT INTO STA_race_bonus VALUES(1,1,1,'(10,5)')")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.PreviewAsync(database.Path, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QuotaApplyRejectsStalePreviewAndRollsBackEveryCountryOnFailure()
    {
        var service = new CountryQuotaMaintenanceService();
        await using var staleDatabase = await CreateQuotaDatabaseAsync().ConfigureAwait(false);
        CountryQuotaPreview stale = await service.PreviewAsync(staleDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        await staleDatabase.ExecuteAsync(
            "UPDATE STA_country SET gene_i_num_cyclist_WC=7 WHERE IDcountry=1")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DBConcurrencyException>(() =>
            service.ApplyAsync(staleDatabase.Path, stale, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(7L, await staleDatabase.ScalarAsync<long>(
            "SELECT gene_i_num_cyclist_WC FROM STA_country WHERE IDcountry=1").ConfigureAwait(false));

        await using var rollbackDatabase = await CreateQuotaDatabaseAsync().ConfigureAwait(false);
        CountryQuotaPreview rollback = await service.PreviewAsync(rollbackDatabase.Path, CancellationToken.None)
            .ConfigureAwait(false);
        await rollbackDatabase.ExecuteAsync(
            "CREATE TRIGGER reject_second_country BEFORE UPDATE ON STA_country " +
            "WHEN OLD.IDcountry=2 BEGIN SELECT RAISE(ABORT,'synthetic failure'); END")
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            service.ApplyAsync(rollbackDatabase.Path, rollback, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L, await rollbackDatabase.ScalarAsync<long>(
            "SELECT gene_i_num_cyclist_WC FROM STA_country WHERE IDcountry=1").ConfigureAwait(false));
        Assert.AreEqual(8L, await rollbackDatabase.ScalarAsync<long>(
            "SELECT gene_i_num_cyclist_WC FROM STA_country WHERE IDcountry=2").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task QuotaApplyCreatesOneFourColumnTypedUndoAndThenBecomesANoOp()
    {
        await using var database = await CreateQuotaDatabaseAsync().ConfigureAwait(false);
        var service = new CountryQuotaMaintenanceService();
        CountryQuotaPreview preview = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult applied = await service.ApplyAsync(database.Path, preview, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(2, applied.AffectedRows);
        MaintenanceEditOperation operation = Assert.IsInstanceOfType<MaintenanceEditOperation>(
            applied.HistoryOperation);
        Assert.AreEqual(MaintenanceToolKind.CountryChampionshipQuota, operation.Tool);
        Assert.HasCount(2, operation.Changes);
        Assert.IsTrue(operation.Changes.All(change => change.BeforeValues!.Count == 4));
        Assert.IsTrue(operation.Changes.SelectMany(change => change.BeforeValues!.Values)
            .All(value => value.Kind == SqliteValueKind.Integer));
        Assert.HasCount(2, applied.UndoGuards!);
        Assert.IsTrue(applied.UndoGuards!.All(
            guard => guard.Expectation == RowReplayExpectation.PresentWithRevision));

        CountryQuotaPreview matching = await service.PreviewAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        MaintenanceApplyResult noOp = await service.ApplyAsync(database.Path, matching, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0, noOp.AffectedRows);
        Assert.IsNull(noOp.HistoryOperation);
    }

    [TestMethod]
    public async Task PreCancelledApplyLeavesEveryMaintenanceTargetUntouched()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using var riderDatabase = await CreateRiderDatabaseAsync().ConfigureAwait(false);
        var riderService = new RiderRecoveryService();
        RiderRecoveryPreview riderPreview = await riderService.PreviewAsync(
            riderDatabase.Path,
            [1],
            CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            riderService.ApplyAsync(riderDatabase.Path, riderPreview, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(10D, await riderDatabase.ScalarAsync<double>(
            "SELECT value_f_FIT FROM DYN_cyclist_fitness WHERE IDcyclist=1").ConfigureAwait(false));

        await using var januaryDatabase = await CreateJanuaryDatabaseAsync().ConfigureAwait(false);
        var januaryService = new JanuaryFirstRepairService();
        JanuaryFirstRepairPreview januaryPreview = await januaryService.PreviewAsync(
            januaryDatabase.Path,
            CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            januaryService.ApplyAsync(januaryDatabase.Path, januaryPreview, cancellation.Token))
            .ConfigureAwait(false);
        Assert.AreEqual(2L, await januaryDatabase.ScalarAsync<long>(
            "SELECT COUNT(*) FROM DYN_result_season_stage").ConfigureAwait(false));

        await using var quotaDatabase = await CreateQuotaDatabaseAsync().ConfigureAwait(false);
        var quotaService = new CountryQuotaMaintenanceService();
        CountryQuotaPreview quotaPreview = await quotaService.PreviewAsync(
            quotaDatabase.Path,
            CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            quotaService.ApplyAsync(quotaDatabase.Path, quotaPreview, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(0L, await quotaDatabase.ScalarAsync<long>(
            "SELECT gene_i_num_cyclist_WC FROM STA_country WHERE IDcountry=1").ConfigureAwait(false));
    }

    private const string RiderSchema = @"CREATE TABLE DYN_cyclist_fitness(
      IDcyclist INTEGER PRIMARY KEY, value_f_FIT REAL, value_f_injury REAL,
      value_i_injury_num_days INTEGER, value_f_fat_phy REAL,
      value_f_freshness REAL, value_f_prepa REAL)";

    private static async Task<SqliteTestDatabase> CreateRiderDatabaseAsync() =>
        await SqliteTestDatabase.CreateAsync(
            RiderSchema,
            "INSERT INTO DYN_cyclist_fitness VALUES(1,10,1,5,50,20,30),(2,11,2,6,51,21,31)")
            .ConfigureAwait(false);

    private static async Task<SqliteTestDatabase> CreateJanuaryDatabaseAsync() =>
        await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE GAM_config(gene_i_date TEXT); INSERT INTO GAM_config VALUES('20270101')",
            "CREATE TABLE DYN_result_season_stage(IDresult_season_stage INTEGER PRIMARY KEY, value TEXT)",
            "INSERT INTO DYN_result_season_stage VALUES(1,'a'),(2,'b')")
            .ConfigureAwait(false);

    private static async Task<SqliteTestDatabase> CreateQuotaDatabaseAsync() =>
        await SqliteTestDatabase.CreateAsync(CreateQuotaStatements(
        [
            "INSERT INTO STA_continent VALUES(1,'  Europa  '),(2,'Other')",
            "INSERT INTO STA_country VALUES(1,'KOS',1,0,0,0,0),(2,'ZZZ',2,8,2,8,2)",
            "INSERT INTO STA_region VALUES(1,1),(2,2); INSERT INTO DYN_cyclist VALUES(1,1),(2,2)",
            "INSERT INTO STA_race_class VALUES(1,'CLASS'); INSERT INTO STA_race VALUES(1,1); " +
            "INSERT INTO STA_stage VALUES(1,1,1)",
            "INSERT INTO DYN_result_season_stage VALUES(1,1,0)",
            "INSERT INTO DYN_result_season VALUES(1,1,NULL,1,NULL,NULL,NULL)," +
            "(1,2,NULL,252,NULL,NULL,NULL)",
            "INSERT INTO STA_classification_source VALUES(1,'RACE_FINAL'),(2,'STAGE')",
            "INSERT INTO STA_classification_type VALUES(1,'TIME')",
            "INSERT INTO STA_race_bonus VALUES(1,1,1,'(10,5)'),(1,2,1,'(4,2)')"
        ])).ConfigureAwait(false);

    private static async Task<SqliteTestDatabase> CreateQuotaBandDatabaseAsync()
    {
        string awards = string.Join(',', Enumerable.Range(1, 26).Select(rank => 270 - (rank * 10)));
        string countries = string.Join(',', Enumerable.Range(1, 27).Select(id =>
            $"({id},'C{id:00}',{(id <= 19 ? 1 : 2)},9,9,9,9)"));
        string regions = string.Join(',', Enumerable.Range(1, 27).Select(id => $"({id},{id})"));
        string cyclists = string.Join(',', Enumerable.Range(1, 27).Select(id => $"({id},{id})"));
        string results = string.Join(',', Enumerable.Range(1, 26).Select(id =>
            $"(1,{id},NULL,{id},NULL,NULL,NULL)").Append("(1,27,NULL,252,NULL,NULL,NULL)"));
        return await SqliteTestDatabase.CreateAsync(CreateQuotaStatements(
        [
            "INSERT INTO STA_continent VALUES(1,'  eUrOpA  '),(2,'Other')",
            $"INSERT INTO STA_country VALUES{countries}",
            $"INSERT INTO STA_region VALUES{regions}",
            $"INSERT INTO DYN_cyclist VALUES{cyclists}",
            "INSERT INTO STA_race_class VALUES(1,'CLASS'); INSERT INTO STA_race VALUES(1,1); " +
            "INSERT INTO STA_stage VALUES(1,1,1)",
            "INSERT INTO DYN_result_season_stage VALUES(1,1,0)",
            $"INSERT INTO DYN_result_season VALUES{results}",
            "INSERT INTO STA_classification_source VALUES(1,'RACE_FINAL'),(2,'STAGE')",
            "INSERT INTO STA_classification_type VALUES(1,'TIME')",
            $"INSERT INTO STA_race_bonus VALUES(1,1,1,'({awards})'),(1,2,1,'({awards})')"
        ])).ConfigureAwait(false);
    }

    private static async Task<SqliteTestDatabase> CreateQuotaTieDatabaseAsync() =>
        await SqliteTestDatabase.CreateAsync(CreateQuotaStatements(
        [
            "INSERT INTO STA_continent VALUES(1,' eUrOpA ')",
            "INSERT INTO STA_country VALUES" +
            "(10,'CHI',1,0,0,0,0),(1,'CHM',1,0,0,0,0)," +
            "(3,'same',1,0,0,0,0),(4,'SAME',1,0,0,0,0)",
            "INSERT INTO STA_region VALUES(10,10),(1,1),(3,3),(4,4)",
            "INSERT INTO DYN_cyclist VALUES(10,10),(1,1),(3,3),(4,4)",
            "INSERT INTO STA_race_class VALUES(1,'CLASS'); INSERT INTO STA_race VALUES(1,1); " +
            "INSERT INTO STA_stage VALUES(1,1,1)",
            "INSERT INTO DYN_result_season_stage VALUES(1,1,0)",
            "INSERT INTO DYN_result_season VALUES" +
            "(1,10,NULL,1,NULL,NULL,NULL),(1,1,NULL,1,NULL,NULL,NULL)," +
            "(1,3,NULL,1,NULL,NULL,NULL),(1,4,NULL,1,NULL,NULL,NULL)",
            "INSERT INTO STA_classification_source VALUES(1,'RACE_FINAL'),(2,'STAGE')",
            "INSERT INTO STA_classification_type VALUES(1,'TIME')",
            "INSERT INTO STA_race_bonus VALUES(1,1,1,'(1)'),(1,2,1,'(1)')"
        ])).ConfigureAwait(false);

    private static async Task<SqliteTestDatabase> CreateTttQuotaDatabaseAsync()
    {
        string cyclists = string.Join(',', Enumerable.Range(1, 8).Select(id => $"({id},1)"));
        string results = string.Join(',', Enumerable.Range(1, 8).Select(id =>
            $"(1,{id},99,1,NULL,NULL,NULL)"));
        return await SqliteTestDatabase.CreateAsync(CreateQuotaStatements(
        [
            "INSERT INTO STA_continent VALUES(1,'  europa  ')",
            "INSERT INTO STA_country VALUES(1,'TTT',1,0,0,0,0)",
            "INSERT INTO STA_region VALUES(1,1)",
            $"INSERT INTO DYN_cyclist VALUES{cyclists}",
            "INSERT INTO STA_race_class VALUES(1,'CLASS'); INSERT INTO STA_race VALUES(1,1); " +
            "INSERT INTO STA_stage VALUES(1,1,1)",
            "INSERT INTO DYN_result_season_stage VALUES(1,0,1)",
            $"INSERT INTO DYN_result_season VALUES{results}",
            "INSERT INTO STA_classification_source VALUES(1,'STAGE')",
            "INSERT INTO STA_classification_type VALUES(1,'TIME')",
            "INSERT INTO STA_race_bonus VALUES(1,1,1,'(1)')"
        ])).ConfigureAwait(false);
    }

    private static string[] CreateQuotaStatements(IEnumerable<string> dataStatements) =>
    [
        "CREATE TABLE GAM_config(gene_i_date TEXT); INSERT INTO GAM_config VALUES('20271115')",
        @"CREATE TABLE DYN_result_season(
          fkIDstage INTEGER,fkIDcyclist INTEGER,fkIDresult_season_team INTEGER,
          gene_i_rank_stage_time INTEGER,gene_i_rank_race_time INTEGER,
          gene_i_rank_race_mountain INTEGER,gene_i_rank_race_points INTEGER)",
        "CREATE TABLE DYN_result_season_stage(IDresult_season_stage INTEGER,gene_b_isFinalStage INTEGER,gene_b_isTTT INTEGER)",
        "CREATE TABLE DYN_cyclist(IDcyclist INTEGER,fkIDregion INTEGER)",
        "CREATE TABLE STA_region(IDregion INTEGER,fkIDcountry INTEGER)",
        @"CREATE TABLE STA_country(
          IDcountry INTEGER PRIMARY KEY,CONSTANT TEXT,fkIDcontinent INTEGER,
          gene_i_num_cyclist_WC INTEGER,gene_i_num_cyclist_WC_ITT INTEGER,
          gene_i_num_cyclist_EC INTEGER,gene_i_num_cyclist_EC_ITT INTEGER)",
        "CREATE TABLE STA_continent(IDcontinent INTEGER,CONSTANT TEXT)",
        "CREATE TABLE STA_stage(IDstage INTEGER,fkIDrace INTEGER,gene_i_stage_number INTEGER)",
        "CREATE TABLE STA_race(IDrace INTEGER,fkIDrace_class INTEGER)",
        "CREATE TABLE STA_race_class(IDrace_class INTEGER,CONSTANT TEXT)",
        @"CREATE TABLE STA_race_bonus(
          fkIDrace_class INTEGER,fkIDclassification_source INTEGER,
          fkIDclassification_type INTEGER,gene_ilist_bonus TEXT)",
        "CREATE TABLE STA_classification_source(IDclassification_source_cym5 INTEGER,CONSTANT TEXT)",
        "CREATE TABLE STA_classification_type(IDclassification_type_cym5 INTEGER,CONSTANT TEXT)",
        .. dataStatements
    ];

    private static void AssertQuota(
        CountryQuotaPreview preview,
        long countryId,
        int expectedWorldRank,
        long expectedWorldRoad,
        long expectedWorldTimeTrial,
        int? expectedEuropeanRank,
        long expectedEuropeanRoad,
        long expectedEuropeanTimeTrial)
    {
        CountryQuotaChange change = preview.Changes.Single(item => item.CountryId == countryId);
        Assert.AreEqual(expectedWorldRank, change.WorldRank);
        Assert.AreEqual(expectedWorldRoad, change.NewValues.WorldRoad);
        Assert.AreEqual(expectedWorldTimeTrial, change.NewValues.WorldTimeTrial);
        Assert.AreEqual(expectedEuropeanRank, change.EuropeanRank);
        Assert.AreEqual(expectedEuropeanRoad, change.NewValues.EuropeanRoad);
        Assert.AreEqual(expectedEuropeanTimeTrial, change.NewValues.EuropeanTimeTrial);
    }
}
