namespace PcmCdbEditor.Application;

/// <summary>
/// Issues monotonically increasing leases so asynchronous UI work can reject
/// completions that were superseded by a newer request.
/// </summary>
public sealed class LatestRequestGate
{
    private long _generation;

    public RequestLease Begin() => new(this, Interlocked.Increment(ref _generation));

    public void Invalidate() => Interlocked.Increment(ref _generation);

    private bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;

    public readonly struct RequestLease
    {
        private readonly LatestRequestGate? _owner;

        internal RequestLease(LatestRequestGate owner, long generation)
        {
            _owner = owner;
            Generation = generation;
        }

        public long Generation { get; }

        public bool IsCurrent => _owner is not null && _owner.IsCurrent(Generation);

        public void ThrowIfSuperseded(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent)
            {
                throw new OperationCanceledException(
                    "A newer request superseded this table load.",
                    cancellationToken);
            }
        }
    }
}
