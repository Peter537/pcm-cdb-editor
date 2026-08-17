using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

/// <summary>
/// Keeps the decision to replace grid content independent from any UI framework.
/// Reference identity is intentional: a completed page and its captured view state
/// are immutable snapshots owned by one table tab.
/// </summary>
internal sealed class GridContentBindingSession
{
    private object? _owner;
    private object? _rowSource;
    private object? _viewState;
    private bool _hasBinding;

    public bool IsBoundTo(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _hasBinding && ReferenceEquals(_owner, owner);
    }

    public bool BindIfChanged(
        object owner,
        object rowSource,
        object? viewState,
        Action bind)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rowSource);
        ArgumentNullException.ThrowIfNull(bind);

        if (_hasBinding &&
            ReferenceEquals(_owner, owner) &&
            ReferenceEquals(_rowSource, rowSource) &&
            ReferenceEquals(_viewState, viewState))
        {
            return false;
        }

        bind();
        _owner = owner;
        _rowSource = rowSource;
        _viewState = viewState;
        _hasBinding = true;
        return true;
    }

    public void UpdateBoundViewState(object owner, object rowSource, object? viewState)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rowSource);
        if (!IsBoundTo(owner))
        {
            throw new InvalidOperationException("Only the currently bound grid owner can update its view state.");
        }

        _rowSource = rowSource;
        _viewState = viewState;
    }

    public void ClearIfBoundTo(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (IsBoundTo(owner))
        {
            Reset();
        }
    }

    public void Reset()
    {
        _owner = null;
        _rowSource = null;
        _viewState = null;
        _hasBinding = false;
    }

    public static GridSelectionResolution<T> ResolveSelection<T>(
        GridSelection selection,
        IReadOnlyList<T> rows,
        Func<T, RowIdentity?> identitySelector,
        IEnumerable<string> visibleColumns)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(identitySelector);
        ArgumentNullException.ThrowIfNull(visibleColumns);

        var rowsByIdentity = new Dictionary<RowIdentity, T>();
        foreach (T row in rows)
        {
            if (identitySelector(row) is { } identity)
            {
                rowsByIdentity.TryAdd(identity, row);
            }
        }

        T? currentRow = selection.CurrentRow is not null &&
            rowsByIdentity.TryGetValue(selection.CurrentRow, out T? resolvedCurrent)
                ? resolvedCurrent
                : null;
        string? currentColumn = currentRow is null || selection.CurrentColumn is null
            ? null
            : visibleColumns.FirstOrDefault(column =>
                column.Equals(selection.CurrentColumn, StringComparison.OrdinalIgnoreCase));

        var seen = new HashSet<RowIdentity>();
        T[] selectedRows = selection.SelectedRows
            .Where(seen.Add)
            .Select(identity => rowsByIdentity.GetValueOrDefault(identity))
            .OfType<T>()
            .ToArray();

        return new GridSelectionResolution<T>(currentRow, currentColumn, selectedRows);
    }
}

/// <summary>
/// Projects a complete replacement off to the side and publishes the new array
/// with one assignment, so consumers never observe a partially populated source.
/// </summary>
internal sealed class BulkRowSource<T>
{
    public T[] Items { get; private set; } = [];

    public void Replace<TSource>(IEnumerable<TSource> source, Func<TSource, T> projector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);
        T[] replacement = source.Select(projector).ToArray();
        Items = replacement;
    }

    public void Clear() => Items = [];
}

internal sealed record GridSelectionResolution<T>(
    T? CurrentRow,
    string? CurrentColumn,
    IReadOnlyList<T> SelectedRows)
    where T : class;
