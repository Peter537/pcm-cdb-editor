using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MutationWriteAheadTests
{
    [TestMethod]
    public async Task PrepareWaitsForTheNonCancellableDirtyMarker()
    {
        var releasePersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new RecordingWorkspaceService { ReleasePersistence = releasePersistence.Task };
        var writeAhead = new MutationWriteAhead(workspace);
        EditorSessionState session = CreateSession();

        Task<EditorSessionState> preparing = writeAhead.PrepareAsync(session);
        try
        {
            await workspace.PersistenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(preparing.IsCompleted);
            Assert.IsNull(workspace.LastState);
            Assert.IsFalse(workspace.ObservedToken.CanBeCanceled);
        }
        finally
        {
            releasePersistence.TrySetResult();
        }

        EditorSessionState persisted = await preparing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreSame(workspace.LastState, persisted);
        Assert.IsTrue(persisted.IsDirty);
        Assert.IsTrue(persisted.MutationPending);
        Assert.AreEqual(session.SessionId, persisted.SessionId);
    }

    [TestMethod]
    public async Task PersistenceFaultPreventsTheMutationBoundaryFromCompleting()
    {
        var workspace = new RecordingWorkspaceService { FailMarkDirty = true };
        var writeAhead = new MutationWriteAhead(workspace);

        await Assert.ThrowsExactlyAsync<IOException>(() => writeAhead.PrepareAsync(CreateSession()))
            .ConfigureAwait(false);
        Assert.IsTrue(workspace.PersistenceStarted.Task.IsCompletedSuccessfully);
        Assert.IsNull(workspace.LastState);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MismatchedOrCleanPersistenceResultIsRejected(bool mismatched)
    {
        var workspace = new RecordingWorkspaceService
        {
            ReturnCleanSession = !mismatched,
            OverrideSessionId = mismatched ? Guid.NewGuid() : null
        };
        var writeAhead = new MutationWriteAhead(workspace);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            writeAhead.PrepareAsync(CreateSession())).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LaterMutationCannotEraseAnUnfinishedMutation()
    {
        var workspace = new RecordingWorkspaceService();
        var writeAhead = new MutationWriteAhead(workspace);
        EditorSessionState session = CreateSession() with { HistoryUncertain = false };
        EditorSessionState interrupted = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
        Assert.IsTrue(interrupted.MutationPending);

        EditorSessionState later = await writeAhead.PrepareAsync(interrupted).ConfigureAwait(false);
        later = await writeAhead.CompleteAsync(later).ConfigureAwait(false);

        Assert.IsFalse(later.MutationPending);
        Assert.IsTrue(later.HistoryUncertain == true);
        Assert.IsTrue(later.IsDirty);
        Assert.IsFalse(workspace.ObservedToken.CanBeCanceled);
    }

    [TestMethod]
    public async Task ACommittedMutationKeepsItsMarkerUntilHistoryIsDurable()
    {
        var workspace = new RecordingWorkspaceService();
        var writeAhead = new MutationWriteAhead(workspace);
        EditorSessionState prepared = await writeAhead.PrepareAsync(CreateSession()).ConfigureAwait(false);
        using var releaseHistory = new ManualResetEventSlim();
        var enteredHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EditorSessionState> completing = writeAhead.CompleteCommittedAsync(prepared, () =>
        {
            enteredHistory.SetResult();
            if (!releaseHistory.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release history persistence.");
            }
        });
        try
        {
            await enteredHistory.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsTrue(workspace.LastState!.MutationPending);
            Assert.IsFalse(completing.IsCompleted);
        }
        finally
        {
            releaseHistory.Set();
        }
        Assert.IsFalse((await completing.ConfigureAwait(false)).MutationPending);

        prepared = await writeAhead.PrepareAsync(CreateSession()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<IOException>(() => writeAhead.CompleteCommittedAsync(prepared,
            () => throw new IOException("Injected history write failure."))).ConfigureAwait(false);
        Assert.IsTrue(workspace.LastState!.MutationPending);
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

        public Guid? OverrideSessionId { get; init; }

        public Task ReleasePersistence { get; init; } = Task.CompletedTask;

        public TaskCompletionSource PersistenceStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public EditorSessionState? LastState { get; private set; }

        public async Task<EditorSessionState> MarkDirtyAsync(
            EditorSessionState session,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            PersistenceStarted.TrySetResult();
            await ReleasePersistence.ConfigureAwait(false);
            if (FailMarkDirty)
            {
                throw new IOException("Synthetic write-ahead failure.");
            }

            LastState = session with { IsDirty = !ReturnCleanSession, SessionId = OverrideSessionId ?? session.SessionId };
            return LastState;
        }

        public Task<EditorSessionState> OpenAsync(
            WorkspaceOpenRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditorSessionState> RecoverAsync(
            Guid sessionId,
            CancellationToken cancellationToken,
            DateTimeOffset? expectedUpdatedAtUtc = null) => throw new NotSupportedException();

        public Task<SaveDestinationSnapshot> InspectSaveDestinationAsync(
            string path,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceSaveResult> SaveAsync(
            EditorSessionState session,
            CancellationToken cancellationToken,
            SaveDestinationSnapshot? confirmedDestination = null) => throw new NotSupportedException();

        public Task<WorkspaceSaveResult> SaveAsAsync(
            EditorSessionState session,
            string destinationCdbPath,
            CancellationToken cancellationToken,
            SaveDestinationSnapshot? confirmedDestination = null) => throw new NotSupportedException();

        public Task<EditorSessionState> PersistSavedBaselineAsync(
            EditorSessionState session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceCloseResult> CloseAsync(
            EditorSessionState session,
            bool discardDirtySession,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReleaseSession(Guid sessionId) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecoverableSession>> GetRecoverableSessionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceCloseResult> DiscardRecoverableSessionAsync(
            Guid sessionId,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CleanCompletedSessionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
