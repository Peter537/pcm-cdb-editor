namespace PcmCdbEditor.Domain;

public abstract record EditOperation(Guid OperationId, string TableName, DateTimeOffset CreatedAtUtc);

public sealed record CellUpdateOperation(
    Guid OperationId,
    string TableName,
    DateTimeOffset CreatedAtUtc,
    RowIdentity Identity,
    string ColumnName,
    SqliteValue OldValue,
    SqliteValue NewValue,
    RowRevision ExpectedRevision) : EditOperation(OperationId, TableName, CreatedAtUtc);

public sealed record RowUpdateOperation : EditOperation
{
    public RowUpdateOperation(
        Guid operationId,
        string tableName,
        DateTimeOffset createdAtUtc,
        RowIdentity identity,
        IEnumerable<KeyValuePair<string, SqliteValue>> oldValues,
        IEnumerable<KeyValuePair<string, SqliteValue>> newValues,
        RowRevision expectedRevision)
        : base(operationId, tableName, createdAtUtc)
    {
        Identity = identity;
        OldValues = ModelCollections.FreezeDictionary(oldValues);
        NewValues = ModelCollections.FreezeDictionary(newValues);
        ExpectedRevision = expectedRevision;
    }

    public RowIdentity Identity { get; }

    public IReadOnlyDictionary<string, SqliteValue> OldValues { get; }

    public IReadOnlyDictionary<string, SqliteValue> NewValues { get; }

    public RowRevision ExpectedRevision { get; }
}

public sealed record RowInsertionOperation : EditOperation
{
    public RowInsertionOperation(
        Guid operationId,
        string tableName,
        DateTimeOffset createdAtUtc,
        IEnumerable<KeyValuePair<string, SqliteValue>> values,
        RowIdentity? assignedIdentity = null,
        TypedRow? insertedRow = null)
        : base(operationId, tableName, createdAtUtc)
    {
        Values = ModelCollections.FreezeDictionary(values);
        if (insertedRow is not null && insertedRow.Identity is null)
        {
            throw new ArgumentException("An inserted-row snapshot must have a verified database identity.", nameof(insertedRow));
        }

        if (assignedIdentity is not null
            && insertedRow?.Identity is not null
            && !assignedIdentity.Equals(insertedRow.Identity))
        {
            throw new ArgumentException("The assigned identity and inserted-row snapshot identity must match.", nameof(insertedRow));
        }

        AssignedIdentity = assignedIdentity ?? insertedRow?.Identity;
        InsertedRow = insertedRow;
    }

    public IReadOnlyDictionary<string, SqliteValue> Values { get; }

    public RowIdentity? AssignedIdentity { get; }

    /// <summary>
    /// Gets the complete typed row read back after insertion. History records use this
    /// snapshot to preserve defaults, generated values, NULLs, and BLOBs during replay.
    /// </summary>
    public TypedRow? InsertedRow { get; }
}

public sealed record RowDeletionOperation : EditOperation
{
    public RowDeletionOperation(
        Guid operationId,
        string tableName,
        DateTimeOffset createdAtUtc,
        TypedRow deletedRow)
        : base(operationId, tableName, createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(deletedRow);
        if (deletedRow.Identity is null)
        {
            throw new ArgumentException("A deleted row must have a verified database identity.", nameof(deletedRow));
        }

        DeletedRow = deletedRow;
    }

    public TypedRow DeletedRow { get; }
}

public sealed record EditResult(int AffectedRows, TypedRow? CurrentRow, string? Message = null);

public enum EditReplayDirection
{
    Undo,
    Redo
}

public enum RowReplayExpectation
{
    PresentWithRevision,
    Absent
}

/// <summary>
/// Describes the exact database state that must still exist before a history
/// replay begins. Implementations must validate these guards inside the same
/// transaction that applies the replay.
/// </summary>
public sealed record RowReplayGuard
{
    public RowReplayGuard(
        string tableName,
        RowIdentity identity,
        RowReplayExpectation expectation,
        RowRevision? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A replay guard requires a table name.", nameof(tableName));
        }

        ArgumentNullException.ThrowIfNull(identity);
        if (expectation == RowReplayExpectation.PresentWithRevision && expectedRevision is null)
        {
            throw new ArgumentException("A present-row guard requires an expected revision.", nameof(expectedRevision));
        }

        if (expectation == RowReplayExpectation.Absent && expectedRevision is not null)
        {
            throw new ArgumentException("An absent-row guard cannot carry a revision.", nameof(expectedRevision));
        }

        TableName = tableName;
        Identity = identity;
        Expectation = expectation;
        ExpectedRevision = expectedRevision;
    }

    public string TableName { get; }

    public RowIdentity Identity { get; }

    public RowReplayExpectation Expectation { get; }

    public RowRevision? ExpectedRevision { get; }

    public static RowReplayGuard Present(string tableName, TypedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RowReplayGuard(
            tableName,
            row.Identity ?? throw new ArgumentException(
                "A guarded row must have a verified database identity.",
                nameof(row)),
            RowReplayExpectation.PresentWithRevision,
            row.Revision);
    }

    public static RowReplayGuard Absent(string tableName, RowIdentity identity) =>
        new(tableName, identity, RowReplayExpectation.Absent);
}

/// <summary>
/// A history entry checked out for one undo or redo attempt. The guard list is
/// the persisted precondition for that exact direction.
/// </summary>
public sealed class EditHistoryReplay
{
    public EditHistoryReplay(
        EditOperation operation,
        EditReplayDirection direction,
        IEnumerable<RowReplayGuard>? guards)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Direction = direction;
        Guards = ModelCollections.Freeze(guards);
    }

    public EditOperation Operation { get; }

    public EditReplayDirection Direction { get; }

    public IReadOnlyList<RowReplayGuard> Guards { get; }
}

/// <summary>
/// One row transition within an atomic maintenance command. A null before-value
/// map represents insertion; a null after-value map represents deletion. Update
/// maps contain only the columns owned by the maintenance preset, while deletion
/// maps contain the complete typed row needed for lossless restoration.
/// </summary>
public sealed record MaintenanceRowChange
{
    public MaintenanceRowChange(
        string tableName,
        RowIdentity identity,
        IEnumerable<KeyValuePair<string, SqliteValue>>? beforeValues,
        IEnumerable<KeyValuePair<string, SqliteValue>>? afterValues)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A maintenance row change requires a table name.", nameof(tableName));
        }

        ArgumentNullException.ThrowIfNull(identity);
        if (beforeValues is null && afterValues is null)
        {
            throw new ArgumentException("A maintenance row change requires before or after values.", nameof(beforeValues));
        }

        var frozenBefore = beforeValues is null ? null : ModelCollections.FreezeDictionary(beforeValues);
        var frozenAfter = afterValues is null ? null : ModelCollections.FreezeDictionary(afterValues);
        if (frozenBefore is not null
            && frozenAfter is not null
            && !frozenBefore.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                frozenAfter.Keys.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Maintenance update snapshots must contain the same columns.", nameof(afterValues));
        }

        if (frozenBefore is not null && frozenAfter is not null && frozenBefore.Count == 0)
        {
            throw new ArgumentException("A maintenance update requires at least one column.", nameof(beforeValues));
        }

        TableName = tableName;
        Identity = identity;
        BeforeValues = frozenBefore;
        AfterValues = frozenAfter;
    }

    public MaintenanceRowChange(string tableName, TypedRow? beforeRow, TypedRow? afterRow)
        : this(
            tableName,
            beforeRow?.Identity
                ?? afterRow?.Identity
                ?? throw new ArgumentException(
                    "Maintenance rows must have verified database identities.",
                    nameof(beforeRow)),
            beforeRow?.Values,
            afterRow?.Values)
    {
        if (beforeRow is not null && beforeRow.Identity is null)
        {
            throw new ArgumentException("The before-row must have a verified database identity.", nameof(beforeRow));
        }

        if (afterRow is not null && afterRow.Identity is null)
        {
            throw new ArgumentException("The after-row must have a verified database identity.", nameof(afterRow));
        }

        if (beforeRow?.Identity is not null
            && afterRow?.Identity is not null
            && !beforeRow.Identity.Equals(afterRow.Identity))
        {
            throw new ArgumentException("A maintenance row change cannot change the stable row identity.", nameof(afterRow));
        }
    }

    public string TableName { get; }

    public RowIdentity Identity { get; }

    public IReadOnlyDictionary<string, SqliteValue>? BeforeValues { get; }

    public IReadOnlyDictionary<string, SqliteValue>? AfterValues { get; }
}

/// <summary>
/// A thin, data-oriented maintenance command. The ordered typed snapshots are
/// sufficient for one atomic undo/redo without embedding PCM game logic in the
/// history subsystem.
/// </summary>
public sealed record MaintenanceEditOperation : EditOperation
{
    public MaintenanceEditOperation(
        Guid operationId,
        string tableName,
        DateTimeOffset createdAtUtc,
        MaintenanceToolKind tool,
        string description,
        IEnumerable<MaintenanceRowChange> changes)
        : base(operationId, tableName, createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A maintenance history description is required.", nameof(description));
        }

        Tool = tool;
        Description = description;
        Changes = ModelCollections.Freeze(changes);
        if (Changes.Count == 0)
        {
            throw new ArgumentException("A maintenance command requires at least one row change.", nameof(changes));
        }

        if (Changes
            .Select(static change => $"{change.TableName}\u001f{change.Identity}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != Changes.Count)
        {
            throw new ArgumentException("A maintenance command cannot change the same row more than once.", nameof(changes));
        }
    }

    public MaintenanceToolKind Tool { get; }

    public string Description { get; }

    public IReadOnlyList<MaintenanceRowChange> Changes { get; }
}

public sealed record EditReplayResult(int AffectedRows, IReadOnlyList<RowReplayGuard> OppositeGuards);

public sealed record EditHistoryState(
    bool CanUndo,
    bool CanRedo,
    int UndoCount,
    int RedoCount,
    bool IsDirty = false,
    bool HasPendingReplay = false,
    bool RecoveredInterruptedReplay = false);
