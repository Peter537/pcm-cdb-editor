using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class SqliteResponsivenessTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task WideForeignKeyPageUsesBoundedLookupsWithoutCorrelatedProjection()
    {
        const int relationshipCount = 12;
        const int payloadColumnCount = 79;
        string[] relationshipSuffixes = Enumerable.Range(0, relationshipCount)
            .Select(static index => $"lookup{index:D2}")
            .ToArray();
        var statements = new List<string>();
        foreach (string suffix in relationshipSuffixes)
        {
            statements.Add($"CREATE TABLE STA_{suffix}(ID{suffix} INTEGER, gene_sz_name TEXT)");
            statements.Add($@"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 500
)
INSERT INTO STA_{suffix}(ID{suffix}, gene_sz_name)
SELECT value, '{suffix}-' || value FROM sequence;");
        }

        string foreignKeyColumns = string.Join(", ", relationshipSuffixes.Select(
            static suffix => $"fkID{suffix} INTEGER"));
        string payloadColumns = string.Join(", ", Enumerable.Range(0, payloadColumnCount).Select(
            static index => $"gene_i_payload{index:D2} INTEGER"));
        statements.Add(
            $"CREATE TABLE DYN_cyclist(IDcyclist INTEGER PRIMARY KEY, {foreignKeyColumns}, {payloadColumns})");
        string projectedValues = string.Join(", ", relationshipSuffixes.Select(static _ =>
            "CASE WHEN value = 1 THEN NULL WHEN value = 2 THEN 999999 " +
            "WHEN value = 3 THEN 499 ELSE ((value - 1) % 500) + 1 END"));
        string payloadValues = string.Join(", ", Enumerable.Range(0, payloadColumnCount).Select(
            static _ => "value"));
        statements.Add($@"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 10000
)
INSERT INTO DYN_cyclist
SELECT value, {projectedValues}, {payloadValues} FROM sequence;");
        statements.Add("INSERT INTO STA_lookup00 VALUES(499, 'ambiguous-lookup00')");

        await using var database = await SqliteTestDatabase.CreateAsync(statements.ToArray())
            .ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var query = new TableQuery(
            "DYN_cyclist",
            new PageRequest(0, 500),
            foreignKeyDisplayMode: ForeignKeyDisplayMode.RawAndName);

        string commandText = SqliteTableDataStore.BuildPageCommandTextForDiagnostics(catalog, query);
        Assert.IsFalse(
            commandText.Contains("SELECT COUNT", StringComparison.OrdinalIgnoreCase),
            "Ordinary page projection must not contain per-row relationship counts.");
        IReadOnlyList<string> planDetails = await ReadQueryPlanAsync(database.Path, commandText)
            .ConfigureAwait(false);
        Assert.IsFalse(
            planDetails.Any(static detail => detail.Contains("CORRELATED", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, planDetails));

        var stopwatch = Stopwatch.StartNew();
        TableSlice slice = await new SqliteTableDataStore()
            .QueryRowsAsync(database.Path, catalog, query, CancellationToken.None)
            .ConfigureAwait(false);
        stopwatch.Stop();

        Assert.HasCount(500, slice.Rows);
        Assert.AreEqual(92, catalog.Tables.Single(table => table.Name == "DYN_cyclist").Columns.Count);
        Assert.IsFalse(slice.Rows[0].RelatedValues.ContainsKey("fkIDlookup00"));
        Assert.AreEqual(RelationshipResolution.MissingTarget, slice.Rows[1].RelatedValues["fkIDlookup00"].Resolution);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, slice.Rows[2].RelatedValues["fkIDlookup00"].Resolution);
        Assert.AreEqual(
            "lookup00-4",
            slice.Rows[3].RelatedValues["fkIDlookup00"].Label);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), stopwatch.Elapsed);

        var store = new SqliteTableDataStore();
        TableSlice raw = await store.QueryRowsAsync(
            database.Path,
            catalog,
            new TableQuery(
                "DYN_cyclist",
                new PageRequest(0, 4),
                foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(raw.Rows[3].Values.ContainsKey("fkIDlookup00__display"));
        TableSlice names = await store.QueryRowsAsync(
            database.Path,
            catalog,
            new TableQuery(
                "DYN_cyclist",
                new PageRequest(0, 4),
                foreignKeyDisplayMode: ForeignKeyDisplayMode.ResolvedName),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("lookup00-4", names.Rows[3].RelatedValues["fkIDlookup00"].Label);
    }

    [TestMethod]
    public async Task ResolvedDisplaySortIsDatabaseSideStableAcrossPages()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_country(IDcountry INTEGER, gene_sz_name TEXT)",
            "INSERT INTO STA_country VALUES(1, 'Zulu'), (2, 'Alpha')",
            "CREATE TABLE DYN_rider(IDrider INTEGER PRIMARY KEY, fkIDcountry INTEGER)",
            "INSERT INTO DYN_rider VALUES(12, 1), (11, 2), (10, 2)")
            .ConfigureAwait(false);
        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        var baseQuery = new TableQuery(
            "DYN_rider",
            new PageRequest(0, 1),
            [new SortDescriptor("fkIDcountry", SortDirection.Ascending, QueryValueTarget.RelatedLabel)],
            foreignKeyDisplayMode: ForeignKeyDisplayMode.ResolvedName);

        string commandText = SqliteTableDataStore.BuildPageCommandTextForDiagnostics(catalog, baseQuery);
        StringAssert.Contains(commandText, "AS MATERIALIZED", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> planDetails = await ReadQueryPlanAsync(database.Path, commandText)
            .ConfigureAwait(false);
        Assert.IsTrue(
            planDetails.Any(static detail => detail.Contains("AUTOMATIC COVERING INDEX", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, planDetails));

        var store = new SqliteTableDataStore();
        var orderedIds = new List<long>();
        for (var offset = 0; offset < 3; offset++)
        {
            var query = new TableQuery(
                baseQuery.TableName,
                new PageRequest(offset, 1),
                baseQuery.Sorts,
                foreignKeyDisplayMode: baseQuery.ForeignKeyDisplayMode);
            TableSlice slice = await store.QueryRowsAsync(
                    database.Path,
                    catalog,
                    query,
                    CancellationToken.None)
                .ConfigureAwait(false);
            orderedIds.Add(slice.Rows[0].Values["IDrider"].IntegerValue);
        }

        CollectionAssert.AreEqual(new long[] { 10, 11, 12 }, orderedIds);
    }

    [TestMethod]
    public async Task DuplicateTargetRelationshipsShareBoundedLookupBatches()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER, gene_sz_name TEXT)",
            @"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 500
)
INSERT INTO STA_lookup
SELECT value, 'Lookup ' || value FROM sequence;",
            "INSERT INTO STA_lookup VALUES(7, 'Duplicate lookup')",
            "CREATE TABLE DYN_source(IDrow INTEGER PRIMARY KEY, fkIDprimary, fkIDsecondary)",
            @"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 500
)
INSERT INTO DYN_source
SELECT value, value, 501 - value FROM sequence;",
            "UPDATE DYN_source SET fkIDsecondary = CAST(777.25 AS REAL) WHERE IDrow = 1")
            .ConfigureAwait(false);
        DatabaseSchemaCatalog discovered = await new SqliteTableCatalog()
            .DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        TableSchema source = discovered.Tables.Single(table => table.Name == "DYN_source");
        var relatedSource = new TableSchema(
            source.Name,
            source.ObjectKind,
            source.Columns,
            [
                new ForeignKeyRelation(
                    "fkIDprimary",
                    "STA_lookup",
                    "IDlookup",
                    "gene_sz_name",
                    IsDeclared: true,
                    Confidence: "Neutral test relationship"),
                new ForeignKeyRelation(
                    "fkIDsecondary",
                    "sta_LOOKUP",
                    "idLOOKUP",
                    "GENE_SZ_NAME",
                    IsDeclared: true,
                    Confidence: "Neutral test relationship")
            ],
            source.StableIdentity,
            source.EditCapability,
            source.EstimatedRowCount,
            source.IsWithoutRowId);
        var catalog = new DatabaseSchemaCatalog(
            discovered.SchemaSignature,
            discovered.Tables.Select(table => table.Name == source.Name ? relatedSource : table));
        var query = new TableQuery(
            source.Name,
            new PageRequest(0, 500),
            foreignKeyDisplayMode: ForeignKeyDisplayMode.RawAndName);

        string commandText = SqliteTableDataStore.BuildPageCommandTextForDiagnostics(catalog, query);
        Assert.IsFalse(
            commandText.Contains("STA_lookup", StringComparison.OrdinalIgnoreCase),
            "An ordinary page command must not project relationship targets.");
        IReadOnlyList<string> planDetails = await ReadQueryPlanAsync(database.Path, commandText)
            .ConfigureAwait(false);
        Assert.IsFalse(
            planDetails.Any(static detail => detail.Contains("CORRELATED", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, planDetails));

        var commands = new List<SqliteTableDataStore.CommandKind>();
        var store = new SqliteTableDataStore(commands.Add);
        TableSlice slice = await store.QueryRowsAsync(
                database.Path,
                catalog,
                query,
                CancellationToken.None)
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[]
            {
                SqliteTableDataStore.CommandKind.Page,
                SqliteTableDataStore.CommandKind.ForeignKeyLookup,
                SqliteTableDataStore.CommandKind.ForeignKeyLookup
            },
            commands);
        Assert.AreEqual("Lookup 1", slice.Rows[0].RelatedValues["fkIDprimary"].Label);
        Assert.AreEqual(SqliteValueKind.Real, slice.Rows[0].Values["fkIDsecondary"].Kind);
        Assert.AreEqual(RelationshipResolution.MissingTarget, slice.Rows[0].RelatedValues["fkIDsecondary"].Resolution);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, slice.Rows[6].RelatedValues["fkIDprimary"].Resolution);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, slice.Rows[493].RelatedValues["fkIDsecondary"].Resolution);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task RelatedSearchGroupsUnindexedTargetsOnceAndSharesTheCountPredicate()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER, gene_sz_name TEXT)",
            """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM numbers WHERE value<500)
            INSERT INTO STA_lookup SELECT value, 'Target ' || value FROM numbers;
            """,
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup INTEGER)",
            """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM numbers WHERE value<10000)
            INSERT INTO data SELECT value, ((value-1) % 500)+1 FROM numbers;
            """).ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var query = new TableQuery("data", new PageRequest(0, 5),
            search: new GlobalSearchRequest("Target 421", ["fkIDlookup"]), foreignKeyDisplayMode: ForeignKeyDisplayMode.RawValue);
        var details = await ReadQueryPlanAsync(database.Path,
            SqliteTableDataStore.BuildPageCommandTextForDiagnostics(catalog, query), "%Target 421%").ConfigureAwait(false);
        Assert.AreEqual(1, details.Count(detail => detail.Contains("SCAN main.STA_lookup", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, details));
        Assert.IsTrue(details.Any(detail => detail.Contains("AUTOMATIC COVERING INDEX", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, details));
        var store = new SqliteTableDataStore();
        var stopwatch = Stopwatch.StartNew();
        var page = await store.QueryRowsAsync(database.Path, catalog, query, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(5, page.Rows);
        Assert.AreEqual(421L, page.Rows[0].Values["ID"].IntegerValue);
        Assert.AreEqual(20L, await store.CountAsync(database.Path, catalog, query, CancellationToken.None).ConfigureAwait(false));
        var all = await store.QueryAllRowsAsync(database.Path, catalog, query.Definition, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(20, all);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), stopwatch.Elapsed);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task AllRowsStagesDistinctKeysAndReadsEachUnindexedTargetOnce()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE STA_lookup(IDlookup INTEGER, gene_sz_name TEXT)",
            """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM numbers WHERE value<20000)
            INSERT INTO STA_lookup SELECT value, 'Target ' || value FROM numbers;
            """,
            "CREATE TABLE data(ID INTEGER PRIMARY KEY, fkIDlookup INTEGER)",
            "INSERT INTO data SELECT IDlookup, IDlookup FROM STA_lookup",
            "INSERT INTO STA_lookup VALUES(7, 'Duplicate')",
            "CREATE TABLE __pcm_lookup_keys(note TEXT); INSERT INTO __pcm_lookup_keys VALUES('preserved')")
            .ConfigureAwait(false);
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None).ConfigureAwait(false);
        var commands = new List<SqliteTableDataStore.CommandKind>();
        var stopwatch = Stopwatch.StartNew();
        var rows = await new SqliteTableDataStore(commands.Add).QueryAllRowsAsync(database.Path, catalog,
            new TableQueryDefinition("data"), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(20000, rows);
        Assert.AreEqual("Target 20000", rows[^1].RelatedValues["fkIDlookup"].Label);
        Assert.AreEqual(RelationshipResolution.AmbiguousKey, rows[6].RelatedValues["fkIDlookup"].Resolution);
        CollectionAssert.AreEqual(new[]
        {
            SqliteTableDataStore.CommandKind.Page,
            SqliteTableDataStore.CommandKind.ForeignKeyLookup
        }, commands);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), stopwatch.Elapsed);

        using var cancellation = new CancellationTokenSource();
        var cancelledStore = new SqliteTableDataStore(kind =>
        {
            if (kind == SqliteTableDataStore.CommandKind.ForeignKeyLookup)
            {
                cancellation.Cancel();
            }
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledStore.QueryAllRowsAsync(
            database.Path, catalog, new TableQueryDefinition("data"), cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual("preserved", await database.ScalarAsync<string>(
            "SELECT note FROM __pcm_lookup_keys").ConfigureAwait(false));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task NativeInterruptCancelsInFlightWorkAndConnectionRemainsUsable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var dispatchStopwatch = Stopwatch.StartNew();
        Task<long> operation = SqliteOperationRunner.RunAsync(
            async () =>
            {
                using var interruptRegistration = SqliteOperationRunner.RegisterInterrupt(
                    connection,
                    cancellation.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = @"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 100000000
)
SELECT SUM(value) FROM sequence";
                started.TrySetResult();
                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellation.Token);
        dispatchStopwatch.Stop();
        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), dispatchStopwatch.Elapsed);
        Assert.IsFalse(operation.IsCompleted);

        await started.Task.ConfigureAwait(false);
        var interruptStopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation).ConfigureAwait(false);
        interruptStopwatch.Stop();
        stopwatch.Stop();
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), interruptStopwatch.Elapsed);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), stopwatch.Elapsed);

        await using var verification = connection.CreateCommand();
        verification.CommandText = "SELECT 1";
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await verification.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));

        await using var createTable = connection.CreateCommand();
        createTable.CommandText = "CREATE TABLE transaction_probe(value INTEGER)";
        await createTable.ExecuteNonQueryAsync().ConfigureAwait(false);
        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO transaction_probe VALUES(1)";
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM transaction_probe";
        Assert.AreEqual(0L, Convert.ToInt64(
            await count.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task InterruptedWritePreservesCancellationAndLeavesDatabaseReusable()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(
            "CREATE TABLE neutral(IDneutral INTEGER PRIMARY KEY, value INTEGER)")
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = SqliteOperationRunner.RunAsync(
            async () =>
            {
                await using var connection = SqliteSupport.CreateConnection(database.Path);
                using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                        connection,
                        cancellation.Token)
                    .ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellation.Token)
                    .ConfigureAwait(false);
                var sqliteTransaction = (SqliteTransaction)transaction;
                try
                {
                    await using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = sqliteTransaction;
                        insert.CommandText = "INSERT INTO neutral VALUES(1, 0)";
                        await insert.ExecuteNonQueryAsync(cancellation.Token).ConfigureAwait(false);
                    }

                    await using var update = connection.CreateCommand();
                    update.Transaction = sqliteTransaction;
                    update.CommandText = @"
UPDATE neutral
SET value = (
  WITH RECURSIVE sequence(value) AS (
    VALUES(1)
    UNION ALL
    SELECT value + 1 FROM sequence WHERE value < 100000000
  )
  SELECT SUM(value) FROM sequence
)
WHERE IDneutral = 1";
                    started.TrySetResult();
                    await update.ExecuteNonQueryAsync(cancellation.Token).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch
                {
                    await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                        .ConfigureAwait(false);
                    throw;
                }
            },
            cancellation.Token);

        await started.Task.ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        cancellation.Cancel();
        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(
                () => operation)
            .ConfigureAwait(false);
        Assert.IsInstanceOfType<SqliteException>(canceled.InnerException);
        Assert.AreEqual(0L, await database.ScalarAsync<long>("SELECT COUNT(*) FROM neutral")
            .ConfigureAwait(false));

        await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await verification.OpenAsync().ConfigureAwait(false);
        await using (SqliteTransaction transaction = verification.BeginTransaction())
        {
            await using var insert = verification.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO neutral VALUES(2, 42)";
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        await using var query = verification.CreateCommand();
        query.CommandText = "SELECT value FROM neutral WHERE IDneutral = 2";
        Assert.AreEqual(42L, Convert.ToInt64(
            await query.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<string>> ReadQueryPlanAsync(
        string sqlitePath,
        string commandText,
        string? searchPattern = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {commandText}";
        command.Parameters.AddWithValue("$limit", 501);
        command.Parameters.AddWithValue("$offset", 0);
        if (searchPattern is not null)
        {
            command.Parameters.AddWithValue("$p0", searchPattern);
        }
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }
}
