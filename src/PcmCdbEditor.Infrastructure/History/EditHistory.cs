using System.Globalization;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Internal;

namespace PcmCdbEditor.Infrastructure.History;

/// <summary>
/// Session-scoped, atomically persisted edit history. A replay is first persisted
/// as pending, then moved to the opposite stack only after its database transaction
/// succeeds. An interrupted pending replay is restored on load; its row guards make
/// retry fail safely if the original transaction actually committed.
/// </summary>
public sealed class EditHistory : IEditHistory
{
    private readonly object _gate = new();
    private readonly Stack<HistoryEntry> _undo = [];
    private readonly Stack<HistoryEntry> _redo = [];
    private readonly string? _snapshotPath;
    private PendingReplay? _pending;
    private Guid _currentStateId = Guid.NewGuid();
    private Guid _savedStateId;
    private bool _recoveredInterruptedReplay;

    public EditHistory(string? snapshotPath = null)
    {
        _snapshotPath = string.IsNullOrWhiteSpace(snapshotPath) ? null : Path.GetFullPath(snapshotPath);
        _savedStateId = _currentStateId;
        if (_snapshotPath is not null)
        {
            LoadSnapshot();
        }
    }

    public EditHistoryState State
    {
        get
        {
            lock (_gate)
            {
                return new EditHistoryState(
                    _undo.Count > 0,
                    _redo.Count > 0,
                    _undo.Count,
                    _redo.Count,
                    _currentStateId != _savedStateId,
                    _pending is not null,
                    _recoveredInterruptedReplay);
            }
        }
    }

    public void Record(EditOperation operation) => Record(operation, []);

    public void Record(EditOperation operation, IEnumerable<RowReplayGuard> undoGuards)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            EnsureNoPendingReplay();
            var entry = new HistoryEntry(
                operation,
                FreezeGuards(undoGuards),
                _currentStateId,
                Guid.NewGuid());
            _undo.Push(entry);
            _redo.Clear();
            _currentStateId = entry.AfterStateId;
            Persist();
        }
    }

    public EditOperation TakeUndo() => TakeUndoReplay().Operation;

    public EditOperation TakeRedo() => TakeRedoReplay().Operation;

    public EditHistoryReplay TakeUndoReplay()
    {
        lock (_gate)
        {
            return TakeReplay(_undo, EditReplayDirection.Undo);
        }
    }

    public EditHistoryReplay TakeRedoReplay()
    {
        lock (_gate)
        {
            return TakeReplay(_redo, EditReplayDirection.Redo);
        }
    }

    public void CompleteUndo(EditOperation operation)
    {
        lock (_gate)
        {
            PendingReplay pending = RequirePending(operation, EditReplayDirection.Undo);
            CompleteReplay(pending.Replay, EditReplayDirection.Undo, []);
        }
    }

    public void CompleteRedo(EditOperation operation)
    {
        lock (_gate)
        {
            PendingReplay pending = RequirePending(operation, EditReplayDirection.Redo);
            CompleteReplay(pending.Replay, EditReplayDirection.Redo, []);
        }
    }

    public void CompleteUndo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> redoGuards)
    {
        lock (_gate)
        {
            CompleteReplay(replay, EditReplayDirection.Undo, redoGuards);
        }
    }

    public void CompleteRedo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> undoGuards)
    {
        lock (_gate)
        {
            CompleteReplay(replay, EditReplayDirection.Redo, undoGuards);
        }
    }

    public void RestoreFailedUndo(EditOperation operation)
    {
        lock (_gate)
        {
            PendingReplay pending = RequirePending(operation, EditReplayDirection.Undo);
            RestoreReplay(pending.Replay, EditReplayDirection.Undo);
        }
    }

    public void RestoreFailedRedo(EditOperation operation)
    {
        lock (_gate)
        {
            PendingReplay pending = RequirePending(operation, EditReplayDirection.Redo);
            RestoreReplay(pending.Replay, EditReplayDirection.Redo);
        }
    }

    public void RestoreFailedUndo(EditHistoryReplay replay)
    {
        lock (_gate)
        {
            RestoreReplay(replay, EditReplayDirection.Undo);
        }
    }

    public void RestoreFailedRedo(EditHistoryReplay replay)
    {
        lock (_gate)
        {
            RestoreReplay(replay, EditReplayDirection.Redo);
        }
    }

    public void MarkSavedBaseline()
    {
        lock (_gate)
        {
            EnsureNoPendingReplay();
            _savedStateId = _currentStateId;
            Persist();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureNoPendingReplay();
            _undo.Clear();
            _redo.Clear();
            Persist();
        }
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
        Persist();
        return replay;
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
        Persist();
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
        Persist();
    }

    private void LoadSnapshot()
    {
        try
        {
            HistoryDocument document = AtomicJsonFile.ReadOrCreateAsync(
                    _snapshotPath!,
                    HistoryDocument.CreateEmpty,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (document.Version != HistoryDocument.CurrentVersion)
            {
                throw new InvalidDataException("The edit-history snapshot version is not supported.");
            }

            if (document.CurrentStateId == Guid.Empty || document.SavedStateId == Guid.Empty)
            {
                throw new InvalidDataException("The edit-history state markers are invalid.");
            }

            _currentStateId = document.CurrentStateId;
            _savedStateId = document.SavedStateId;
            RestoreStack(_undo, document.Undo);
            RestoreStack(_redo, document.Redo);
            if (document.Pending is not null)
            {
                HistoryEntry interrupted = document.Pending.Entry.ToEntry();
                if (document.Pending.Direction == EditReplayDirection.Undo)
                {
                    _undo.Push(interrupted);
                }
                else
                {
                    _redo.Push(interrupted);
                }

                _recoveredInterruptedReplay = true;
                Persist();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or FormatException)
        {
            _undo.Clear();
            _redo.Clear();
            _pending = null;
            _currentStateId = Guid.NewGuid();
            _savedStateId = _currentStateId;
            PreserveInvalidSnapshot();
        }
    }

    private static void RestoreStack(Stack<HistoryEntry> stack, IReadOnlyList<HistoryEntryEnvelope> topFirst)
    {
        for (var index = topFirst.Count - 1; index >= 0; index--)
        {
            stack.Push(topFirst[index].ToEntry());
        }
    }

    private void Persist()
    {
        if (_snapshotPath is null)
        {
            return;
        }

        var document = new HistoryDocument(
            HistoryDocument.CurrentVersion,
            _currentStateId,
            _savedStateId,
            _undo.Select(HistoryEntryEnvelope.From).ToArray(),
            _redo.Select(HistoryEntryEnvelope.From).ToArray(),
            _pending is null ? null : PendingEnvelope.From(_pending));
        AtomicJsonFile.WriteAsync(_snapshotPath, document, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private void PreserveInvalidSnapshot()
    {
        if (_snapshotPath is null || !File.Exists(_snapshotPath))
        {
            return;
        }

        var destination = string.Create(
            CultureInfo.InvariantCulture,
            $"{_snapshotPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        try
        {
            File.Move(_snapshotPath, destination);
        }
        catch (IOException)
        {
            // A safe empty in-memory history is still usable if preservation races another process.
        }
        catch (UnauthorizedAccessException)
        {
            // A safe empty in-memory history is still usable when the snapshot is read-only.
        }
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

    private sealed record HistoryDocument(
        int Version,
        Guid CurrentStateId,
        Guid SavedStateId,
        IReadOnlyList<HistoryEntryEnvelope> Undo,
        IReadOnlyList<HistoryEntryEnvelope> Redo,
        PendingEnvelope? Pending)
    {
        public const int CurrentVersion = 2;

        public static HistoryDocument CreateEmpty()
        {
            Guid initialState = Guid.NewGuid();
            return new HistoryDocument(CurrentVersion, initialState, initialState, [], [], null);
        }
    }

    private sealed record HistoryEntryEnvelope(
        OperationEnvelope Operation,
        IReadOnlyList<GuardEnvelope> Guards,
        Guid BeforeStateId,
        Guid AfterStateId)
    {
        public static HistoryEntryEnvelope From(HistoryEntry entry) => new(
            OperationEnvelope.From(entry.Operation),
            entry.Guards.Select(GuardEnvelope.From).ToArray(),
            entry.BeforeStateId,
            entry.AfterStateId);

        public HistoryEntry ToEntry()
        {
            if (BeforeStateId == Guid.Empty || AfterStateId == Guid.Empty)
            {
                throw new InvalidDataException("A persisted history entry has invalid state markers.");
            }

            return new HistoryEntry(
                Operation.ToOperation(),
                Array.AsReadOnly(Guards.Select(static guard => guard.ToGuard()).ToArray()),
                BeforeStateId,
                AfterStateId);
        }
    }

    private sealed record PendingEnvelope(EditReplayDirection Direction, HistoryEntryEnvelope Entry)
    {
        public static PendingEnvelope From(PendingReplay pending) => new(
            pending.Replay.Direction,
            HistoryEntryEnvelope.From(pending.Entry));
    }

    private sealed record GuardEnvelope(
        string TableName,
        IdentityEnvelope Identity,
        RowReplayExpectation Expectation,
        string? ExpectedRevision)
    {
        public static GuardEnvelope From(RowReplayGuard guard) => new(
            guard.TableName,
            IdentityEnvelope.From(guard.Identity),
            guard.Expectation,
            guard.ExpectedRevision?.Value);

        public RowReplayGuard ToGuard() => new(
            TableName,
            Identity.ToIdentity(),
            Expectation,
            ExpectedRevision is null ? null : new RowRevision(ExpectedRevision));
    }

    private sealed record OperationEnvelope(
        string Kind,
        Guid OperationId,
        string TableName,
        DateTimeOffset CreatedAtUtc,
        IdentityEnvelope? Identity,
        string? ColumnName,
        ValueEnvelope? OldValue,
        ValueEnvelope? NewValue,
        IReadOnlyDictionary<string, ValueEnvelope>? OldValues,
        IReadOnlyDictionary<string, ValueEnvelope>? NewValues,
        string? ExpectedRevision,
        RowEnvelope? Row,
        RowEnvelope? InsertedRow,
        MaintenanceToolKind? MaintenanceTool,
        string? Description,
        IReadOnlyList<MaintenanceChangeEnvelope>? MaintenanceChanges)
    {
        public static OperationEnvelope From(EditOperation operation) => operation switch
        {
            CellUpdateOperation cell => new OperationEnvelope(
                "cell-update", cell.OperationId, cell.TableName, cell.CreatedAtUtc,
                IdentityEnvelope.From(cell.Identity), cell.ColumnName,
                ValueEnvelope.From(cell.OldValue), ValueEnvelope.From(cell.NewValue),
                null, null, cell.ExpectedRevision.Value, null, null, null, null, null),
            RowUpdateOperation row => new OperationEnvelope(
                "row-update", row.OperationId, row.TableName, row.CreatedAtUtc,
                IdentityEnvelope.From(row.Identity), null, null, null,
                FreezeValues(row.OldValues), FreezeValues(row.NewValues),
                row.ExpectedRevision.Value, null, null, null, null, null),
            RowInsertionOperation insertion => new OperationEnvelope(
                "row-insertion", insertion.OperationId, insertion.TableName, insertion.CreatedAtUtc,
                insertion.AssignedIdentity is null ? null : IdentityEnvelope.From(insertion.AssignedIdentity),
                null, null, null, null, FreezeValues(insertion.Values), null, null,
                insertion.InsertedRow is null ? null : RowEnvelope.From(insertion.InsertedRow),
                null, null, null),
            RowDeletionOperation deletion => new OperationEnvelope(
                "row-deletion", deletion.OperationId, deletion.TableName, deletion.CreatedAtUtc,
                null, null, null, null, null, null, null, RowEnvelope.From(deletion.DeletedRow),
                null, null, null, null),
            MaintenanceEditOperation maintenance => new OperationEnvelope(
                "maintenance", maintenance.OperationId, maintenance.TableName, maintenance.CreatedAtUtc,
                null, null, null, null, null, null, null, null, null,
                maintenance.Tool, maintenance.Description,
                maintenance.Changes.Select(MaintenanceChangeEnvelope.From).ToArray()),
            _ => throw new InvalidDataException($"Unsupported edit operation type '{operation.GetType().Name}'.")
        };

        public EditOperation ToOperation() => Kind switch
        {
            "cell-update" => new CellUpdateOperation(
                OperationId, RequireTableName(), CreatedAtUtc, RequireIdentity(),
                ColumnName ?? throw Invalid("column name"),
                (OldValue ?? throw Invalid("old value")).ToValue(),
                (NewValue ?? throw Invalid("new value")).ToValue(),
                new RowRevision(ExpectedRevision ?? throw Invalid("expected revision"))),
            "row-update" => new RowUpdateOperation(
                OperationId, RequireTableName(), CreatedAtUtc, RequireIdentity(),
                ThawValues(OldValues ?? throw Invalid("old values")),
                ThawValues(NewValues ?? throw Invalid("new values")),
                new RowRevision(ExpectedRevision ?? throw Invalid("expected revision"))),
            "row-insertion" => new RowInsertionOperation(
                OperationId, RequireTableName(), CreatedAtUtc,
                ThawValues(NewValues ?? throw Invalid("inserted values")),
                Identity?.ToIdentity(),
                InsertedRow?.ToRow()),
            "row-deletion" => new RowDeletionOperation(
                OperationId, RequireTableName(), CreatedAtUtc,
                (Row ?? throw Invalid("deleted row")).ToRow()),
            "maintenance" => new MaintenanceEditOperation(
                OperationId,
                RequireTableName(),
                CreatedAtUtc,
                MaintenanceTool ?? throw Invalid("maintenance tool"),
                Description ?? throw Invalid("description"),
                (MaintenanceChanges ?? throw Invalid("maintenance changes"))
                    .Select(static change => change.ToChange())),
            _ => throw new InvalidDataException($"Unknown edit operation kind '{Kind}'.")
        };

        private string RequireTableName() => string.IsNullOrWhiteSpace(TableName)
            ? throw Invalid("table name")
            : TableName;

        private RowIdentity RequireIdentity() => (Identity ?? throw Invalid("identity")).ToIdentity();

        private static Dictionary<string, ValueEnvelope> FreezeValues(
            IReadOnlyDictionary<string, SqliteValue> values) =>
            values.ToDictionary(
                static pair => pair.Key,
                static pair => ValueEnvelope.From(pair.Value),
                StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<KeyValuePair<string, SqliteValue>> ThawValues(
            IReadOnlyDictionary<string, ValueEnvelope> values) =>
            values.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value.ToValue()));

        private static InvalidDataException Invalid(string field) =>
            new($"The persisted edit operation is missing its {field}.");
    }

    private sealed record MaintenanceChangeEnvelope(
        string TableName,
        IdentityEnvelope Identity,
        IReadOnlyDictionary<string, ValueEnvelope>? BeforeValues,
        IReadOnlyDictionary<string, ValueEnvelope>? AfterValues)
    {
        public static MaintenanceChangeEnvelope From(MaintenanceRowChange change) => new(
            change.TableName,
            IdentityEnvelope.From(change.Identity),
            change.BeforeValues is null ? null : FreezeValues(change.BeforeValues),
            change.AfterValues is null ? null : FreezeValues(change.AfterValues));

        public MaintenanceRowChange ToChange() => new(
            TableName,
            Identity.ToIdentity(),
            BeforeValues?.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value.ToValue())),
            AfterValues?.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value.ToValue())));

        private static Dictionary<string, ValueEnvelope> FreezeValues(
            IReadOnlyDictionary<string, SqliteValue> values) =>
            values.ToDictionary(
                static pair => pair.Key,
                static pair => ValueEnvelope.From(pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RowEnvelope(
        IdentityEnvelope? Identity,
        IReadOnlyDictionary<string, ValueEnvelope> Values)
    {
        public static RowEnvelope From(TypedRow row) => new(
            row.Identity is null ? null : IdentityEnvelope.From(row.Identity),
            row.Values.ToDictionary(
                static pair => pair.Key,
                static pair => ValueEnvelope.From(pair.Value),
                StringComparer.OrdinalIgnoreCase));

        public TypedRow ToRow() => new(
            Identity?.ToIdentity(),
            Values.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value.ToValue())));
    }

    private sealed record IdentityEnvelope(
        RowIdentityKind Kind,
        IReadOnlyList<IdentityComponentEnvelope> Components)
    {
        public static IdentityEnvelope From(RowIdentity identity) => new(
            identity.Kind,
            identity.Components.Select(static component => new IdentityComponentEnvelope(
                component.ColumnName,
                ValueEnvelope.From(component.Value))).ToArray());

        public RowIdentity ToIdentity()
        {
            if (Kind == RowIdentityKind.RowId)
            {
                if (Components.Count != 1 || Components[0].Value.Kind != SqliteValueKind.Integer)
                {
                    throw new InvalidDataException("A persisted rowid identity is invalid.");
                }

                return RowIdentity.FromRowId(Components[0].Value.IntegerValue);
            }

            if (Kind != RowIdentityKind.DeclaredPrimaryKey)
            {
                throw new InvalidDataException($"Unknown persisted identity kind '{Kind}'.");
            }

            return RowIdentity.FromPrimaryKey(Components.Select(static component =>
                new RowIdentityComponent(component.ColumnName, component.Value.ToValue())));
        }
    }

    private sealed record IdentityComponentEnvelope(string ColumnName, ValueEnvelope Value);

    private sealed record ValueEnvelope(
        SqliteValueKind Kind,
        long IntegerValue,
        double RealValue,
        string? TextValue,
        string? BlobBase64)
    {
        public static ValueEnvelope From(SqliteValue value) => new(
            value.Kind,
            value.IntegerValue,
            value.RealValue,
            value.TextValue,
            value.BlobBase64);

        public SqliteValue ToValue() => Kind switch
        {
            SqliteValueKind.Null => SqliteValue.Null,
            SqliteValueKind.Integer => SqliteValue.Integer(IntegerValue),
            SqliteValueKind.Real => SqliteValue.Real(RealValue),
            SqliteValueKind.Text => SqliteValue.Text(TextValue ?? throw new InvalidDataException(
                "A persisted text value has no text payload.")),
            SqliteValueKind.Blob => SqliteValue.Blob(Convert.FromBase64String(BlobBase64
                ?? throw new InvalidDataException("A persisted blob value has no payload."))),
            _ => throw new InvalidDataException($"Unknown persisted SQLite value kind '{Kind}'.")
        };
    }
}
