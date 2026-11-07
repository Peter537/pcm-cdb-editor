using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

/// <summary>
/// Persists the conservative recovery marker that must complete before a
/// working SQLite mutation is allowed to begin.
/// </summary>
public sealed class MutationWriteAhead(IWorkspaceService workspaceService)
{
    private readonly IWorkspaceService _workspaceService = workspaceService
        ?? throw new ArgumentNullException(nameof(workspaceService));

    public async Task<EditorSessionState> PrepareAsync(EditorSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool? uncertainty = session.MutationPending ? true : session.HistoryUncertain;
        EditorSessionState persisted = await _workspaceService.MarkDirtyAsync(
                session with { IsDirty = true, MutationPending = true, HistoryUncertain = uncertainty },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (persisted.SessionId != session.SessionId ||
            !persisted.WorkingSqlitePath.Equals(
                session.WorkingSqlitePath,
                StringComparison.OrdinalIgnoreCase) ||
            !persisted.IsDirty || !persisted.MutationPending ||
            persisted.HistoryUncertain != uncertainty)
        {
            throw new InvalidDataException(
                "The workspace did not persist the required dirty-session write-ahead state.");
        }

        return persisted;
    }

    // Only call after the history record or replay completion is durable, or
    // when the caller knows that no database mutation occurred.
    public async Task<EditorSessionState> CompleteAsync(EditorSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.MutationPending || !session.IsDirty)
        {
            throw new InvalidOperationException("There is no prepared mutation to complete.");
        }

        EditorSessionState persisted = await _workspaceService.MarkDirtyAsync(
                session with { MutationPending = false }, CancellationToken.None)
            .ConfigureAwait(false);
        if (persisted.SessionId != session.SessionId ||
            !persisted.WorkingSqlitePath.Equals(session.WorkingSqlitePath, StringComparison.OrdinalIgnoreCase) ||
            !persisted.IsDirty || persisted.MutationPending ||
            persisted.HistoryUncertain != session.HistoryUncertain)
        {
            throw new InvalidDataException("The workspace did not persist the mutation completion marker.");
        }

        return persisted;
    }
}
