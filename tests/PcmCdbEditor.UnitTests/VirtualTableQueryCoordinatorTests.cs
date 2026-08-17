using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class VirtualTableQueryCoordinatorTests
{
    [TestMethod]
    public async Task CountStartsUnknownLoadsLazilyAndRecoversAfterIndependentCancellation()
    {
        var store = new RecordingTableDataStore { CountResult = 2_345 };
        using var coordinator = CreateCoordinator(store);

        Assert.AreEqual(TableRowCountStatus.Unknown, coordinator.CountState.Status);
        Assert.AreEqual(0, store.CountCalls);

        store.BlockCount = true;
        using var cancelled = new CancellationTokenSource();
        Task<TableRowCountState> pending = coordinator.LoadCountAsync(cancelled.Token);
        await store.CountStarted.Task.ConfigureAwait(false);
        Assert.AreEqual(TableRowCountStatus.Loading, coordinator.CountState.Status);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        Assert.AreEqual(TableRowCountStatus.Cancelled, coordinator.CountState.Status);

        store.BlockCount = false;
        TableRowCountState recovered = await coordinator.LoadCountAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(TableRowCountStatus.Available, recovered.Status);
        Assert.AreEqual(2_345L, recovered.Value);
        Assert.AreEqual(2, store.CountCalls);

        VirtualChunk<TypedRow> chunk = await coordinator.LoadChunkContainingAsync(0, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.HasCount(VirtualTableQueryCoordinator.ChunkSize, chunk.Items);
    }

    [TestMethod]
    public async Task ChunksAlignBoundariesUseLruEvictionAndRefetchEvictedRanges()
    {
        var store = new RecordingTableDataStore();
        using var coordinator = CreateCoordinator(store);

        VirtualChunk<TypedRow> first = await coordinator.LoadChunkContainingAsync(499, CancellationToken.None)
            .ConfigureAwait(false);
        VirtualChunk<TypedRow> same = await coordinator.LoadChunkContainingAsync(0, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreSame(first, same);
        Assert.AreEqual(1, store.RowQueryCalls);
        Assert.AreEqual(0L, store.Queries[0].Page.Offset);
        Assert.AreEqual(VirtualTableQueryCoordinator.ChunkSize, store.Queries[0].Page.Limit);

        foreach (var offset in new long[] { 500, 1_000, 1_500, 2_000 })
        {
            await coordinator.LoadChunkContainingAsync(offset, CancellationToken.None).ConfigureAwait(false);
        }

        Assert.HasCount(BoundedVirtualWindow<TypedRow>.MaximumChunks, coordinator.Chunks);
        Assert.IsLessThanOrEqualTo(
            BoundedVirtualWindow<TypedRow>.MaximumChunks * VirtualTableQueryCoordinator.ChunkSize,
            coordinator.ActiveRows.Count);
        Assert.IsFalse(coordinator.Chunks.Any(static chunk => chunk.Offset == 0));

        await coordinator.LoadChunkContainingAsync(5, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(6, store.RowQueryCalls);
        Assert.IsTrue(coordinator.Chunks.Any(static chunk => chunk.Offset == 0));
        Assert.IsFalse(coordinator.Chunks.Any(static chunk => chunk.Offset == 500));
    }

    [TestMethod]
    public async Task ResetCancelsOldFetchClearsRowsAndPreservesDatabaseQueryShape()
    {
        var store = new RecordingTableDataStore { BlockRows = true };
        var original = new TableQuery(
            "items",
            new PageRequest(73, 12),
            [new SortDescriptor("ID", SortDirection.Descending)],
            new FilterCondition("ID", FilterOperator.GreaterThan, SqliteValue.Integer(10)),
            new GlobalSearchRequest("needle", ["label"]),
            ForeignKeyDisplayMode.RawValue);
        using var coordinator = CreateCoordinator(store, original);

        Task<VirtualChunk<TypedRow>> pending = coordinator.LoadChunkContainingAsync(700, CancellationToken.None);
        await store.RowsStarted.Task.ConfigureAwait(false);
        coordinator.Reset(original);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        Assert.IsEmpty(coordinator.Chunks);
        Assert.AreEqual(TableRowCountStatus.Unknown, coordinator.CountState.Status);

        store.BlockRows = false;
        await coordinator.LoadChunkContainingAsync(700, CancellationToken.None).ConfigureAwait(false);
        TableQuery captured = store.Queries[^1];
        Assert.AreEqual(500L, captured.Page.Offset);
        Assert.AreEqual(VirtualTableQueryCoordinator.ChunkSize, captured.Page.Limit);
        Assert.AreEqual(original.Sorts[0], captured.Sorts[0]);
        Assert.AreSame(original.Filter, captured.Filter);
        Assert.AreSame(original.Search, captured.Search);
        Assert.AreEqual(ForeignKeyDisplayMode.RawValue, captured.ForeignKeyDisplayMode);
    }

    [TestMethod]
    public async Task InvalidateDropsCachedRowsAndCountThenRefetchesTheSameQuery()
    {
        var store = new RecordingTableDataStore { CountResult = 731 };
        var query = new TableQuery(
            "items",
            new PageRequest(0, 100),
            [new SortDescriptor("ID", SortDirection.Descending)],
            new FilterCondition("ID", FilterOperator.GreaterThan, SqliteValue.Integer(10)),
            new GlobalSearchRequest("needle", ["label"]),
            ForeignKeyDisplayMode.RawAndName);
        using var coordinator = CreateCoordinator(store, query);

        VirtualChunk<TypedRow> original = await coordinator.LoadChunkContainingAsync(
                0,
                CancellationToken.None)
            .ConfigureAwait(false);
        _ = await coordinator.LoadCountAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(VirtualTableQueryCoordinator.ChunkSize, original.Items);
        Assert.AreEqual(TableRowCountStatus.Available, coordinator.CountState.Status);
        Assert.AreEqual(1, store.RowQueryCalls);
        Assert.AreEqual(1, store.CountCalls);

        coordinator.Invalidate();

        Assert.IsEmpty(coordinator.Chunks);
        Assert.IsEmpty(coordinator.ActiveRows);
        Assert.AreEqual(TableRowCountStatus.Unknown, coordinator.CountState.Status);

        _ = await coordinator.LoadChunkContainingAsync(0, CancellationToken.None).ConfigureAwait(false);
        _ = await coordinator.LoadCountAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, store.RowQueryCalls);
        Assert.AreEqual(2, store.CountCalls);
        TableQuery reloaded = store.Queries[^1];
        Assert.AreEqual(query.Sorts[0], reloaded.Sorts[0]);
        Assert.AreSame(query.Filter, reloaded.Filter);
        Assert.AreSame(query.Search, reloaded.Search);
        Assert.AreEqual(query.ForeignKeyDisplayMode, reloaded.ForeignKeyDisplayMode);
    }

    [TestMethod]
    public async Task CallerCancellationRejectsLateRowsFromANonCooperativeStore()
    {
        var store = new RecordingTableDataStore
        {
            BlockRows = true,
            IgnoreRowCancellation = true,
        };
        using var coordinator = CreateCoordinator(store);
        using var cancellation = new CancellationTokenSource();

        Task<VirtualChunk<TypedRow>> pending = coordinator.LoadChunkContainingAsync(
            0,
            cancellation.Token);
        await store.RowsStarted.Task.ConfigureAwait(false);
        cancellation.Cancel();
        store.ReleaseRows.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        Assert.IsEmpty(coordinator.Chunks);
    }

    [TestMethod]
    public async Task CountsRemainIndependentAcrossTabsAndRejectLateCancellation()
    {
        var firstStore = new RecordingTableDataStore
        {
            BlockCount = true,
            IgnoreCountCancellation = true,
            CountResult = 111,
        };
        var secondStore = new RecordingTableDataStore
        {
            BlockCount = true,
            IgnoreCountCancellation = true,
            CountResult = 222,
        };
        using var first = CreateCoordinator(firstStore);
        using var second = CreateCoordinator(secondStore);
        using var firstCancellation = new CancellationTokenSource();

        Task<TableRowCountState> firstPending = first.LoadCountAsync(firstCancellation.Token);
        Task<TableRowCountState> secondPending = second.LoadCountAsync(CancellationToken.None);
        await Task.WhenAll(firstStore.CountStarted.Task, secondStore.CountStarted.Task)
            .ConfigureAwait(false);

        firstCancellation.Cancel();
        firstStore.ReleaseCount.TrySetResult();
        secondStore.ReleaseCount.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => firstPending).ConfigureAwait(false);
        TableRowCountState secondResult = await secondPending.ConfigureAwait(false);
        Assert.AreEqual(TableRowCountStatus.Cancelled, first.CountState.Status);
        Assert.AreEqual(TableRowCountStatus.Available, secondResult.Status);
        Assert.AreEqual(222L, secondResult.Value);
    }

    private static VirtualTableQueryCoordinator CreateCoordinator(
        RecordingTableDataStore store,
        TableQuery? query = null)
    {
        var table = new TableSchema(
            "items",
            TableObjectKind.Table,
            [
                new ColumnSchema(0, "ID", "INTEGER", SqliteAffinity.Integer, false, null, 1, false, false),
                new ColumnSchema(1, "label", "TEXT", SqliteAffinity.Text, true, null, 0, false, false)
            ],
            relationships: null,
            new StableIdentityDefinition(StableIdentityKind.DeclaredPrimaryKey, ["ID"]),
            TableEditCapability.Editable,
            estimatedRowCount: null,
            isWithoutRowId: false);
        var catalog = new DatabaseSchemaCatalog("test-signature", [table]);
        return new VirtualTableQueryCoordinator(
            store,
            "virtual-test.sqlite",
            catalog,
            query ?? new TableQuery("items", new PageRequest(0, 100)));
    }

    private sealed class RecordingTableDataStore : ITableDataStore
    {
        public List<TableQuery> Queries { get; } = [];

        public TaskCompletionSource CountStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RowsStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCount { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRows { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockCount { get; set; }

        public bool BlockRows { get; set; }

        public bool IgnoreCountCancellation { get; set; }

        public bool IgnoreRowCancellation { get; set; }

        public long CountResult { get; set; } = 10_000;

        public int CountCalls { get; private set; }

        public int RowQueryCalls { get; private set; }

        public Task<TablePage> QueryAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            TableQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<TableSlice> QueryRowsAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            TableQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            RowQueryCalls++;
            RowsStarted.TrySetResult();
            if (BlockRows)
            {
                if (IgnoreRowCancellation)
                {
                    await ReleaseRows.Task.ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
            }

            var rows = Enumerable.Range(0, query.Page.Limit)
                .Select(index => CreateRow(query.Page.Offset + index))
                .ToArray();
            return new TableSlice(query.TableName, query.Page, rows, hasMore: true);
        }

        public Task<long> CountAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            string tableName,
            FilterExpression? filter,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<long> CountAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            TableQuery query,
            CancellationToken cancellationToken)
        {
            CountCalls++;
            CountStarted.TrySetResult();
            if (BlockCount)
            {
                if (IgnoreCountCancellation)
                {
                    await ReleaseCount.Task.ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
            }

            return CountResult;
        }

        public Task<EditResult> UpdateCellAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            CellUpdateOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditResult> UpdateRowAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            RowUpdateOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditResult> InsertRowAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            RowInsertionOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditResult> DeleteRowAsync(
            string sqlitePath,
            DatabaseSchemaCatalog catalog,
            RowDeletionOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private static TypedRow CreateRow(long id) => new(
            RowIdentity.FromPrimaryKey([new RowIdentityComponent("ID", SqliteValue.Integer(id))]),
            new Dictionary<string, SqliteValue>
            {
                ["ID"] = SqliteValue.Integer(id),
                ["label"] = SqliteValue.Text($"row-{id}")
            });
    }
}
