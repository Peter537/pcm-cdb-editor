using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

/// <summary>
/// A non-persistent implementation used by focused application tests. Production
/// sessions use the infrastructure history with the same replay semantics.
/// </summary>
public sealed class InMemoryEditHistory : IEditHistory
{
    private readonly Stack<HistoryEntry> _undo = [];
    private readonly Stack<HistoryEntry> _redo = [];
    private PendingReplay? _pending;
    private Guid _currentStateId = Guid.NewGuid();
    private Guid _savedStateId;

    public InMemoryEditHistory()
    {
        _savedStateId = _currentStateId;
    }

    public EditHistoryState State => new(
        _undo.Count > 0,
        _redo.Count > 0,
        _undo.Count,
        _redo.Count,
        _currentStateId != _savedStateId,
        _pending is not null);

    public void Record(EditOperation operation) => Record(operation, []);

    public void Record(EditOperation operation, IEnumerable<RowReplayGuard> undoGuards)
    {
        EnsureNoPendingReplay();
        ArgumentNullException.ThrowIfNull(operation);
        var entry = new HistoryEntry(
            operation,
            FreezeGuards(undoGuards),
            _currentStateId,
            Guid.NewGuid());
        _undo.Push(entry);
        _redo.Clear();
        _currentStateId = entry.AfterStateId;
    }

    public EditOperation TakeUndo() => TakeUndoReplay().Operation;

    public EditOperation TakeRedo() => TakeRedoReplay().Operation;

    public EditHistoryReplay TakeUndoReplay() => TakeReplay(_undo, EditReplayDirection.Undo);

    public EditHistoryReplay TakeRedoReplay() => TakeReplay(_redo, EditReplayDirection.Redo);

    public void CompleteUndo(EditOperation operation) =>
        CompleteLegacy(operation, EditReplayDirection.Undo);

    public void CompleteRedo(EditOperation operation) =>
        CompleteLegacy(operation, EditReplayDirection.Redo);

    public void CompleteUndo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> redoGuards) =>
        CompleteReplay(replay, EditReplayDirection.Undo, redoGuards);

    public void CompleteRedo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> undoGuards) =>
        CompleteReplay(replay, EditReplayDirection.Redo, undoGuards);

    public void RestoreFailedUndo(EditOperation operation) =>
        RestoreLegacy(operation, EditReplayDirection.Undo);

    public void RestoreFailedRedo(EditOperation operation) =>
        RestoreLegacy(operation, EditReplayDirection.Redo);

    public void RestoreFailedUndo(EditHistoryReplay replay) =>
        RestoreReplay(replay, EditReplayDirection.Undo);

    public void RestoreFailedRedo(EditHistoryReplay replay) =>
        RestoreReplay(replay, EditReplayDirection.Redo);

    public void MarkSavedBaseline()
    {
        EnsureNoPendingReplay();
        _savedStateId = _currentStateId;
    }

    public void Clear()
    {
        EnsureNoPendingReplay();
        _undo.Clear();
        _redo.Clear();
    }

    private EditHistoryReplay TakeReplay(Stack<HistoryEntry> source, EditReplayDirection direction)
    {
        EnsureNoPendingReplay();
        if (source.Count == 0)
        {
            throw new InvalidOperationException($"There is no operation to {direction.ToString().ToLowerInvariant()}.");
        }

        HistoryEntry entry = source.Pop();
        var replay = new EditHistoryReplay(entry.Operation, direction, entry.Guards);
        _pending = new PendingReplay(entry, replay);
        return replay;
    }

    private void CompleteLegacy(EditOperation operation, EditReplayDirection direction)
    {
        PendingReplay pending = RequirePending(operation, direction);
        CompleteReplay(pending.Replay, direction, []);
    }

    private void CompleteReplay(
        EditHistoryReplay replay,
        EditReplayDirection direction,
        IEnumerable<RowReplayGuard> oppositeGuards)
    {
        PendingReplay pending = RequirePending(replay, direction);
        var completed = pending.Entry with { Guards = FreezeGuards(oppositeGuards) };
        if (direction == EditReplayDirection.Undo)
        {
            _redo.Push(completed);
            _currentStateId = completed.BeforeStateId;
        }
        else
        {
            _undo.Push(completed);
            _currentStateId = completed.AfterStateId;
        }

        _pending = null;
    }

    private void RestoreLegacy(EditOperation operation, EditReplayDirection direction)
    {
        PendingReplay pending = RequirePending(operation, direction);
        RestoreReplay(pending.Replay, direction);
    }

    private void RestoreReplay(EditHistoryReplay replay, EditReplayDirection direction)
    {
        PendingReplay pending = RequirePending(replay, direction);
        if (direction == EditReplayDirection.Undo)
        {
            _undo.Push(pending.Entry);
        }
        else
        {
            _redo.Push(pending.Entry);
        }

        _pending = null;
    }

    private void EnsureNoPendingReplay()
    {
        if (_pending is not null)
        {
            throw new InvalidOperationException("Complete or restore the pending history replay first.");
        }
    }

    private PendingReplay RequirePending(EditOperation operation, EditReplayDirection direction)
    {
        ArgumentNullException.ThrowIfNull(operation);
        PendingReplay pending = _pending
            ?? throw new InvalidOperationException("There is no pending history replay.");
        if (!ReferenceEquals(pending.Entry.Operation, operation) || pending.Replay.Direction != direction)
        {
            throw new InvalidOperationException("The supplied operation is not the pending history replay.");
        }

        return pending;
    }

    private PendingReplay RequirePending(EditHistoryReplay replay, EditReplayDirection direction)
    {
        ArgumentNullException.ThrowIfNull(replay);
        PendingReplay pending = _pending
            ?? throw new InvalidOperationException("There is no pending history replay.");
        if (!ReferenceEquals(pending.Replay, replay) || replay.Direction != direction)
        {
            throw new InvalidOperationException("The supplied replay is not the pending history replay.");
        }

        return pending;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<RowReplayGuard> FreezeGuards(
        IEnumerable<RowReplayGuard> guards)
    {
        ArgumentNullException.ThrowIfNull(guards);
        RowReplayGuard[] copy = guards.ToArray();
        if (copy.Any(static guard => guard is null))
        {
            throw new ArgumentException("A history guard cannot be null.", nameof(guards));
        }

        return Array.AsReadOnly(copy);
    }

    private sealed record HistoryEntry(
        EditOperation Operation,
        IReadOnlyList<RowReplayGuard> Guards,
        Guid BeforeStateId,
        Guid AfterStateId);

    private sealed record PendingReplay(HistoryEntry Entry, EditHistoryReplay Replay);
}
