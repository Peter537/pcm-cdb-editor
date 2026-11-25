using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
[TestCategory("Performance")]
public sealed class PerformancePagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(120_000)]
    public async Task MillionRowsAndHundredTablesRemainDatabaseSideBoundedAndRecoverAfterCancellation()
    {
        var setup = Stopwatch.StartNew();
        await using var database = await CreatePerformanceFixtureAsync().ConfigureAwait(false);
        setup.Stop();
        TestContext.WriteLine($"Fixture setup: {setup.Elapsed.TotalSeconds:F3} s");
        var catalog = await new SqliteTableCatalog().DiscoverAsync(database.Path, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(100, catalog.Tables.Count);
        var store = new SqliteTableDataStore();
        var query = new TableQuery(
            "DYN_large",
            new PageRequest(500_000, 100),
            [new SortDescriptor("IDrow", SortDirection.Descending)],
            new FilterCondition("bucket", FilterOperator.GreaterThanOrEqual, SqliteValue.Integer(0)),
            new GlobalSearchRequest("row-", ["label"]));
        var before = Process.GetCurrentProcess().WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        using var coordinator = new VirtualTableQueryCoordinator(store, database.Path, catalog, query);
        Assert.AreEqual(TableRowCountStatus.Unknown, coordinator.CountState.Status);
        foreach (var offset in new long[] { 500_000, 500_500, 501_000, 501_500, 502_000 })
        {
            var page = await coordinator.LoadChunkContainingAsync(offset, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(VirtualTableQueryCoordinator.ChunkSize, page.Items);
            Assert.AreEqual(1_000_000L - offset, page.Items[0].Values["IDrow"].IntegerValue);
            Assert.AreEqual(1_000_001L - offset - page.Items.Count, page.Items[^1].Values["IDrow"].IntegerValue);
        }

        var count = await coordinator.LoadCountAsync(CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();
        TestContext.WriteLine($"Deep pages and count: {stopwatch.Elapsed.TotalSeconds:F3} s");
        var workingSetGrowth = Process.GetCurrentProcess().WorkingSet64 - before;
        Assert.AreEqual(1_000_000L, count.Value);
        Assert.HasCount(BoundedVirtualWindow<TypedRow>.MaximumChunks, coordinator.Chunks);
        Assert.IsLessThanOrEqualTo(2_000, coordinator.ActiveRows.Count);
        Assert.IsLessThan(TimeSpan.FromSeconds(30), stopwatch.Elapsed);
        Assert.IsLessThan(512L * 1024 * 1024, workingSetGrowth);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.LoadChunkContainingAsync(750_000, cancelled.Token)).ConfigureAwait(false);
        coordinator.Reset(new TableQuery(
            "DYN_large",
            new PageRequest(0, 100),
            [new SortDescriptor("IDrow", SortDirection.Ascending)]));
        var recovered = await coordinator.LoadChunkContainingAsync(0, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.HasCount(VirtualTableQueryCoordinator.ChunkSize, recovered.Items);
        Assert.AreEqual(1L, recovered.Items[0].Values["IDrow"].IntegerValue);

        using var allRowsCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        var allRowsStopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.LoadAllRowsAsync(allRowsCancellation.Token)).ConfigureAwait(false);
        allRowsStopwatch.Stop();
        Assert.IsLessThan(TimeSpan.FromSeconds(10), allRowsStopwatch.Elapsed);
        Assert.IsLessThanOrEqualTo(
            BoundedVirtualWindow<TypedRow>.MaximumChunks * VirtualTableQueryCoordinator.ChunkSize,
            coordinator.ActiveRows.Count);
    }

    private static Task<SqliteTestDatabase> CreatePerformanceFixtureAsync()
    {
        var statements = Enumerable.Range(0, 99)
            .Select(index => $"CREATE TABLE INF_fixture_{index:D2}(ID INTEGER PRIMARY KEY, value TEXT)")
            .Append("CREATE TABLE DYN_large(IDrow INTEGER PRIMARY KEY, bucket INTEGER, label TEXT)")
            .ToArray();
        return SqliteTestDatabase.CreateWithTransactionalSchemaAsync(statements, @"
WITH RECURSIVE sequence(value) AS (
  VALUES(1)
  UNION ALL
  SELECT value + 1 FROM sequence WHERE value < 1000000
)
INSERT INTO DYN_large(IDrow, bucket, label)
SELECT value, value % 10, 'row-' || value FROM sequence;
CREATE INDEX IX_DYN_large_bucket_label ON DYN_large(bucket, label);");
    }
}
