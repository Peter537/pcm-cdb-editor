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
        EditorSessionState persisted = await _workspaceService.MarkDirtyAsync(
                session with { IsDirty = true },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (persisted.SessionId != session.SessionId ||
            !persisted.WorkingSqlitePath.Equals(
                session.WorkingSqlitePath,
                StringComparison.OrdinalIgnoreCase) ||
            !persisted.IsDirty)
        {
            throw new InvalidDataException(
                "The workspace did not persist the required dirty-session write-ahead state.");
        }

        return persisted;
    }
}
