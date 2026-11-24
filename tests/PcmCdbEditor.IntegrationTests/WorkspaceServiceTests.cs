using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Sqlite;
using PcmCdbEditor.Infrastructure.Workspace;

namespace PcmCdbEditor.IntegrationTests;

[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SavePreservesActualDisplacedBytesAndReportsChangeDuringAtomicHandoff(bool archiveUnavailable)
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorSave-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string external = Path.Combine(root, "external.cdb");
        string backups = Path.Combine(root, "backups");
        try
        {
            await File.WriteAllTextAsync(source, "initial").ConfigureAwait(false);
            await File.WriteAllTextAsync(external, "outside").ConfigureAwait(false);
            if (archiveUnavailable)
            {
                await File.WriteAllTextAsync(backups, "blocks the archive directory").ConfigureAwait(false);
            }

            using var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), backups)
            {
                ReplaceDestination = (staged, destination, displaced) =>
                {
                    File.Move(external, destination, overwrite: true);
                    File.Replace(staged, destination, displaced, ignoreMetadataErrors: true);
                }
            };
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            WorkspaceSaveResult result = await service.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.DestinationChangedAtCommit);
            Assert.IsFalse(result.Session.IsDirty);
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.IsNotNull(result.BackupPath);
            Assert.AreEqual(result.BackupPath, result.Session.LastBackupPath);
            Assert.AreEqual(archiveUnavailable ? root : backups, Path.GetDirectoryName(result.BackupPath));
            Assert.AreEqual("outside", await File.ReadAllTextAsync(result.BackupPath).ConfigureAwait(false));
            Assert.AreEqual(
                await service.InspectSaveDestinationAsync(source, CancellationToken.None).ConfigureAwait(false),
                result.Session.SaveDestinationBaseline);
            await service.CloseAsync(result.Session, discardDirtySession: false, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(result.BackupPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncompleteReplacementPreservesStageAndOnlyRestoresToVacantDestination(bool occupied)
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorHandoff-").FullName;
        string source = Path.Combine(root, "original.cdb");
        try
        {
            await File.WriteAllTextAsync(source, "original");
            using var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"),
                Path.Combine(root, "backups"))
            {
                ReplaceDestination = (_, target, backup) =>
                {
                    File.Move(target, backup);
                    if (occupied)
                    {
                        File.WriteAllText(target, "newer");
                    }
                    throw new IOException("Injected incomplete Windows handoff.");
                }
            };
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None);
            var failure = await Assert.ThrowsExactlyAsync<WorkspaceSaveHandoffException>(
                () => service.SaveAsync(session, CancellationToken.None));

            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(failure.Stage));
            Assert.AreEqual(occupied ? "newer" : "original", await File.ReadAllTextAsync(source));
            if (occupied)
            {
                Assert.IsNotNull(failure.Backup);
                Assert.AreEqual("original", await File.ReadAllTextAsync(failure.Backup));
            }
            else
            {
                Assert.IsNull(failure.Backup);
            }

            service.ReleaseSession(session.SessionId);
            EditorSessionState recovered = await service.RecoverAsync(session.SessionId, CancellationToken.None);
            Assert.IsTrue(recovered.IsDirty);
            Assert.IsTrue(recovered.HistoryUncertain);
            Assert.IsFalse(File.Exists(failure.Stage));
            if (occupied)
            {
                Assert.AreEqual(failure.Backup, recovered.LastBackupPath);
                Assert.IsTrue(File.Exists(failure.Backup));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecoveryListsAndRollsBackAHotJournal()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorJournal-").FullName;
        string source = Path.Combine(root, "original.cdb");
        try
        {
            await File.WriteAllTextAsync(source, "original");
            using var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"),
                Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None);
            session = await service.MarkDirtyAsync(session, CancellationToken.None);
            string database = session.WorkingSqlitePath;
            byte[] interruptedDatabase;
            byte[] hotJournal;
            using (var connection = SqliteSupport.CreateConnection(database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode = DELETE;
                    PRAGMA cache_size = 4;
                    PRAGMA cache_spill = ON;
                    CREATE TABLE audit_recovery(id INTEGER PRIMARY KEY, value TEXT);
                    WITH RECURSIVE ids(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM ids WHERE x<100)
                    INSERT INTO audit_recovery SELECT x, printf('%04096d', 1) FROM ids;
                    """;
                command.ExecuteNonQuery();

                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "UPDATE audit_recovery SET value = printf('%04096d', 2)";
                command.ExecuteNonQuery();
                // Copy the on-disk state at a point where cache spill has written uncommitted pages.
                interruptedDatabase = ReadShared(database);
                hotJournal = ReadShared(database + "-journal");
                Assert.AreNotEqual(0, hotJournal[0], "The captured rollback journal is not hot.");
            }

            File.WriteAllBytes(database, interruptedDatabase);
            File.WriteAllBytes(database + "-journal", hotJournal);
            service.ReleaseSession(session.SessionId);
            var listing = await service.GetRecoverableSessionsAsync(CancellationToken.None);
            Assert.IsTrue(Assert.ContainsSingle(listing).RequiresJournalRecovery);
            Assert.IsTrue(File.Exists(database + "-journal"), "Listing must leave recovery to explicit resume.");
            var recovered = await service.RecoverAsync(session.SessionId, CancellationToken.None, session.UpdatedAtUtc);
            using var check = SqliteSupport.CreateConnection(recovered.WorkingSqlitePath, SqliteOpenMode.ReadOnly);
            check.Open();
            using var query = check.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM audit_recovery WHERE substr(value,-1)='1'";
            Assert.AreEqual(100L, query.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static byte[] ReadShared(string path)
        {
            using var sourceStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = new MemoryStream();
            sourceStream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    [TestMethod]
    public async Task CleanupFailureReturnsClosedResultAndCanBeRetried()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorClose-").FullName;
        string source = Path.Combine(root, "original.cdb");
        string sessions = Path.Combine(root, "sessions");
        string backups = Path.Combine(root, "backups");
        try
        {
            await File.WriteAllTextAsync(source, "original");
            using var service = new WorkspaceService(new SyntheticConverter(), sessions, backups)
            {
                DeleteCompletedDirectory = _ => throw new IOException("Injected cleanup failure.")
            };
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None);
            WorkspaceCloseResult result = await service.CloseAsync(session, false, CancellationToken.None);
            Assert.IsNotNull(result.RemainingDirectory);
            Assert.IsTrue(Directory.Exists(result.RemainingDirectory));
            Assert.IsFalse(Directory.Exists(session.SessionDirectory));

            using var cleanup = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            string unrelated = Path.Combine(sessions, $".closed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(unrelated);
            await cleanup.CleanCompletedSessionsAsync(CancellationToken.None);
            Assert.IsFalse(Directory.Exists(result.RemainingDirectory));
            Assert.IsTrue(Directory.Exists(unrelated));
            Assert.AreEqual("original", await File.ReadAllTextAsync(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CleanupReclaimsVerifiedReadySessionAndOnlyItsTrackedStages()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorStages-").FullName;
        string source = Path.Combine(root, "original.cdb");
        string sessions = Path.Combine(root, "sessions");
        try
        {
            await File.WriteAllTextAsync(source, "original");
            using var service = new WorkspaceService(new SyntheticConverter(), sessions, Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None);
            var attempt = new WorkspaceSaveAttempt(Guid.NewGuid(), source, root);
            string stage = Path.Combine(root, $".original.{attempt.Id:N}.sqlite");
            string sidecar = stage + "-journal";
            string backup = Path.Combine(root, $"original-backup-{attempt.Id:N}.cdb");
            string unrelated = Path.Combine(root, $".original.{Guid.NewGuid():N}.sqlite");
            foreach (string path in new[] { stage, sidecar, backup, unrelated })
            {
                await File.WriteAllTextAsync(path, "fixture");
            }
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(session with { PendingSaveAttempt = attempt }));

            await service.CleanCompletedSessionsAsync(CancellationToken.None);
            Assert.IsTrue(File.Exists(stage), "An active owner retains its stage.");
            service.ReleaseSession(session.SessionId);
            await service.CleanCompletedSessionsAsync(CancellationToken.None);

            Assert.IsFalse(Directory.Exists(session.SessionDirectory));
            Assert.IsFalse(File.Exists(stage));
            Assert.IsFalse(File.Exists(sidecar));
            Assert.IsTrue(File.Exists(backup));
            Assert.IsTrue(File.Exists(unrelated));
            Assert.AreEqual("original", await File.ReadAllTextAsync(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LockedStageSurvivesSubsequentMetadataWritesAndDoesNotPreventResume()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorResume-").FullName;
        string source = Path.Combine(root, "original.cdb");
        string sessions = Path.Combine(root, "sessions");
        try
        {
            await File.WriteAllTextAsync(source, "original");
            using var service = new WorkspaceService(new SyntheticConverter(), sessions, Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None);
            var attempt = new WorkspaceSaveAttempt(Guid.NewGuid(), source, root);
            string stage = Path.Combine(root, $".original.{attempt.Id:N}.sqlite");
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(session with { PendingSaveAttempt = attempt }));
            EditorSessionState updated = await service.MarkDirtyAsync(session, CancellationToken.None);
            Assert.AreEqual(attempt, updated.PendingSaveAttempt);
            service.ReleaseSession(session.SessionId);

            string? remainingDirectory;
            string newDestination = Path.Combine(root, "saved-as.cdb");
            using (new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                EditorSessionState resumed = await service.RecoverAsync(session.SessionId, CancellationToken.None);
                var deferredAttempt = attempt with { CleanupNeedsEvidence = true };
                Assert.AreEqual(deferredAttempt, resumed.PendingSaveAttempt);
                Assert.IsTrue(File.Exists(resumed.WorkingSqlitePath));

                WorkspaceSaveResult saved = await service.SaveAsAsync(resumed, newDestination, CancellationToken.None);
                Assert.AreEqual(deferredAttempt, Assert.ContainsSingle(saved.Session.DeferredSaveAttempts!));
                Assert.IsFalse(saved.Session.IsDirty);
                Assert.IsTrue(File.Exists(newDestination));

                WorkspaceCloseResult closed = await service.CloseAsync(saved.Session, false, CancellationToken.None);
                remainingDirectory = closed.RemainingDirectory;
                Assert.IsNotNull(remainingDirectory);
                Assert.IsFalse(Directory.Exists(resumed.SessionDirectory));
                Assert.IsTrue(Directory.Exists(remainingDirectory));
            }

            await service.CleanCompletedSessionsAsync(CancellationToken.None);
            Assert.IsFalse(Directory.Exists(remainingDirectory));
            Assert.IsFalse(File.Exists(stage));
            Assert.IsTrue(File.Exists(newDestination));
            Assert.AreEqual("original", await File.ReadAllTextAsync(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnavailableSaveFolderRetainsCleanupThroughResumeAndClose()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorOfflineStage-").FullName;
        string source = Path.Combine(root, "original.cdb");
        string staging = Path.Combine(root, "destination-folder");
        string unavailableStaging = Path.Combine(root, "unavailable-folder");
        try
        {
            await File.WriteAllTextAsync(source, "original").ConfigureAwait(false);
            Directory.CreateDirectory(staging);
            using var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            var attempt = new WorkspaceSaveAttempt(Guid.NewGuid(), Path.Combine(staging, "export.cdb"), staging);
            string stage = Path.Combine(staging, $".export.{attempt.Id:N}.sqlite");
            string unrelated = Path.Combine(staging, "keep.sqlite");
            await File.WriteAllTextAsync(stage, "recorded stage").ConfigureAwait(false);
            await File.WriteAllTextAsync(unrelated, "unrelated file").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(session with
                {
                    Lifecycle = EditorSessionLifecycle.Saving,
                    PendingSaveAttempt = attempt
                })).ConfigureAwait(false);
            service.ReleaseSession(session.SessionId);
            Directory.Move(staging, unavailableStaging);

            EditorSessionState resumed = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(attempt.Id, resumed.PendingSaveAttempt?.Id);
            WorkspaceSaveAttempt? tracked = (await ReadSessionMetadataAsync(resumed).ConfigureAwait(false)).PendingSaveAttempt;
            Assert.AreEqual(attempt.Id, tracked?.Id);
            Assert.IsTrue(tracked?.CleanupNeedsEvidence == true);
            Assert.IsTrue(File.Exists(resumed.WorkingSqlitePath));
            Directory.CreateDirectory(staging);
            WorkspaceCloseResult closed = await service.CloseAsync(resumed, true, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(closed.RemainingDirectory);

            Directory.Delete(staging);
            Directory.Move(unavailableStaging, staging);
            await service.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(closed.RemainingDirectory));
            Assert.IsFalse(File.Exists(stage));
            Assert.AreEqual("unrelated file", await File.ReadAllTextAsync(unrelated).ConfigureAwait(false));
            Assert.AreEqual("original", await File.ReadAllTextAsync(source).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveFailureRetainsTheAttemptWhenItsStagingFolderDisappears()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorLostSaveFolder-").FullName;
        string source = Path.Combine(root, "original.cdb");
        string staging = Path.Combine(root, "destination-folder");
        string unavailableStaging = Path.Combine(root, "unavailable-folder");
        string destination = Path.Combine(staging, "export.cdb");
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(source, "original").ConfigureAwait(false);
            await File.WriteAllTextAsync(destination, "previous destination").ConfigureAwait(false);
            bool hideFolderAfterImport = true;
            var converter = new SyntheticConverter
            {
                AfterImport = _ =>
                {
                    if (hideFolderAfterImport)
                    {
                        Directory.Move(staging, unavailableStaging);
                    }
                }
            };
            using var service = new WorkspaceService(converter, Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            converter.SessionMetadataPath = Path.Combine(session.SessionDirectory, "session.json");

            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                service.SaveAsAsync(session, destination, CancellationToken.None)).ConfigureAwait(false);
            WorkspaceSaveAttempt? attempt = (await ReadSessionMetadataAsync(session).ConfigureAwait(false)).PendingSaveAttempt;
            Assert.IsNotNull(attempt);
            Assert.AreEqual(destination, attempt.DestinationPath);
            Assert.IsNotNull(attempt.StagingDirectoryIdentity);

            hideFolderAfterImport = false;
            WorkspaceSaveResult retry = await service.SaveAsAsync(session, destination, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(attempt, Assert.ContainsSingle(retry.Session.DeferredSaveAttempts!));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            string replacement = Path.Combine(root, "replacement-folder");
            Directory.Move(staging, replacement);
            Directory.Move(unavailableStaging, staging);
            WorkspaceCloseResult closed = await service.CloseAsync(retry.Session, false, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(closed.RemainingDirectory);
            Assert.IsFalse(File.Exists(Path.Combine(staging, $".export.{attempt.Id:N}.cdb")));
            Assert.AreEqual("previous destination", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(Path.Combine(replacement, "export.cdb")).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavePathsCannotPointIntoManagedSessions()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorSave-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string sessions = Path.Combine(root, "sessions");
        try
        {
            await File.WriteAllTextAsync(source, "initial").ConfigureAwait(false);
            using var service = new WorkspaceService(new SyntheticConverter(), sessions, Path.Combine(root, "backups"));
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            string destination = Path.Combine(session.SessionDirectory, "nested", "saved.cdb");

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.InspectSaveDestinationAsync(destination, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.SaveAsAsync(session, destination, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                service.SaveAsync(session with { SaveTargetCdbPath = destination }, CancellationToken.None)).ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(destination)));
            Assert.AreEqual("initial", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            await service.CloseAsync(session, discardDirtySession: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveRefusesChangedBytesAndOnlyOverwritesTheMostRecentlyConfirmedVersion()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorSave-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string backups = Path.Combine(root, "backups");
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), backups);
        try
        {
            await File.WriteAllTextAsync(source, "initial").ConfigureAwait(false);
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(
                await service.InspectSaveDestinationAsync(source, CancellationToken.None).ConfigureAwait(false),
                session.SaveDestinationBaseline);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            await File.WriteAllTextAsync(source, "outside").ConfigureAwait(false);
            SaveDestinationConflictException first = await Assert.ThrowsExactlyAsync<SaveDestinationConflictException>(() =>
                service.SaveAsync(session, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(SaveDestinationConflictReason.Changed, first.Reason);
            Assert.AreEqual(session.SaveDestinationBaseline!.Length, first.ObservedDestination.Length);
            Assert.AreEqual("outside", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.IsFalse(Directory.Exists(backups));

            await File.WriteAllTextAsync(source, "another").ConfigureAwait(false);
            SaveDestinationConflictException second = await Assert.ThrowsExactlyAsync<SaveDestinationConflictException>(() =>
                service.SaveAsync(session, CancellationToken.None, first.ObservedDestination)).ConfigureAwait(false);
            Assert.AreEqual(SaveDestinationConflictReason.Changed, second.Reason);
            Assert.AreNotEqual(first.ObservedDestination, second.ObservedDestination);
            Assert.AreEqual("another", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.IsTrue((await ReadSessionMetadataAsync(session).ConfigureAwait(false)).IsDirty);

            WorkspaceSaveResult saved = await service.SaveAsync(session, CancellationToken.None, second.ObservedDestination)
                .ConfigureAwait(false);
            Assert.IsFalse(saved.DestinationChangedAtCommit);
            Assert.AreEqual("another", await File.ReadAllTextAsync(saved.BackupPath!).ConfigureAwait(false));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.AreEqual(
                await service.InspectSaveDestinationAsync(source, CancellationToken.None).ConfigureAwait(false),
                saved.Session.SaveDestinationBaseline);
            Assert.IsFalse(saved.Session.IsDirty);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveAsRechecksThePickerObservationAfterStaging(bool startsAsPlaceholder)
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorSave-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string destination = Path.Combine(root, "new-target.cdb");
        string backups = Path.Combine(root, "backups");
        var converter = new SyntheticConverter
        {
            AfterImport = _ => File.WriteAllText(destination, "appeared while staging")
        };
        var service = new WorkspaceService(converter, Path.Combine(root, "sessions"), backups);
        try
        {
            await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
            if (startsAsPlaceholder)
            {
                await File.WriteAllBytesAsync(destination, []).ConfigureAwait(false);
            }

            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            converter.SessionMetadataPath = Path.Combine(session.SessionDirectory, "session.json");
            session = await service.MarkDirtyAsync(session with { HistoryUncertain = true }, CancellationToken.None)
                .ConfigureAwait(false);
            SaveDestinationSnapshot picked = await service.InspectSaveDestinationAsync(destination, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(startsAsPlaceholder ? 0L : (long?)null, picked.Length);

            SaveDestinationConflictException conflict = await Assert.ThrowsExactlyAsync<SaveDestinationConflictException>(() =>
                service.SaveAsAsync(session, destination, CancellationToken.None, picked)).ConfigureAwait(false);

            Assert.AreEqual(SaveDestinationConflictReason.Changed, conflict.Reason);
            Assert.AreEqual("appeared while staging", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
            Assert.AreEqual("source", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.IsFalse(Directory.Exists(backups));
            Assert.HasCount(2, Directory.GetFiles(root, "*.cdb"));
            service.ReleaseSession(session.SessionId);
            EditorSessionState recoverable = Assert.ContainsSingle(
                await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false)).Session;
            Assert.IsTrue(recoverable.IsDirty);
            Assert.IsTrue(recoverable.HistoryUncertain == true);
            Assert.AreEqual(source, recoverable.SaveTargetCdbPath);
            Assert.AreEqual(session.SaveDestinationBaseline, recoverable.SaveDestinationBaseline);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecoveredLegacySessionWithoutDestinationBaselineNeedsAnExplicitDecision()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorSave-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            string metadata = Path.Combine(session.SessionDirectory, "session.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metadata).ConfigureAwait(false))!.AsObject();
            Assert.IsTrue(document.Remove(nameof(EditorSessionState.SaveDestinationBaseline)));
            await File.WriteAllTextAsync(metadata, document.ToJsonString()).ConfigureAwait(false);
            service.ReleaseSession(session.SessionId);
            EditorSessionState recovered = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(recovered.SaveDestinationBaseline);
            SaveDestinationConflictException conflict = await Assert.ThrowsExactlyAsync<SaveDestinationConflictException>(() =>
                service.SaveAsAsync(recovered, source, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(SaveDestinationConflictReason.BaselineUnknown, conflict.Reason);
            Assert.AreEqual("source", await File.ReadAllTextAsync(source).ConfigureAwait(false));

            string newPath = Path.Combine(root, "different.cdb");
            WorkspaceSaveResult result = await service.SaveAsAsync(recovered, newPath, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("source", await File.ReadAllTextAsync(source).ConfigureAwait(false));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(newPath).ConfigureAwait(false));
            Assert.AreEqual(
                await service.InspectSaveDestinationAsync(newPath, CancellationToken.None).ConfigureAwait(false),
                result.Session.SaveDestinationBaseline);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

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
            service.Dispose();
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
            service.ReleaseSession(session.SessionId);
            IReadOnlyList<RecoverableSession> recoverable = await service.GetRecoverableSessionsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.HasCount(1, recoverable);
            Assert.AreEqual(session.SessionId, recoverable[0].Session.SessionId);
        }
        finally
        {
            service.Dispose();
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
            Assert.IsTrue((await ReadSessionMetadataAsync(dirty).ConfigureAwait(false)).IsDirty);
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
            service.Dispose();
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
            service.ReleaseSession(session.SessionId);
            var recoverable = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, recoverable);
            Assert.AreEqual(session.SessionId, recoverable[0].Session.SessionId);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task MetadataFailureAfterCommitAllowsClosingOnlyTheVerifiedCommittedSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        var destination = Path.Combine(root, "destination.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, "safe-old").ConfigureAwait(false);
        FileStream? metadataLock = null;
        bool rejectRetry = false;
        var converter = new SyntheticConverter
        {
            AfterImport = sessionMetadataPath =>
            {
                if (rejectRetry)
                {
                    throw new InvalidOperationException("Synthetic retry failure before replacement.");
                }
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

            metadataLock!.Dispose();
            metadataLock = null;
            EditorSessionState saved = exception.CommittedSave.Session;
            Assert.AreEqual(EditorSessionLifecycle.Saving,
                (await ReadSessionMetadataAsync(saved).ConfigureAwait(false)).Lifecycle);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CloseAsync(saved with { }, false, CancellationToken.None)).ConfigureAwait(false);

            string metadataPath = Path.Combine(saved.SessionDirectory, "session.json");
            string savingMetadata = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
            await File.WriteAllTextAsync(destination, "outside change").ConfigureAwait(false);
            await File.WriteAllTextAsync(metadataPath, "{").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CloseAsync(saved, false, CancellationToken.None)).ConfigureAwait(false);
            await File.WriteAllTextAsync(metadataPath, savingMetadata).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CloseAsync(saved, false, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsTrue(Directory.Exists(saved.SessionDirectory));
            await File.WriteAllTextAsync(destination, "converted-output").ConfigureAwait(false);

            string blockedClose = Path.Combine(Path.GetDirectoryName(saved.SessionDirectory)!, $".closed-{saved.SessionId:N}");
            Directory.CreateDirectory(blockedClose);
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                service.CloseAsync(saved, false, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(EditorSessionLifecycle.Ready,
                (await ReadSessionMetadataAsync(saved).ConfigureAwait(false)).Lifecycle);
            Directory.Delete(blockedClose);
            await File.WriteAllTextAsync(destination, "changed after metadata repair").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CloseAsync(saved, false, CancellationToken.None)).ConfigureAwait(false);
            await File.WriteAllTextAsync(destination, "converted-output").ConfigureAwait(false);

            rejectRetry = true;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.SaveAsync(saved, CancellationToken.None)).ConfigureAwait(false);
            WorkspaceCloseResult closed = await service.CloseAsync(saved, false, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(closed.RemainingDirectory);
            Assert.IsFalse(Directory.Exists(saved.SessionDirectory));
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(destination).ConfigureAwait(false));
        }
        finally
        {
            metadataLock?.Dispose();
            service.Dispose();
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
            service.ReleaseSession(session.SessionId);
            Assert.HasCount(1, await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            service.Dispose();
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
            Assert.IsFalse((await ReadSessionMetadataAsync(session).ConfigureAwait(false)).IsDirty);

            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            EditHistoryReplay redoInitialEdit = history.TakeRedoReplay();
            history.CompleteRedo(redoInitialEdit, []);
            Assert.IsTrue(history.State.IsDirty);
            Assert.IsTrue((await ReadSessionMetadataAsync(session).ConfigureAwait(false)).IsDirty);

            WorkspaceSaveResult saved = await service.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            session = saved.Session;
            history.MarkSavedBaseline();
            Assert.IsFalse(session.IsDirty);
            Assert.IsFalse(history.State.IsDirty);

            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            EditHistoryReplay undoSavedEdit = history.TakeUndoReplay();
            history.CompleteUndo(undoSavedEdit, []);
            Assert.IsTrue(history.State.IsDirty);
            Assert.IsTrue((await ReadSessionMetadataAsync(session).ConfigureAwait(false)).IsDirty);

            EditHistoryReplay redoSavedEdit = history.TakeRedoReplay();
            history.CompleteRedo(redoSavedEdit, []);
            Assert.IsFalse(history.State.IsDirty);
            session = await service.PersistSavedBaselineAsync(session, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(session.IsDirty);
            Assert.IsFalse((await ReadSessionMetadataAsync(session).ConfigureAwait(false)).IsDirty);

            await service.CloseAsync(session, discardDirtySession: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InterruptedMutationRemainsDirtyThroughLaterHistoryAndRecoveryUntilSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        var writeAhead = new MutationWriteAhead(service);
        try
        {
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            session = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
            await using (var connection = new SqliteConnection($"Data Source={session.WorkingSqlitePath};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO neutral VALUES (1, 'unrecorded')";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Simulate termination before the committed mutation is recorded.
            service.ReleaseSession(session.SessionId);
            session = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(session.HistoryUncertain == true);
            var history = new EditHistory(Path.Combine(session.SessionDirectory, "edit-history.json"), recovering: true);
            var catalog = await new SqliteTableCatalog().DiscoverAsync(session.WorkingSqlitePath, CancellationToken.None).ConfigureAwait(false);
            var store = new SqliteTableDataStore();
            TypedRow original = (await store.QueryAsync(session.WorkingSqlitePath, catalog,
                new TableQuery("neutral", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
            var update = new RowUpdateOperation(Guid.NewGuid(), "neutral", DateTimeOffset.UtcNow, original.Identity!,
                [KeyValuePair.Create("value", original.Values["value"])],
                [KeyValuePair.Create("value", SqliteValue.Text("later"))], original.Revision);
            session = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
            EditResult updated = await store.UpdateRowAsync(session.WorkingSqlitePath, catalog, update, CancellationToken.None).ConfigureAwait(false);
            history.Record(update, [RowReplayGuard.Present("neutral", updated.CurrentRow!)]);
            session = await writeAhead.CompleteAsync(session).ConfigureAwait(false);
            EditHistoryReplay replay = history.TakeUndoReplay();
            session = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
            EditReplayResult undone = await new SqliteEditOperationReplayer().ReplayAsync(
                session.WorkingSqlitePath, catalog, replay, CancellationToken.None).ConfigureAwait(false);
            history.CompleteUndo(replay, undone.OppositeGuards);
            session = await writeAhead.CompleteAsync(session).ConfigureAwait(false);
            TypedRow afterUndo = (await store.QueryAsync(session.WorkingSqlitePath, catalog,
                new TableQuery("neutral", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
            Assert.AreEqual("unrecorded", afterUndo.Values["value"].TextValue);
            Assert.IsTrue(history.State.IsDirty);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.PersistSavedBaselineAsync(session, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CloseAsync(session, discardDirtySession: false, CancellationToken.None)).ConfigureAwait(false);

            service.ReleaseSession(session.SessionId);
            session = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(session.IsDirty);
            Assert.IsTrue(session.HistoryUncertain == true);
            Assert.IsTrue(new EditHistory(Path.Combine(session.SessionDirectory, "edit-history.json"), recovering: true).State.IsDirty);
            WorkspaceSaveResult saved = await service.SaveAsAsync(session, Path.Combine(root, "saved.cdb"), CancellationToken.None).ConfigureAwait(false);
            history.MarkSavedBaseline();
            Assert.IsFalse(saved.Session.IsDirty);
            Assert.IsTrue(saved.Session.HistoryUncertain == false);
            Assert.IsFalse(history.State.IsDirty);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InvalidAndInterruptedHistoryStayUncertainAcrossReloadAndUndo()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.json");
            File.WriteAllText(path, "{ malformed");
            var history = new EditHistory(path);
            Assert.IsTrue(history.State.HistoryUncertain);
            history.Record(Operation("later", 1));
            EditHistoryReplay undo = history.TakeUndoReplay();
            history.CompleteUndo(undo, []);
            Assert.IsTrue(new EditHistory(path).State.IsDirty);
            history.MarkSavedBaseline();
            Assert.IsFalse(new EditHistory(path).State.IsDirty);

            history.Record(Operation("another", 2));
            _ = history.TakeUndoReplay();
            var recovered = new EditHistory(path, recovering: true);
            Assert.IsTrue(recovered.State.RecoveredInterruptedReplay);
            Assert.IsTrue(recovered.State.HistoryUncertain);
            Assert.IsTrue(new EditHistory(path).State.HistoryUncertain);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LegacyDirtySessionAndMissingHistoryRemainUncertain()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session with { HistoryUncertain = null }, CancellationToken.None).ConfigureAwait(false);
            service.ReleaseSession(session.SessionId);
            session = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(session.HistoryUncertain == true);
            var path = Path.Combine(session.SessionDirectory, "edit-history.json");
            Assert.IsTrue(new EditHistory(path, recovering: true).State.HistoryUncertain);
            Assert.IsTrue(new EditHistory(path).State.HistoryUncertain);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.PersistSavedBaselineAsync(session, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InterruptedSaveAfterDestinationReplacementMakesRecoveredBaselineUncertain()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "original").ConfigureAwait(false);
        FileStream? metadataLock = null;
        var converter = new SyntheticConverter
        {
            AfterImport = path => metadataLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        var service = new WorkspaceService(converter, Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        var writeAhead = new MutationWriteAhead(service);
        try
        {
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            converter.SessionMetadataPath = Path.Combine(session.SessionDirectory, "session.json");
            await using (var connection = new SqliteConnection($"Data Source={session.WorkingSqlitePath};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO neutral VALUES (1, 'before')";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var catalog = await new SqliteTableCatalog().DiscoverAsync(session.WorkingSqlitePath, CancellationToken.None).ConfigureAwait(false);
            var store = new SqliteTableDataStore();
            TypedRow original = (await store.QueryAsync(session.WorkingSqlitePath, catalog,
                new TableQuery("neutral", new PageRequest(0, 10)), CancellationToken.None).ConfigureAwait(false)).Rows.Single();
            var update = new RowUpdateOperation(Guid.NewGuid(), "neutral", DateTimeOffset.UtcNow, original.Identity!,
                [KeyValuePair.Create("value", original.Values["value"])],
                [KeyValuePair.Create("value", SqliteValue.Text("saved"))], original.Revision);
            var history = new EditHistory(Path.Combine(session.SessionDirectory, "edit-history.json"));
            session = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
            EditResult updated = await store.UpdateRowAsync(session.WorkingSqlitePath, catalog, update, CancellationToken.None).ConfigureAwait(false);
            history.Record(update, [RowReplayGuard.Present("neutral", updated.CurrentRow!)]);
            session = await writeAhead.CompleteAsync(session).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<WorkspaceSaveCommitException>(() =>
                service.SaveAsync(session, CancellationToken.None)).ConfigureAwait(false);
            metadataLock!.Dispose();
            metadataLock = null;
            Assert.AreEqual("converted-output", await File.ReadAllTextAsync(source).ConfigureAwait(false));

            service.ReleaseSession(session.SessionId);
            session = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(session.IsDirty);
            Assert.IsTrue(session.HistoryUncertain == true);
            EditHistoryReplay replay = history.TakeUndoReplay();
            session = await writeAhead.PrepareAsync(session).ConfigureAwait(false);
            EditReplayResult undone = await new SqliteEditOperationReplayer().ReplayAsync(
                session.WorkingSqlitePath, catalog, replay, CancellationToken.None).ConfigureAwait(false);
            history.CompleteUndo(replay, undone.OppositeGuards);
            session = await writeAhead.CompleteAsync(session).ConfigureAwait(false);
            Assert.IsFalse(history.State.IsDirty);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.PersistSavedBaselineAsync(session, CancellationToken.None)).ConfigureAwait(false);
            service.ReleaseSession(session.SessionId);
            Assert.IsTrue((await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false)).HistoryUncertain == true);
        }
        finally
        {
            metadataLock?.Dispose();
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MalformedSessionMetadataDoesNotHideOtherRecoverableSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), sessions, Path.Combine(root, "backups"));
        try
        {
            var badId = Guid.NewGuid();
            var badDirectory = Path.Combine(sessions, badId.ToString("N"));
            Directory.CreateDirectory(badDirectory);
            var badPath = Path.Combine(badDirectory, "session.json");
            string invalid = $"{{\"SessionId\":\"{badId}\",\"IsDirty\":true,\"Lifecycle\":5}}";
            await File.WriteAllTextAsync(badPath, invalid).ConfigureAwait(false);
            EditorSessionState valid = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            valid = await service.MarkDirtyAsync(valid, CancellationToken.None).ConfigureAwait(false);

            service.ReleaseSession(valid.SessionId);
            await service.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            IReadOnlyList<RecoverableSession> recovered = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, recovered);
            Assert.AreEqual(valid.SessionId, recovered[0].Session.SessionId);
            Assert.AreEqual(invalid, await File.ReadAllTextAsync(badPath).ConfigureAwait(false));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CleanupSkipsLockedSessionAndOtherRecoveryRemainsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            EditorSessionState locked = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            locked = await service.MarkDirtyAsync(locked, CancellationToken.None).ConfigureAwait(false);
            EditorSessionState available = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            available = await service.MarkDirtyAsync(available, CancellationToken.None).ConfigureAwait(false);
            service.ReleaseSession(locked.SessionId);
            service.ReleaseSession(available.SessionId);
            var metadata = Path.Combine(locked.SessionDirectory, "session.json");
            string before = await File.ReadAllTextAsync(metadata).ConfigureAwait(false);
            using (var metadataLock = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await service.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                IReadOnlyList<RecoverableSession> found = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, found);
                Assert.AreEqual(available.SessionId, found[0].Session.SessionId);
                Assert.IsTrue(Directory.Exists(locked.SessionDirectory));
            }

            Assert.AreEqual(before, await File.ReadAllTextAsync(metadata).ConfigureAwait(false));
            Assert.HasCount(2, await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CleanupSkipsLockedCompletedSessionAndFindsDirtySession()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            EditorSessionState completed = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            var metadata = Path.Combine(completed.SessionDirectory, "session.json");
            await File.WriteAllTextAsync(metadata, System.Text.Json.JsonSerializer.Serialize(
                completed with { Lifecycle = EditorSessionLifecycle.Closed })).ConfigureAwait(false);
            var lockedPath = Path.Combine(completed.SessionDirectory, "locked.bin");
            await File.WriteAllTextAsync(lockedPath, "locked").ConfigureAwait(false);
            EditorSessionState dirty = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            dirty = await service.MarkDirtyAsync(dirty, CancellationToken.None).ConfigureAwait(false);

            service.ReleaseSession(completed.SessionId);
            service.ReleaseSession(dirty.SessionId);
            using (var fileLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await service.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(lockedPath));
                IReadOnlyList<RecoverableSession> found = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, found);
                Assert.AreEqual(dirty.SessionId, found[0].Session.SessionId);
            }
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitCleanupKeepsCompletedRecordsWithUnsavedWorkAndUnreadableMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), sessions, Path.Combine(root, "backups"));
        try
        {
            EditorSessionState completed = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            EditorSessionState dirty = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(completed.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(completed with { Lifecycle = EditorSessionLifecycle.Closed }))
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dirty.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(dirty with { IsDirty = true, Lifecycle = EditorSessionLifecycle.Closed }))
                .ConfigureAwait(false);
            var malformedDirectory = Path.Combine(sessions, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(malformedDirectory);
            var malformedPath = Path.Combine(malformedDirectory, "session.json");
            await File.WriteAllTextAsync(malformedPath, "{").ConfigureAwait(false);

            service.ReleaseSession(completed.SessionId);
            service.ReleaseSession(dirty.SessionId);
            await service.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(completed.SessionDirectory));
            Assert.IsTrue(File.Exists(dirty.WorkingSqlitePath));
            Assert.AreEqual("{", await File.ReadAllTextAsync(malformedPath).ConfigureAwait(false));
            RecoverableSession recoverable = Assert.ContainsSingle(
                await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(dirty.SessionId, recoverable.Session.SessionId);
            Assert.AreEqual("{", await File.ReadAllTextAsync(malformedPath).ConfigureAwait(false));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellingResumeDuringLockedValidationKeepsMetadataAndReleasesTheSession()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorResumeCancel-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        using var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            session = await service.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);
            service.ReleaseSession(session.SessionId);
            string metadataPath = Path.Combine(session.SessionDirectory, "session.json");
            string before = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);

            await using var connection = SqliteSupport.CreateConnection(session.WorkingSqlitePath);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            Task<EditorSessionState> recovery = service.RecoverAsync(session.SessionId, cancellation.Token, session.UpdatedAtUtc);
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
                Assert.IsFalse(recovery.IsCompleted, "Validation should be waiting for the SQLite lock.");
                await cancellation.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(() => recovery.WaitAsync(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(false);
                Assert.AreEqual(before, await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false));
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                command.CommandText = "ROLLBACK";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                await ((Task)recovery).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            EditorSessionState resumed = await service.RecoverAsync(session.SessionId, CancellationToken.None, session.UpdatedAtUtc)
                .ConfigureAwait(false);
            Assert.IsTrue(resumed.IsDirty);
            Assert.AreEqual(session.SessionId, resumed.SessionId);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ActiveWorkingSessionsCannotBeResumedOrDiscardedByAnotherEditor()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorOwnership-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string sessions = Path.Combine(root, "sessions");
        string backups = Path.Combine(root, "backups");
        try
        {
            await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
            using var owner = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            using var secondEditor = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            EditorSessionState session = await owner.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            session = await owner.MarkDirtyAsync(session, CancellationToken.None).ConfigureAwait(false);

            Assert.IsEmpty(await secondEditor.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondEditor.RecoverAsync(session.SessionId, CancellationToken.None, session.UpdatedAtUtc))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondEditor.DiscardRecoverableSessionAsync(session.SessionId, session.UpdatedAtUtc, CancellationToken.None))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondEditor.MarkDirtyAsync(session, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondEditor.SaveAsync(session, CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                secondEditor.CloseAsync(session, discardDirtySession: true, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(session.WorkingSqlitePath));

            owner.Dispose();
            RecoverableSession listed = Assert.ContainsSingle(
                await secondEditor.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(session.SessionId, listed.Session.SessionId);
            EditorSessionState resumed = await secondEditor.RecoverAsync(
                session.SessionId, CancellationToken.None, listed.Session.UpdatedAtUtc).ConfigureAwait(false);
            Assert.IsTrue(resumed.IsDirty);

            using var thirdEditor = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            Assert.IsEmpty(await thirdEditor.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                thirdEditor.DiscardRecoverableSessionAsync(resumed.SessionId, resumed.UpdatedAtUtc, CancellationToken.None))
                .ConfigureAwait(false);
            await secondEditor.CloseAsync(resumed, discardDirtySession: true, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(session.SessionDirectory));
            Assert.AreEqual("source", await File.ReadAllTextAsync(source).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CleanupWaitsForTheOwnerEvenWhenCompletedMetadataIsAlreadyWritten()
    {
        string root = Directory.CreateTempSubdirectory("PcmCdbEditorOwnership-").FullName;
        string source = Path.Combine(root, "neutral.cdb");
        string sessions = Path.Combine(root, "sessions");
        string backups = Path.Combine(root, "backups");
        try
        {
            await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
            using var owner = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            using var secondEditor = new WorkspaceService(new SyntheticConverter(), sessions, backups);
            EditorSessionState session = await owner.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(session.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(session with { Lifecycle = EditorSessionLifecycle.Closed }))
                .ConfigureAwait(false);

            await secondEditor.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(session.WorkingSqlitePath));

            owner.Dispose();
            await secondEditor.CleanCompletedSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(session.SessionDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecoveryActionsRejectStaleSelectionsAndDiscardRequiresRecoverableWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        var service = new WorkspaceService(new SyntheticConverter(), Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            EditorSessionState clean = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None)
                .ConfigureAwait(false);
            service.ReleaseSession(clean.SessionId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.DiscardRecoverableSessionAsync(clean.SessionId, clean.UpdatedAtUtc, CancellationToken.None))
                .ConfigureAwait(false);
            Assert.IsTrue(File.Exists(clean.WorkingSqlitePath));

            EditorSessionState reviewed = clean with { IsDirty = true };
            EditorSessionState changed = reviewed with { UpdatedAtUtc = reviewed.UpdatedAtUtc.AddMinutes(1) };
            await File.WriteAllTextAsync(Path.Combine(reviewed.SessionDirectory, "session.json"),
                System.Text.Json.JsonSerializer.Serialize(changed)).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.RecoverAsync(reviewed.SessionId, CancellationToken.None, reviewed.UpdatedAtUtc)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.DiscardRecoverableSessionAsync(reviewed.SessionId, reviewed.UpdatedAtUtc, CancellationToken.None))
                .ConfigureAwait(false);
            Assert.IsTrue(File.Exists(reviewed.WorkingSqlitePath));

            await service.DiscardRecoverableSessionAsync(changed.SessionId, changed.UpdatedAtUtc, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(changed.SessionDirectory));
            Assert.AreEqual("source", await File.ReadAllTextAsync(source).ConfigureAwait(false));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<EditorSessionState> ReadSessionMetadataAsync(EditorSessionState session)
    {
        string json = await File.ReadAllTextAsync(Path.Combine(session.SessionDirectory, "session.json"))
            .ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<EditorSessionState>(json)
            ?? throw new AssertFailedException("Session metadata was missing.");
    }

    [TestMethod]
    public async Task InterruptedSaveOfCleanSessionIsDiscoverableAsUncertain()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "neutral.cdb");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(false);
        FileStream? metadataLock = null;
        var converter = new SyntheticConverter
        {
            AfterImport = path => metadataLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        var service = new WorkspaceService(converter, Path.Combine(root, "sessions"), Path.Combine(root, "backups"));
        try
        {
            EditorSessionState session = await service.OpenAsync(new WorkspaceOpenRequest(source), CancellationToken.None).ConfigureAwait(false);
            converter.SessionMetadataPath = Path.Combine(session.SessionDirectory, "session.json");
            await Assert.ThrowsExactlyAsync<WorkspaceSaveCommitException>(() =>
                service.SaveAsync(session, CancellationToken.None)).ConfigureAwait(false);
            metadataLock!.Dispose();
            metadataLock = null;
            service.ReleaseSession(session.SessionId);
            IReadOnlyList<RecoverableSession> recoverable = await service.GetRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, recoverable);
            EditorSessionState resumed = await service.RecoverAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(resumed.IsDirty);
            Assert.IsTrue(resumed.HistoryUncertain == true);
        }
        finally
        {
            metadataLock?.Dispose();
            service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FailedReplaySnapshotWritesKeepEntriesOrPendingReplayAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "PcmCdbEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.json");
            var history = new EditHistory(path);
            history.Record(Operation("edited", 1));
            using (var snapshotLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AssertSnapshotWriteFails(() => history.TakeUndoReplay());
            }
            Assert.IsTrue(history.State.CanUndo);
            Assert.IsFalse(history.State.HasPendingReplay);
            Assert.IsTrue(new EditHistory(path).State.CanUndo);

            EditHistoryReplay undo = history.TakeUndoReplay();
            using (var snapshotLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AssertSnapshotWriteFails(() => history.RestoreFailedUndo(undo));
            }
            Assert.IsTrue(history.State.HasPendingReplay);
            Assert.IsFalse(history.State.CanUndo);
            history.RestoreFailedUndo(undo);
            undo = history.TakeUndoReplay();
            history.CompleteUndo(undo, []);

            using (var snapshotLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AssertSnapshotWriteFails(() => history.TakeRedoReplay());
            }
            Assert.IsTrue(history.State.CanRedo);
            Assert.IsFalse(history.State.HasPendingReplay);
            EditHistoryReplay redo = history.TakeRedoReplay();
            using (var snapshotLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AssertSnapshotWriteFails(() => history.RestoreFailedRedo(redo));
            }
            Assert.IsTrue(history.State.HasPendingReplay);
            Assert.IsFalse(history.State.CanRedo);
            history.RestoreFailedRedo(redo);
            Assert.IsTrue(history.State.CanRedo);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertSnapshotWriteFails(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Fail("The locked history snapshot should reject the write.");
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
