namespace PcmCdbEditor.Application;

public sealed class VirtualChunk<T>
{
    public VirtualChunk(long offset, IEnumerable<T> items)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        Offset = offset;
        Items = Array.AsReadOnly((items ?? throw new ArgumentNullException(nameof(items))).ToArray());
    }

    public long Offset { get; }

    public IReadOnlyList<T> Items { get; }
}

public sealed class BoundedVirtualWindow<T>
{
    public const int MaximumChunks = 4;

    private readonly LinkedList<VirtualChunk<T>> _chunks = new();

    public IReadOnlyList<VirtualChunk<T>> Chunks => _chunks.OrderBy(static chunk => chunk.Offset).ToArray();

    public IReadOnlyList<T> Items => Chunks.SelectMany(static chunk => chunk.Items).ToArray();

    public long? FirstOffset => _chunks.Count == 0 ? null : _chunks.Min(static chunk => chunk.Offset);

    public long? NextOffset => _chunks.Count == 0
        ? null
        : _chunks.Max(static chunk => chunk.Offset + chunk.Items.Count);

    public void Add(VirtualChunk<T> chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var existing = _chunks.First;
        while (existing is not null)
        {
            if (existing.Value.Offset == chunk.Offset)
            {
                existing.Value = chunk;
                return;
            }

            existing = existing.Next;
        }

        if (_chunks.Last is not null && chunk.Offset < _chunks.Last.Value.Offset)
        {
            throw new InvalidOperationException("Virtual chunks must be added in increasing offset order.");
        }

        _chunks.AddLast(chunk);
        while (_chunks.Count > MaximumChunks)
        {
            _chunks.RemoveFirst();
        }
    }

    public void Store(VirtualChunk<T> chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var existing = FindByOffset(chunk.Offset);
        if (existing is not null)
        {
            existing.Value = chunk;
            _chunks.Remove(existing);
        }

        _chunks.AddLast(chunk);
        while (_chunks.Count > MaximumChunks)
        {
            _chunks.RemoveFirst();
        }
    }

    public bool TryGetContaining(long rowOffset, out VirtualChunk<T> chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);

        var current = _chunks.First;
        while (current is not null)
        {
            var candidate = current.Value;
            if (rowOffset >= candidate.Offset
                && (rowOffset < candidate.Offset + candidate.Items.Count
                    || rowOffset == candidate.Offset && candidate.Items.Count == 0))
            {
                chunk = candidate;
                _chunks.Remove(current);
                _chunks.AddLast(current);
                return true;
            }

            current = current.Next;
        }

        chunk = null!;
        return false;
    }

    public bool TryGetAtOffset(long offset, out VirtualChunk<T> chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var existing = FindByOffset(offset);
        if (existing is null)
        {
            chunk = null!;
            return false;
        }

        chunk = existing.Value;
        _chunks.Remove(existing);
        _chunks.AddLast(existing);
        return true;
    }

    public void Reset(VirtualChunk<T>? initialChunk = null)
    {
        _chunks.Clear();
        if (initialChunk is not null)
        {
            _chunks.AddLast(initialChunk);
        }
    }

    private LinkedListNode<VirtualChunk<T>>? FindByOffset(long offset)
    {
        var current = _chunks.First;
        while (current is not null)
        {
            if (current.Value.Offset == offset)
            {
                return current;
            }

            current = current.Next;
        }

        return null;
    }
}
