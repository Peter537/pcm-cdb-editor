using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Workspace;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    public async Task SaveAsTreatsZeroBytePickerPlaceholderAsNewDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var backups = Path.Combine(root, "backups");
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllBytesAsync(destination, []).ConfigureAwait(false);
        var service = new WorkspaceService(
            new SyntheticConverter(),
            sessions,
            backups,
            TimeSpan.FromMinutes(1));
        try
        {
            EditorSessionState session = await service.OpenAsync(
                    new WorkspaceOpenRequest(source),
                    CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            WorkspaceSaveResult result = await service.SaveAsAsync(
                    session,
                    destination,
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsNull(result.BackupPath);
            Assert.IsFalse(result.Session.IsDirty);
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            Assert.IsFalse(Directory.Exists(backups));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task FailedSaveAsLeavesZeroBytePickerPlaceholderAndDirtySessionIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var backups = Path.Combine(root, "backups");
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllBytesAsync(destination, []).ConfigureAwait(false);
        var service = new WorkspaceService(
            new SyntheticConverter { FailImport = true },
            sessions,
            backups,
            TimeSpan.FromMinutes(1));
        try
        {
            EditorSessionState session = await service.OpenAsync(
                    new WorkspaceOpenRequest(source),
                    CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.SaveAsAsync(session, destination, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(0, new FileInfo(destination).Length);
            Assert.IsFalse(Directory.Exists(backups));
            IReadOnlyList<RecoverableSession> recoverable = await service.GetRecoverableSessionsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.HasCount(1, recoverable);
            Assert.AreEqual(session.SessionId, recoverable[0].Session.SessionId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OpenIsCopyFirstAndSaveCreatesBackupBeforeAtomicReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var backups = Path.Combine(root, "backups");
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "original-source").ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, "old-destination").ConfigureAwait(false);
        var converter = new SyntheticConverter();
        var service = new WorkspaceService(converter, sessions, backups, TimeSpan.FromMinutes(1));
        try
        {
            var session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual("original-source", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.AreEqual("original-source", await File.ReadAllTextAsync(session.WorkingCdbPath).ConfigureAwait(false));
            Assert.AreEqual(EditorSessionLifecycle.Ready, session.Lifecycle);

            var dirty = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(dirty.IsDirty);
            var persistedRecoverable = await service.GetRecoverableSessionsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.HasCount(1, persistedRecoverable);
            Assert.AreEqual(session.SessionId, persistedRecoverable[0].Session.SessionId);
            var result = await service.SaveAsAsync(dirty, destination, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(result.Session.IsDirty);
            Assert.IsNotNull(result.BackupPath);
            Assert.AreEqual("old-destination", await File.ReadAllTextAsync(result.BackupPath).ConfigureAwait(false));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            await service.CloseAsync(result.Session, discardDirtySession: false, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(session.SessionDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SaveFailurePreservesDestinationAndRecoverableDirtySession()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, "safe-old").ConfigureAwait(false);
        var converter = new SyntheticConverter { FailImport = true };
        var service = new WorkspaceService(
            converter,
            Path.Combine(root, "sessions"),
            Path.Combine(root, "backups"),
            TimeSpan.FromMinutes(1));
        try
        {
            var session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            var dirty = session with { IsDirty = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
            // Save persists the supplied dirty state before import, making crash recovery deterministic.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.SaveAsAsync(dirty, destination, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("safe-old", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            var recoverable = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, recoverable);
            Assert.AreEqual(session.SessionId, recoverable[0].Session.SessionId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MetadataFailureAfterCommitReportsCommittedSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, "safe-old").ConfigureAwait(false);
        FileStream? metadataLock = null;
        var converter = new SyntheticConverter
        {
            AfterImport = sessionMetadataPath =>
            {
                metadataLock = new FileStream(
                    sessionMetadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
        };
        var service = new WorkspaceService(
            converter,
            Path.Combine(root, "sessions"),
            Path.Combine(root, "backups"),
            TimeSpan.FromMinutes(1));
        try
        {
            EditorSessionState session = await service.OpenAsync(
                    new WorkspaceOpenRequest(source),
                    CancellationToken.None)
                .ConfigureAwait(false);
            converter.SessionMetadataPath = Path.Combine(session.SessionDirectory, "session.json");
            EditorSessionState dirty = await service.MarkDirtyAsync(session, CancellationToken.None)
                .ConfigureAwait(false);

            WorkspaceSaveCommitException exception = await Assert.ThrowsExactlyAsync<WorkspaceSaveCommitException>(
                    () => service.SaveAsAsync(dirty, destination, CancellationToken.None))
                .ConfigureAwait(false);

            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            Assert.IsFalse(exception.CommittedSave.Session.IsDirty);
            Assert.AreEqual(EditorSessionLifecycle.Ready, exception.CommittedSave.Session.Lifecycle);
            Assert.AreEqual(Path.GetFullPath(destination), exception.CommittedSave.Session.SaveTargetCdbPath);
            Assert.IsNotNull(exception.CommittedSave.BackupPath);
            Assert.AreEqual(
                "safe-old",
                await File.ReadAllTextAsync(exception.CommittedSave.BackupPath).ConfigureAwait(false));
        }
        finally
        {
            metadataLock?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CancellationBeforeCommitPreservesDestinationAndDirtyRecoveryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, "safe-old").ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var converter = new SyntheticConverter { CancelAfterImport = cancellation };
        var service = new WorkspaceService(
            converter,
            Path.Combine(root, "sessions"),
            Path.Combine(root, "backups"),
            TimeSpan.FromMinutes(1));
        try
        {
            var session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            var dirty = session with { IsDirty = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                service.SaveAsAsync(dirty, destination, cancellation.Token)).ConfigureAwait(false);
            Assert.AreEqual("safe-old", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            Assert.HasCount(1, await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task HistoryBaselineTransitionsPersistMatchingCrashRecoveryMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(
            new SyntheticConverter(),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "backups"),
            TimeSpan.FromMinutes(1));
        try
        {
            EditorSessionState session = await service.OpenAsync(
                    new WorkspaceOpenRequest(source),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var history = new EditHistory(Path.Combine(session.SessionDirectory, "edit-history.json"));
            history.Record(Operation("edited", 1));
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            EditHistoryReplay undoInitialEdit = history.TakeUndoReplay();
            history.CompleteUndo(undoInitialEdit, []);
            Assert.IsFalse(history.State.IsDirty);
            session = await service.PersistSavedBaselineAsync(session, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(session.IsDirty);
            Assert.IsEmpty(await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));

            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            EditHistoryReplay redoInitialEdit = history.TakeRedoReplay();
            history.CompleteRedo(redoInitialEdit, []);
            Assert.IsTrue(history.State.IsDirty);
            Assert.HasCount(1, await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));

            WorkspaceSaveResult saved = await service.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            session = saved.Session;
            history.MarkSavedBaseline();
            Assert.IsFalse(session.IsDirty);
            Assert.IsFalse(history.State.IsDirty);

            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            EditHistoryReplay undoSavedEdit = history.TakeUndoReplay();
            history.CompleteUndo(undoSavedEdit, []);
            Assert.IsTrue(history.State.IsDirty);
            Assert.HasCount(1, await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));

            EditHistoryReplay redoSavedEdit = history.TakeRedoReplay();
            history.CompleteRedo(redoSavedEdit, []);
            Assert.IsFalse(history.State.IsDirty);
            session = await service.PersistSavedBaselineAsync(session, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(session.IsDirty);
            Assert.IsEmpty(await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));

            await service.CloseAsync(session, discardDirtySession: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CellUpdateOperation Operation(string value, long revisionSeed) =>
        new(
            Guid.NewGuid(),
            "neutral",
            DateTimeOffset.UnixEpoch,
            RowIdentity.FromRowId(1),
            "value",
            SqliteValue.Text("old"),
            SqliteValue.Text(value),
            RowRevision.Compute([KeyValuePair.Create("seed", SqliteValue.Integer(revisionSeed))]));

    private sealed class SyntheticConverter : ICdbConverter
    {
        public bool FailImport { get; init; }

        public CancellationTokenSource? CancelAfterImport { get; init; }

        public Action<string>? AfterImport { get; init; }

        public string? SessionMetadataPath { get; set; }

        public async Task<ConversionResult> ExportToSqliteAsync(
            string workingCdbPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = timeout;
            var output = Path.ChangeExtension(workingCdbPath, ".sqlite");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = output,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE neutral(ID INTEGER PRIMARY KEY, value TEXT)";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new ConversionResult(output, Diagnostics());
        }

        public async Task<ConversionResult> ImportToCdbAsync(
            string workingSqlitePath,
            string temporaryCdbDestination,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = workingSqlitePath;
            _ = timeout;
            if (FailImport)
            {
                throw new InvalidOperationException("Synthetic import failure.");
            }

            await File.WriteAllTextAsync(temporaryCdbDestination, "converted-output", cancellationToken)
                .ConfigureAwait(false);
            CancelAfterImport?.Cancel();
            if (AfterImport is not null)
            {
                AfterImport(SessionMetadataPath
                    ?? throw new InvalidOperationException("A session metadata path is required."));
            }

            return new ConversionResult(temporaryCdbDestination, Diagnostics());
        }

        private static ConverterDiagnostics Diagnostics() =>
            new(0, string.Empty, string.Empty, TimeSpan.Zero);
    }
}
