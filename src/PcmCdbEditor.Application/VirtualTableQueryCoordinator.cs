using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class VirtualTableQueryCoordinator : IDisposable
{
    public const int ChunkSize = 500;

    private const string CountFailureMessage = "The row count could not be computed.";
    private readonly ITableDataStore _dataStore;
    private readonly string _sqlitePath;
    private readonly DatabaseSchemaCatalog _catalog;
    private readonly object _sync = new();
    private readonly BoundedVirtualWindow<TypedRow> _window = new();
    private CancellationTokenSource _queryLifetime = new();
    private TableQuery _query;
    private TableRowCountState _countState = TableRowCountState.Unknown;
    private long _countGeneration;
    private bool _disposed;

    public VirtualTableQueryCoordinator(
        ITableDataStore dataStore,
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        TableQuery query)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        _sqlitePath = Path.GetFullPath(sqlitePath);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _query = CopyWithPage(query ?? throw new ArgumentNullException(nameof(query)), 0);
        RequireCatalogTable(_query.TableName);
    }

    public TableRowCountState CountState
    {
        get
        {
            lock (_sync)
            {
                return _countState;
            }
        }
    }

    public IReadOnlyList<VirtualChunk<TypedRow>> Chunks
    {
        get
        {
            lock (_sync)
            {
                return _window.Chunks;
            }
        }
    }

    public IReadOnlyList<TypedRow> ActiveRows
    {
        get
        {
            lock (_sync)
            {
                return _window.Items;
            }
        }
    }

    public void Reset(TableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireCatalogTable(query.TableName);

        ReplaceQuery(query);
    }

    /// <summary>
    /// Cancels in-flight reads and drops every cached row and count while preserving
    /// the current database-side query shape. Call this after a committed mutation
    /// before loading rows again.
    /// </summary>
    public void Invalidate()
    {
        CancellationTokenSource previousLifetime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previousLifetime = _queryLifetime;
            _queryLifetime = new CancellationTokenSource();
            _query = CopyWithPage(_query, 0);
            _window.Reset();
            _countState = TableRowCountState.Unknown;
            _countGeneration++;
        }

        previousLifetime.Cancel();
        previousLifetime.Dispose();
    }

    private void ReplaceQuery(TableQuery query)
    {
        CancellationTokenSource previousLifetime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previousLifetime = _queryLifetime;
            _queryLifetime = new CancellationTokenSource();
            _query = CopyWithPage(query, 0);
            _window.Reset();
            _countState = TableRowCountState.Unknown;
            _countGeneration++;
        }

        previousLifetime.Cancel();
        previousLifetime.Dispose();
    }

    public async Task<VirtualChunk<TypedRow>> LoadChunkContainingAsync(
        long rowOffset,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        var chunkOffset = rowOffset / ChunkSize * ChunkSize;
        TableQuery query;
        CancellationToken lifetimeToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_window.TryGetAtOffset(chunkOffset, out var cached))
            {
                return cached;
            }

            query = CopyWithPage(_query, chunkOffset);
            lifetimeToken = _queryLifetime.Token;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        var slice = await _dataStore.QueryRowsAsync(
                _sqlitePath,
                _catalog,
                query,
                linkedCancellation.Token)
            .ConfigureAwait(false);
        linkedCancellation.Token.ThrowIfCancellationRequested();
        if (!slice.TableName.Equals(query.TableName, StringComparison.OrdinalIgnoreCase)
            || slice.Request.Offset != chunkOffset
            || slice.Rows.Count > ChunkSize)
        {
            throw new InvalidDataException("The table data store returned an invalid virtual chunk.");
        }

        var loaded = new VirtualChunk<TypedRow>(chunkOffset, slice.Rows);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            _window.Store(loaded);
            return loaded;
        }
    }

    public async Task<TableRowCountState> LoadCountAsync(CancellationToken cancellationToken)
    {
        TableQuery query;
        CancellationToken lifetimeToken;
        long countGeneration;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            query = _query;
            lifetimeToken = _queryLifetime.Token;
            _countState = TableRowCountState.Loading;
            countGeneration = ++_countGeneration;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeToken);
        try
        {
            var count = await _dataStore.CountAsync(
                    _sqlitePath,
                    _catalog,
                    query,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var available = TableRowCountState.Available(count);
            return SetCountStateIfCurrent(available, countGeneration, lifetimeToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCountStateIfCurrent(TableRowCountState.Cancelled, countGeneration, lifetimeToken);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            SetCountStateIfCurrent(TableRowCountState.Failed(CountFailureMessage), countGeneration, lifetimeToken);
            throw;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource lifetime;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lifetime = _queryLifetime;
            _window.Reset();
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }

    private TableRowCountState SetCountStateIfCurrent(
        TableRowCountState state,
        long countGeneration,
        CancellationToken lifetimeToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (lifetimeToken == _queryLifetime.Token && countGeneration == _countGeneration)
            {
                _countState = state;
            }

            return state;
        }
    }

    private static TableQuery CopyWithPage(TableQuery query, long offset) => new(
        query.TableName,
        new PageRequest(offset, ChunkSize),
        query.Sorts,
        query.Filter,
        query.Search,
        query.ForeignKeyDisplayMode);

    private void RequireCatalogTable(string tableName)
    {
        if (!_catalog.TryGetTable(tableName, out _))
        {
            throw new ArgumentException(
                $"Table '{tableName}' is not present in the discovered schema.",
                nameof(tableName));
        }
    }
}
