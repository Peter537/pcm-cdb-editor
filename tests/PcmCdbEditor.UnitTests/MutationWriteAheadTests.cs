using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MutationWriteAheadTests
{
    [TestMethod]
    public async Task DirtyRecoveryIsPersistedNonCancellablyBeforeMutationStarts()
    {
        var workspace = new RecordingWorkspaceService();
        var writeAhead = new MutationWriteAhead(workspace);
        EditorSessionState session = CreateSession();
        var mutationStarted = false;

        EditorSessionState persisted = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
        mutationStarted = true;

        Assert.IsTrue(workspace.MarkDirtyCompleted);
        Assert.IsTrue(mutationStarted);
        Assert.IsFalse(workspace.ObservedToken.CanBeCanceled);
        Assert.IsTrue(persisted.IsDirty);
        Assert.AreEqual(session.SessionId, persisted.SessionId);
    }

    [TestMethod]
    public async Task PersistenceFaultPreventsTheMutationBoundaryFromCompleting()
    {
        var workspace = new RecordingWorkspaceService { FailMarkDirty = true };
        var writeAhead = new MutationWriteAhead(workspace);
        var mutationStarted = false;

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            _ = await writeAhead.PrepareAsync(CreateSession()).ConfigureAwait(false);
            mutationStarted = true;
        }).ConfigureAwait(false);

        Assert.IsFalse(mutationStarted);
        Assert.IsFalse(workspace.MarkDirtyCompleted);
    }

    [TestMethod]
    public async Task MismatchedOrCleanPersistenceResultIsRejected()
    {
        var workspace = new RecordingWorkspaceService { ReturnCleanSession = true };
        var writeAhead = new MutationWriteAhead(workspace);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            writeAhead.PrepareAsync(CreateSession())).ConfigureAwait(false);
    }

    private static EditorSessionState CreateSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", "write-ahead");
        return new EditorSessionState(
            Guid.NewGuid(),
            Path.Combine(root, "source.cdb"),
            Path.Combine(root, "source.cdb"),
            Path.Combine(root, "session"),
            Path.Combine(root, "session", "working.cdb"),
            Path.Combine(root, "session", "working.sqlite"),
            IsDirty: false,
            EditorSessionLifecycle.Ready,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            LastBackupPath: null);
    }

    private sealed class RecordingWorkspaceService : IWorkspaceService
    {
        public bool FailMarkDirty { get; init; }

        public bool ReturnCleanSession { get; init; }

        public bool MarkDirtyCompleted { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public Task<EditorSessionState> MarkDirtyAsync(
            EditorSessionState session,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            if (FailMarkDirty)
            {
                throw new IOException("Synthetic write-ahead failure.");
            }

            MarkDirtyCompleted = true;
            return Task.FromResult(session with { IsDirty = !ReturnCleanSession });
        }

        public Task<EditorSessionState> OpenAsync(
            WorkspaceOpenRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditorSessionState> RecoverAsync(
            Guid sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceSaveResult> SaveAsync(
            EditorSessionState session,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceSaveResult> SaveAsAsync(
            EditorSessionState session,
            string destinationCdbPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseAsync(
            EditorSessionState session,
            bool discardDirtySession,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecoverableSession>> GetRecoverableSessionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DiscardRecoverableSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CleanCompletedSessionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
