namespace PcmCdbEditor.Application;

/// <summary>
/// Owns one cancellable application operation at a time. A competing caller is
/// refused and never cancels or replaces the current operation.
/// </summary>
public sealed class ExclusiveOperationGate : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeSource;
    private bool _disposed;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _activeSource is not null;
            }
        }
    }

    public bool TryEnter(
        CancellationToken lifetimeToken,
        out ExclusiveOperationLease? lease)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeSource is not null)
            {
                lease = null;
                return false;
            }

            _activeSource = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            lease = new ExclusiveOperationLease(this, _activeSource);
            return true;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = _activeSource;
            _activeSource = null;
        }

        active?.Cancel();
        active?.Dispose();
    }

    private void Exit(CancellationTokenSource source)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeSource, source))
            {
                return;
            }

            _activeSource = null;
        }

        source.Dispose();
    }

    public sealed class ExclusiveOperationLease : IDisposable
    {
        private readonly ExclusiveOperationGate _owner;
        private CancellationTokenSource? _source;

        internal ExclusiveOperationLease(
            ExclusiveOperationGate owner,
            CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => (_source
            ?? throw new ObjectDisposedException(nameof(ExclusiveOperationLease))).Token;

        public void Cancel() => _source?.Cancel();

        public void Dispose()
        {
            CancellationTokenSource? source = Interlocked.Exchange(ref _source, null);
            if (source is not null)
            {
                _owner.Exit(source);
            }
        }
    }
}
