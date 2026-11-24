using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Internal;
using PcmCdbEditor.Infrastructure.Sqlite;
using SQLitePCL;

namespace PcmCdbEditor.Infrastructure.Workspace;

public sealed partial class WorkspaceService : IWorkspaceService, IDisposable
{
    private const string MetadataFileName = "session.json";
    private const string WorkingCdbFileName = "working.cdb";
    private const string WorkingSqliteFileName = "working.sqlite";

    private readonly ICdbConverter _converter;
    private readonly string _sessionsRoot;
    private readonly string _backupsRoot;
    private readonly TimeSpan _converterTimeout;

    public WorkspaceService(
        ICdbConverter converter,
        string? sessionsRoot = null,
        string? backupsRoot = null,
        TimeSpan? converterTimeout = null)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        var applicationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PcmCdbEditor");
        _sessionsRoot = Path.GetFullPath(sessionsRoot ?? Path.Combine(applicationRoot, "Sessions"));
        _backupsRoot = Path.GetFullPath(backupsRoot ?? Path.Combine(applicationRoot, "Backups"));
        _converterTimeout = converterTimeout ?? TimeSpan.FromMinutes(10);
        if (_converterTimeout <= TimeSpan.Zero || _converterTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(converterTimeout),
                "The workspace converter timeout must be between zero and ten minutes.");
        }
    }

    public async Task<EditorSessionState> OpenAsync(
        WorkspaceOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = ValidateCdbInput(request.SourceCdbPath);
        var sessionId = Guid.NewGuid();
        var sessionDirectory = SessionDirectory(sessionId);
        var workingCdb = Path.Combine(sessionDirectory, WorkingCdbFileName);
        var workingSqlite = Path.Combine(sessionDirectory, WorkingSqliteFileName);
        var now = DateTimeOffset.UtcNow;
        var state = new EditorSessionState(
            sessionId,
            source,
            source,
            sessionDirectory,
            workingCdb,
            workingSqlite,
            IsDirty: false,
            EditorSessionLifecycle.Creating,
            now,
            now,
            LastBackupPath: null,
            HistoryUncertain: false);
        FileStream? ownership = AcquireSessionOwnership(sessionId);
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            state = await TransitionAsync(state, EditorSessionLifecycle.CopyingSource, cancellationToken)
                .ConfigureAwait(false);
            // Hold the source open without write or delete sharing while copying it.
            // The baseline describes the exact bytes given to the converter.
            using (FileStream sourceStream = OpenReadStream(source))
            await using (var copy = new FileStream(workingCdb, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            }

            EnsureNonEmpty(workingCdb, "working CDB");
            SaveDestinationSnapshot copiedSource = await InspectCdbAsync(workingCdb, cancellationToken)
                .ConfigureAwait(false);
            state = state with { SaveDestinationBaseline = copiedSource with { Path = source } };
            state = await TransitionAsync(state, EditorSessionLifecycle.Converting, cancellationToken)
                .ConfigureAwait(false);
            var conversion = await _converter.ExportToSqliteAsync(
                    workingCdb,
                    _converterTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var exportedPath = Path.GetFullPath(conversion.OutputPath);
            if (!exportedPath.Equals(workingSqlite, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The converter wrote outside the session's expected SQLite path.");
            }

            await ValidateSqliteAsync(workingSqlite, cancellationToken).ConfigureAwait(false);
            EditorSessionState ready = await TransitionAsync(state, EditorSessionLifecycle.Ready, cancellationToken)
                .ConfigureAwait(false);
            RetainSessionOwnership(sessionId, ownership);
            ownership = null;
            return ready;
        }
        catch
        {
            DeleteSessionDirectory(sessionDirectory);
            throw;
        }
        finally
        {
            ownership?.Dispose();
        }
    }

    public async Task<EditorSessionState> RecoverAsync(
        Guid sessionId,
        CancellationToken cancellationToken,
        DateTimeOffset? expectedUpdatedAtUtc = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? ownership = AcquireSessionOwnership(sessionId);
        try
        {
            EditorSessionState state = await ReadReviewedRecoveryAsync(
                sessionId, expectedUpdatedAtUtc, resume: true, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            state = ReconcileInterruptedSave(state, allowDeferredCleanup: true);
            EditorSessionState recovered = await PersistAsync(
                state with
                {
                    Lifecycle = EditorSessionLifecycle.Ready,
                    IsDirty = true,
                    HistoryUncertain = state.HistoryUncertain != false || state.MutationPending ||
                        state.Lifecycle == EditorSessionLifecycle.Saving,
                    MutationPending = false,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
            RetainSessionOwnership(sessionId, ownership);
            ownership = null;
            return recovered;
        }
        finally
        {
            ownership?.Dispose();
        }
    }

    public Task<SaveDestinationSnapshot> InspectSaveDestinationAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string destination = ValidateSaveDestination(path);
        return InspectCdbAsync(destination, cancellationToken);
    }

    private static async Task<SaveDestinationSnapshot> InspectCdbAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream? stream = OpenDestinationIfPresent(destination);
        return await CaptureSaveDestinationAsync(destination, stream, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkspaceSaveResult> SaveAsync(
        EditorSessionState session,
        CancellationToken cancellationToken,
        SaveDestinationSnapshot? confirmedDestination = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveCoreAsync(session, session.SaveTargetCdbPath, confirmedDestination, cancellationToken);
    }

    public Task<WorkspaceSaveResult> SaveAsAsync(
        EditorSessionState session,
        string destinationCdbPath,
        CancellationToken cancellationToken,
        SaveDestinationSnapshot? confirmedDestination = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveCoreAsync(session, destinationCdbPath, confirmedDestination, cancellationToken);
    }

    public Task<EditorSessionState> MarkDirtyAsync(
        EditorSessionState session,
        CancellationToken cancellationToken) =>
        PersistRecoveryStateAsync(session, isDirty: true, cancellationToken);

    public Task<EditorSessionState> PersistSavedBaselineAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        if (session.MutationPending || session.HistoryUncertain != false)
        {
            throw new InvalidOperationException("Uncertain history cannot establish a clean baseline without saving.");
        }

        return PersistRecoveryStateAsync(session, isDirty: false, cancellationToken);
    }

    private async Task<EditorSessionState> PersistRecoveryStateAsync(
        EditorSessionState session,
        bool isDirty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionPaths(session);
        RequireSessionOwnership(session.SessionId);
        EditorSessionState? persisted = await LoadSessionAsync(session.SessionId, cancellationToken)
            .ConfigureAwait(false);
        return await PersistAsync(
            session with
            {
                PendingSaveAttempt = session.PendingSaveAttempt ?? persisted?.PendingSaveAttempt,
                DeferredSaveAttempts = persisted?.DeferredSaveAttempts ?? session.DeferredSaveAttempts,
                IsDirty = isDirty,
                Lifecycle = EditorSessionLifecycle.Ready,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceCloseResult> CloseAsync(
        EditorSessionState session,
        bool discardDirtySession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionPaths(session);
        RequireSessionOwnership(session.SessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!discardDirtySession)
        {
            await ConfirmCloseWithoutDiscardAsync(session, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string completedPath = MoveToCompletedDirectory(session.SessionId, session.SessionDirectory);
        try
        {
            return new WorkspaceCloseResult(await TryDeleteCompletedDirectoryAsync(completedPath)
                .ConfigureAwait(false) ? null : completedPath);
        }
        finally
        {
            ReleaseSession(session.SessionId);
        }
    }

    private async Task ConfirmCloseWithoutDiscardAsync(EditorSessionState session, CancellationToken cancellationToken)
    {
        EditorSessionState? persisted = await LoadSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        CommittedSaveReceipt? receipt = GetUnsynchronizedSave(session.SessionId);
        if (HasRecoverableWork(session))
        {
            throw new InvalidOperationException("The session has unsaved changes and was not approved for discard.");
        }

        if (receipt is not null && ReferenceEquals(receipt.Committed, session))
        {
            bool matchesCommitted = persisted is not null && MatchesCommittedBaseline(persisted, receipt.Committed);
            if (persisted is not null && !matchesCommitted && !SessionStatesMatch(persisted, receipt.Saving))
            {
                throw new InvalidOperationException("The recovery information changed before the working session could close.");
            }

            // Only the lease that saw this exact save commit can reconcile the stale state.
            SaveDestinationSnapshot destination = await InspectSaveDestinationAsync(
                session.SaveTargetCdbPath, cancellationToken).ConfigureAwait(false);
            if (session.SaveDestinationBaseline is not { } baseline || !HaveSameContents(baseline, destination))
            {
                throw new InvalidOperationException("The saved destination changed before the working session could close.");
            }
            if (!matchesCommitted)
            {
                await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if ((persisted is not null && HasRecoverableWork(persisted)) ||
            (receipt is not null && (persisted is null || MatchesCommittedBaseline(persisted, receipt.Committed))))
        {
            throw new InvalidOperationException("The session has unsaved changes and was not approved for discard.");
        }
    }

    private static bool MatchesCommittedBaseline(EditorSessionState persisted, EditorSessionState committed) =>
        // A later failed save can update cleanup information while leaving the committed baseline intact.
        SessionStatesMatch(persisted with
        {
            UpdatedAtUtc = committed.UpdatedAtUtc,
            PendingSaveAttempt = committed.PendingSaveAttempt,
            DeferredSaveAttempts = committed.DeferredSaveAttempts,
            LastBackupPath = committed.LastBackupPath
        }, committed);

    public async Task<IReadOnlyList<RecoverableSession>> GetRecoverableSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return [];
        }

        var recoverable = new List<RecoverableSession>();
        foreach (var directory in Directory.EnumerateDirectories(_sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var sessionId))
            {
                continue;
            }

            try
            {
                using FileStream ownership = AcquireSessionOwnership(sessionId);
                var state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (state is null || !HasRecoverableWork(state))
                {
                    continue;
                }

                if (state.SessionId != sessionId)
                {
                    continue;
                }
                ValidateSessionPaths(state);
                EnsureNonEmpty(state.WorkingCdbPath, "recoverable working CDB");
                bool requiresJournalRecovery = await ValidateRecoverySqliteAsync(
                    state.WorkingSqlitePath, resume: false, cancellationToken).ConfigureAwait(false);

                var description = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unsaved {Path.GetFileName(state.SourceCdbPath)} session from {state.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC");
                recoverable.Add(new RecoverableSession(
                    state with { Lifecycle = EditorSessionLifecycle.Recoverable },
                    description,
                    requiresJournalRecovery));
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or InvalidOperationException
                                              or ArgumentException
                                              or SqliteException)
            {
                continue;
            }
        }

        return recoverable
            .OrderByDescending(static item => item.Session.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<WorkspaceCloseResult> DiscardRecoverableSessionAsync(
        Guid sessionId,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream ownership = AcquireSessionOwnership(sessionId);
        EditorSessionState state = await ReadReviewedRecoveryAsync(
            sessionId, expectedUpdatedAtUtc, resume: false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string completedPath = MoveToCompletedDirectory(sessionId, state.SessionDirectory);
        return new WorkspaceCloseResult(await TryDeleteCompletedDirectoryAsync(completedPath)
            .ConfigureAwait(false) ? null : completedPath);
    }

    private async Task<EditorSessionState> ReadReviewedRecoveryAsync(
        Guid sessionId,
        DateTimeOffset? expectedUpdatedAtUtc,
        bool resume,
        CancellationToken cancellationToken)
    {
        EditorSessionState state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The recoverable editor session was not found.");
        if (state.SessionId != sessionId || !HasRecoverableWork(state))
        {
            throw new InvalidOperationException("The session has no unsaved changes to recover.");
        }

        if (expectedUpdatedAtUtc is { } reviewed && state.UpdatedAtUtc != reviewed)
        {
            throw new InvalidOperationException("The working session changed since it was listed. Refresh to review it again.");
        }

        ValidateSessionPaths(state);
        EnsureNonEmpty(state.WorkingCdbPath, "recoverable working CDB");
        await ValidateRecoverySqliteAsync(state.WorkingSqlitePath, resume, cancellationToken)
            .ConfigureAwait(false);
        if (expectedUpdatedAtUtc.HasValue)
        {
            EditorSessionState? latest = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (latest is null || !SessionStatesMatch(latest, state))
            {
                throw new InvalidOperationException("The working session changed since it was listed. Refresh to review it again.");
            }
        }

        return state;
    }

    private static bool SessionStatesMatch(EditorSessionState left, EditorSessionState right) =>
        left with { DeferredSaveAttempts = null } == right with { DeferredSaveAttempts = null } &&
        (left.DeferredSaveAttempts ?? []).SequenceEqual(right.DeferredSaveAttempts ?? []);

    private static bool HasRecoverableWork(EditorSessionState state) =>
        state.IsDirty || state.MutationPending || state.HistoryUncertain == true ||
        state.Lifecycle == EditorSessionLifecycle.Saving;

    public async Task CleanCompletedSessionsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            bool completed = name.StartsWith(".closed-", StringComparison.Ordinal);
            if (!Guid.TryParseExact(completed ? name[8..] : name, "N", out var sessionId))
            {
                continue;
            }

            try
            {
                using FileStream ownership = AcquireSessionOwnership(sessionId);
                if (completed)
                {
                    _ = await TryDeleteCompletedDirectoryAsync(CompletedDirectory(sessionId)).ConfigureAwait(false);
                    continue;
                }

                EditorSessionState? state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (state is not null && state.SessionId == sessionId && !HasRecoverableWork(state)
                    && (state.Lifecycle is EditorSessionLifecycle.Closed or EditorSessionLifecycle.Cancelled ||
                        state is { Lifecycle: EditorSessionLifecycle.Ready, HistoryUncertain: false }
                        && state.SaveDestinationBaseline is { } baseline && IsValidSnapshot(baseline, state.SaveTargetCdbPath)))
                {
                    ValidateSessionPaths(state);
                    cancellationToken.ThrowIfCancellationRequested();
                    string completedPath = MoveToCompletedDirectory(sessionId, directory);
                    _ = await TryDeleteCompletedDirectoryAsync(completedPath).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or InvalidOperationException
                                              or ArgumentException)
            {
                // Leave an unavailable session intact and continue checking others.
            }
        }
    }

    private async Task<WorkspaceSaveResult> SaveCoreAsync(
        EditorSessionState session,
        string destinationCdbPath,
        SaveDestinationSnapshot? confirmedDestination,
        CancellationToken cancellationToken)
    {
        ValidateSessionPaths(session);
        RequireSessionOwnership(session.SessionId);
        var destination = ValidateSaveDestination(destinationCdbPath);
        if (confirmedDestination is not null && !IsValidSnapshot(confirmedDestination, destination))
        {
            throw new ArgumentException("The confirmation must identify the observed save destination.", nameof(confirmedDestination));
        }

        SaveDestinationSnapshot observed = await InspectCdbAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        bool isCurrentDestination = destination.Equals(Path.GetFullPath(session.SaveTargetCdbPath), StringComparison.OrdinalIgnoreCase);
        SaveDestinationSnapshot expected = RequireExpectedDestination(
            confirmedDestination ?? (isCurrentDestination ? session.SaveDestinationBaseline : observed), observed);
        EnsureNonEmpty(session.WorkingCdbPath, "working CDB");
        await ValidateSqliteAsync(session.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
        string destinationDirectory = Path.GetDirectoryName(destination)!;
        EditorSessionState? previousSave = await LoadSessionAsync(session.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (previousSave is not null)
        {
            session = session with
            {
                PendingSaveAttempt = previousSave.PendingSaveAttempt,
                DeferredSaveAttempts = previousSave.DeferredSaveAttempts
            };
        }
        session = ReconcileInterruptedSave(session, allowDeferredCleanup: true);
        if (session.PendingSaveAttempt is { } unfinished)
        {
            session = session with
            {
                PendingSaveAttempt = null,
                DeferredSaveAttempts = [.. session.DeferredSaveAttempts ?? [], unfinished]
            };
        }
        Directory.CreateDirectory(destinationDirectory);
        string physicalDirectory = ResolveDirectoryLinks(destinationDirectory);
        var attempt = new WorkspaceSaveAttempt(Guid.NewGuid(), destination, physicalDirectory)
        {
            StagingDirectoryIdentity = WindowsDirectoryIdentity.Read(physicalDirectory)
        };
        var saving = await TransitionAsync(session with { PendingSaveAttempt = attempt },
                EditorSessionLifecycle.Saving, cancellationToken)
            .ConfigureAwait(false);
        var (stagedCdb, backupCandidate) = GetAttemptPaths(attempt);
        string? displacedBackup = null;
        SaveDestinationSnapshot stagedBaseline;
        try
        {
            var conversion = await _converter.ImportToCdbAsync(
                    saving.WorkingSqlitePath,
                    stagedCdb,
                    _converterTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!Path.GetFullPath(conversion.OutputPath).Equals(stagedCdb, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The converter wrote outside the expected save staging path.");
            }

            EnsureNonEmpty(stagedCdb, "staged CDB");
            stagedBaseline = await InspectCdbAsync(stagedCdb, cancellationToken).ConfigureAwait(false);
            if (stagedBaseline.Length is not > 0)
            {
                throw new InvalidDataException("The staged CDB is missing or empty.");
            }

            _ = ValidateSaveDestination(destination);
            ValidateStagingDirectory(attempt);
            // Recheck after conversion under a handle that denies writes and rename.
            using (FileStream? destinationStream = OpenDestinationIfPresent(destination))
            {
                observed = await CaptureSaveDestinationAsync(destination, destinationStream, cancellationToken)
                    .ConfigureAwait(false);
                _ = RequireExpectedDestination(expected, observed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (observed.Length.HasValue)
            {
                // The handle must be released to replace. Let the atomic operation
                // itself preserve whichever destination exists at the handoff.
                displacedBackup = backupCandidate;
                ReplaceDestination(stagedCdb, destination, displacedBackup);
            }
            else
            {
                try
                {
                    File.Move(stagedCdb, destination);
                }
                catch (IOException)
                {
                    // Move never overwrites. If a file appeared at the handoff,
                    // give the caller the same decision as any other conflict.
                    SaveDestinationSnapshot current = await InspectCdbAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                    _ = RequireExpectedDestination(expected, current);
                    throw;
                }
            }
        }
        catch (IOException exception) when (displacedBackup is not null &&
            ((exception.HResult & 0xffff) == 1177 || File.Exists(displacedBackup)))
        {
            string? backupPathAfterFailure = displacedBackup;
            if (await VerifyDisplacedDestinationAsync(displacedBackup, expected).ConfigureAwait(false))
            {
                try
                {
                    // Move never overwrites a destination that appeared during the handoff.
                    File.Move(displacedBackup, destination);
                    backupPathAfterFailure = null;
                }
                catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
                {
                    // Keep the displaced original at its recorded backup path.
                }
            }

            throw new WorkspaceSaveHandoffException(saving with
            {
                IsDirty = true,
                HistoryUncertain = true,
                LastBackupPath = backupPathAfterFailure ?? session.LastBackupPath
            }, destination, stagedCdb, backupPathAfterFailure, exception);
        }
        catch
        {
            TryDeleteFile(stagedCdb);
            await PersistAsync(
                    saving with
                    {
                        Lifecycle = EditorSessionLifecycle.Ready,
                        PendingSaveAttempt = GetOutstandingSaveAttempt(attempt, stagedCdb),
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        bool destinationChangedAtCommit = false;
        string? backupPath = displacedBackup;
        if (displacedBackup is not null)
        {
            destinationChangedAtCommit = !await VerifyDisplacedDestinationAsync(displacedBackup, expected)
                .ConfigureAwait(false);
            if (!destinationChangedAtCommit && expected.Length == 0)
            {
                TryDeleteFile(displacedBackup);
                backupPath = null;
            }
            else
            {
                backupPath = ArchiveDisplacedDestination(displacedBackup);
            }
        }

        var committedSave = new WorkspaceSaveResult(
            saving with
            {
                SaveTargetCdbPath = destination,
                SaveDestinationBaseline = stagedBaseline with { Path = destination },
                IsDirty = false,
                MutationPending = false,
                HistoryUncertain = false,
                Lifecycle = EditorSessionLifecycle.Ready,
                PendingSaveAttempt = GetOutstandingSaveAttempt(attempt, stagedCdb),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastBackupPath = backupPath
            },
            backupPath)
        {
            DestinationChangedAtCommit = destinationChangedAtCommit
        };
        try
        {
            EnsureNonEmpty(destination, "saved CDB");
            EditorSessionState ready = await PersistAsync(
                    committedSave.Session,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ClearUnsynchronizedSave(session.SessionId);
            return committedSave with { Session = ready };
        }
        catch (Exception exception)
        {
            RememberUnsynchronizedSave(saving, committedSave.Session);
            throw new WorkspaceSaveCommitException(committedSave, exception);
        }
    }

    internal Action<string, string, string> ReplaceDestination { get; init; } =
        static (staged, destination, displaced) =>
            File.Replace(staged, destination, displaced, ignoreMetadataErrors: true);

    private static async Task<bool> VerifyDisplacedDestinationAsync(
        string displacedBackup,
        SaveDestinationSnapshot expected)
    {
        try
        {
            SaveDestinationSnapshot actual = await InspectCdbAsync(displacedBackup, CancellationToken.None)
                .ConfigureAwait(false);
            return HaveSameContents(expected, actual);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the displaced file and surface that its version was not verified.
            return false;
        }
    }

    private string ArchiveDisplacedDestination(string displacedBackup)
    {
        string archivePath = Path.Combine(_backupsRoot, Path.GetFileName(displacedBackup));
        try
        {
            _ = ValidateSaveDestination(archivePath);
            if (string.Equals(displacedBackup, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                return displacedBackup;
            }

            Directory.CreateDirectory(_backupsRoot);
            File.Move(displacedBackup, archivePath);
            return archivePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The atomic replacement already left a backup beside the destination.
            return displacedBackup;
        }
    }

    private async Task<EditorSessionState> TransitionAsync(
        EditorSessionState state,
        EditorSessionLifecycle lifecycle,
        CancellationToken cancellationToken) =>
        await PersistAsync(
                state with { Lifecycle = lifecycle, UpdatedAtUtc = DateTimeOffset.UtcNow },
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<EditorSessionState> PersistAsync(
        EditorSessionState state,
        CancellationToken cancellationToken)
    {
        ValidateSessionPaths(state);
        await AtomicJsonFile.WriteAsync(
                Path.Combine(state.SessionDirectory, MetadataFileName),
                state,
                cancellationToken)
            .ConfigureAwait(false);
        return state;
    }

    private Task<EditorSessionState?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        AtomicJsonFile.ReadOrCreateAsync<EditorSessionState?>(
            Path.Combine(SessionDirectory(sessionId), MetadataFileName),
            static () => null,
            cancellationToken,
            quarantineCorruptFile: false);

    private string SessionDirectory(Guid sessionId) =>
        Path.Combine(_sessionsRoot, sessionId.ToString("N"));

    private void ValidateSessionPaths(EditorSessionState state)
    {
        _ = ValidateCdbDestination(state.SourceCdbPath);
        _ = ValidateCdbDestination(state.SaveTargetCdbPath);
        var expectedDirectory = SessionDirectory(state.SessionId);
        if (!Path.GetFullPath(state.SessionDirectory).Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase)
            || !IsDirectChild(state.WorkingCdbPath, expectedDirectory)
            || !IsDirectChild(state.WorkingSqlitePath, expectedDirectory))
        {
            throw new InvalidOperationException("The session metadata contains paths outside its private directory.");
        }
    }

    private static bool IsDirectChild(string candidate, string directory) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(candidate)),
            Path.GetFullPath(directory),
            StringComparison.OrdinalIgnoreCase);

    private static string ValidateCdbInput(string path)
    {
        var fullPath = ValidateCdbDestination(path);
        EnsureNonEmpty(fullPath, "source CDB");
        return fullPath;
    }

    private static string ValidateCdbDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".cdb", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(fullPath) is null)
        {
            throw new ArgumentException("A .cdb path with a parent directory is required.", nameof(path));
        }

        return fullPath;
    }

    private string ValidateSaveDestination(string path)
    {
        string destination = ValidateCdbDestination(path);
        string physicalRoot = ResolveDirectoryLinks(_sessionsRoot);
        var file = new FileInfo(destination);
        string resolved = file.Exists && file.ResolveLinkTarget(returnFinalTarget: true) is { } target
            ? target.FullName
            : destination;
        string physicalDestination = Path.Combine(ResolveDirectoryLinks(Path.GetDirectoryName(resolved)!), Path.GetFileName(resolved));
        if (IsInsideDirectory(destination, _sessionsRoot) || IsInsideDirectory(physicalDestination, physicalRoot))
        {
            throw new ArgumentException("Choose a CDB destination outside the editor's private sessions folder.", nameof(path));
        }

        return destination;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ResolveDirectoryLinks(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is not { } parent)
        {
            return directory.FullName;
        }

        var resolved = new DirectoryInfo(Path.Combine(ResolveDirectoryLinks(parent.FullName), directory.Name));
        return resolved.Exists && resolved.ResolveLinkTarget(returnFinalTarget: true) is { } target
            ? target.FullName
            : resolved.FullName;
    }

    private static Task ValidateSqliteAsync(string path, CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ValidateSqliteCoreAsync(path, SqliteOpenMode.ReadOnly, cancellationToken),
            cancellationToken);

    private static Task<bool> ValidateRecoverySqliteAsync(
        string path, bool resume, CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(async () =>
        {
            try
            {
                await ValidateSqliteCoreAsync(path, SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
                return false;
            }
            catch (SqliteException exception) when (exception.SqliteExtendedErrorCode is 776 or 264)
            {
                // SQLITE_READONLY_ROLLBACK / SQLITE_READONLY_RECOVERY.
                // The caller holds the session lease. Listing leaves the journal for explicit resume.
                if (resume)
                {
                    await ValidateSqliteCoreAsync(path, SqliteOpenMode.ReadWrite, cancellationToken)
                        .ConfigureAwait(false);
                }
                return true;
            }
        }, cancellationToken);

    private static async Task ValidateSqliteCoreAsync(
        string path, SqliteOpenMode mode, CancellationToken cancellationToken)
    {
        EnsureNonEmpty(path, "working SQLite database");
        await using var connection = SqliteSupport.CreateConnection(path, mode);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
        // The provider retries busy statements synchronously. Check cancellation between
        // short attempts while keeping the original lock wait budget for validation.
        TimeSpan lockWait = TimeSpan.FromSeconds(command.CommandTimeout);
        command.CommandTimeout = 1;
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (SqliteException exception) when (
                (exception.SqliteErrorCode is raw.SQLITE_BUSY or raw.SQLITE_LOCKED) &&
                (cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(started) < lockWait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static FileStream OpenReadStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream? OpenDestinationIfPresent(string path)
    {
        try
        {
            return OpenReadStream(path);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<SaveDestinationSnapshot> CaptureSaveDestinationAsync(
        string path,
        FileStream? stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is null)
        {
            return new SaveDestinationSnapshot(path, Length: null, Sha256: null);
        }

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new SaveDestinationSnapshot(path, stream.Length, Convert.ToHexString(hash));
    }

    private static bool IsValidSnapshot(SaveDestinationSnapshot snapshot, string path) =>
        string.Equals(snapshot.Path, path, StringComparison.OrdinalIgnoreCase)
        && (snapshot.Length is null
            ? snapshot.Sha256 is null
            : snapshot.Length >= 0 && snapshot.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit));

    private static SaveDestinationSnapshot RequireExpectedDestination(
        SaveDestinationSnapshot? expected,
        SaveDestinationSnapshot observed)
    {
        if (expected is null || !IsValidSnapshot(expected, observed.Path))
        {
            throw new SaveDestinationConflictException(SaveDestinationConflictReason.BaselineUnknown, observed);
        }

        if (!HaveSameContents(expected, observed))
        {
            throw new SaveDestinationConflictException(SaveDestinationConflictReason.Changed, observed);
        }

        return expected;
    }

    private static bool HaveSameContents(SaveDestinationSnapshot expected, SaveDestinationSnapshot observed) =>
        expected.Length == observed.Length &&
        string.Equals(expected.Sha256, observed.Sha256, StringComparison.OrdinalIgnoreCase);

    private static (string Stage, string Backup) GetAttemptPaths(WorkspaceSaveAttempt attempt)
    {
        string folder = attempt.StagingDirectory;
        string name = Path.GetFileNameWithoutExtension(attempt.DestinationPath);
        return (Path.Combine(folder, $".{name}.{attempt.Id:N}.cdb"),
            Path.Combine(folder, $"{name}-backup-{attempt.Id:N}.cdb"));
    }

    private EditorSessionState ReconcileInterruptedSave(EditorSessionState state, bool allowDeferredCleanup)
    {
        List<WorkspaceSaveAttempt>? stillDeferred = null;
        string? backupPath = state.LastBackupPath;
        foreach (WorkspaceSaveAttempt deferred in state.DeferredSaveAttempts ?? [])
        {
            EditorSessionState result = ReconcileCurrentSaveAttempt(
                state with { PendingSaveAttempt = deferred, LastBackupPath = backupPath }, allowDeferredCleanup);
            backupPath = result.LastBackupPath;
            if (result.PendingSaveAttempt is { } pending)
            {
                (stillDeferred ??= []).Add(pending);
            }
        }

        return ReconcileCurrentSaveAttempt(
            state with { DeferredSaveAttempts = stillDeferred, LastBackupPath = backupPath }, allowDeferredCleanup);
    }

    private EditorSessionState ReconcileCurrentSaveAttempt(EditorSessionState state, bool allowDeferredCleanup)
    {
        if (state.PendingSaveAttempt is not { } attempt)
        {
            return state;
        }

        try
        {
            if (attempt.Id == Guid.Empty || !Path.IsPathFullyQualified(attempt.DestinationPath) ||
                string.IsNullOrWhiteSpace(attempt.StagingDirectory) ||
                !Path.IsPathFullyQualified(attempt.StagingDirectory))
            {
                throw new InvalidDataException("The interrupted save has invalid staging ownership.");
            }

            VerifyAttemptDirectory(attempt);
            var (stage, backup) = GetAttemptPaths(attempt);
            _ = ValidateSaveDestination(stage);
            bool foundOwnedFile = false;
            if (GetAttributesIfPresent(backup) is { } backupAttributes &&
                (backupAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            {
                state = state with { LastBackupPath = backup };
                foundOwnedFile = true;
            }
            foreach (string path in EnumerateSaveStages(stage))
            {
                if (GetAttributesIfPresent(path) is not { } attributes)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The interrupted save stage now points to another file.");
                }
                File.Delete(path);
                foundOwnedFile = true;
            }
            if (attempt.CleanupNeedsEvidence && !foundOwnedFile)
            {
                throw new IOException("The previous save's staging files could not be verified in this directory.");
            }
            return state with { PendingSaveAttempt = null };
        }
        catch (Exception exception) when (allowDeferredCleanup &&
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // Private SQLite recovery can proceed while the tracked external stage is unavailable.
            return state with { PendingSaveAttempt = MarkCleanupUnverified(attempt) };
        }
    }

    private static FileAttributes? GetAttributesIfPresent(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static WorkspaceSaveAttempt? GetOutstandingSaveAttempt(WorkspaceSaveAttempt attempt, string stage)
    {
        try
        {
            VerifyAttemptDirectory(attempt);
            return EnumerateSaveStages(stage).Any(path => GetAttributesIfPresent(path).HasValue) ? attempt : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return MarkCleanupUnverified(attempt);
        }
    }

    private static WorkspaceSaveAttempt MarkCleanupUnverified(WorkspaceSaveAttempt attempt) =>
        attempt.StagingDirectoryIdentity is null && !attempt.CleanupNeedsEvidence
            ? attempt with { CleanupNeedsEvidence = true }
            : attempt;

    private static void VerifyAttemptDirectory(WorkspaceSaveAttempt attempt)
    {
        string directory = attempt.StagingDirectory;
        // A path can name a different directory when a drive or folder has been replaced.
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0 ||
            !ResolveDirectoryLinks(directory).Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            attempt.StagingDirectoryIdentity is { } identity &&
            !string.Equals(WindowsDirectoryIdentity.Read(directory), identity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The save staging directory no longer identifies the original folder.");
        }
    }

    private void ValidateStagingDirectory(WorkspaceSaveAttempt attempt)
    {
        string destination = ValidateSaveDestination(attempt.DestinationPath);
        if (string.IsNullOrWhiteSpace(attempt.StagingDirectory) ||
            !Path.IsPathFullyQualified(attempt.StagingDirectory) ||
            !ResolveDirectoryLinks(Path.GetDirectoryName(destination)!)
                .Equals(attempt.StagingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The save destination directory has changed since staging began.");
        }
        VerifyAttemptDirectory(attempt);
    }

    private static string[] EnumerateSaveStages(string cdbStage)
    {
        string sqliteStage = Path.ChangeExtension(cdbStage, ".sqlite");
        return [cdbStage, sqliteStage, sqliteStage + "-journal", sqliteStage + "-wal", sqliteStage + "-shm"];
    }

    private string CompletedDirectory(Guid sessionId) => Path.Combine(_sessionsRoot, $".closed-{sessionId:N}");

    private string CompletionMarker(string completedPath) =>
        Path.Combine(_sessionsRoot, ".leases", Path.GetFileName(completedPath));

    private string MoveToCompletedDirectory(Guid sessionId, string sessionDirectory)
    {
        string completedPath = CompletedDirectory(sessionId);
        string marker = CompletionMarker(completedPath);
        if (Directory.Exists(completedPath) || File.Exists(completedPath))
        {
            throw new IOException("The completed session path is already occupied.");
        }

        using (new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough))
        {
            // The lease excludes other editor instances for this session ID.
        }

        try
        {
            Directory.Move(sessionDirectory, completedPath);
            return completedPath;
        }
        catch
        {
            TryDeleteFile(marker);
            throw;
        }
    }

    private async Task<bool> TryDeleteCompletedDirectoryAsync(string path)
    {
        string marker = CompletionMarker(path);
        try
        {
            if (!File.Exists(marker))
            {
                return false;
            }

            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                EditorSessionState? state = await AtomicJsonFile.ReadOrCreateAsync<EditorSessionState?>(
                    Path.Combine(path, "session.json"), static () => null, CancellationToken.None,
                    quarantineCorruptFile: false).ConfigureAwait(false);
                if (state is not null)
                {
                    ValidateSessionPaths(state);
                    if (!path.Equals(CompletedDirectory(state.SessionId), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    _ = ReconcileInterruptedSave(state, allowDeferredCleanup: false);
                }
                DeleteCompletedDirectory(path);
            }

            if (Directory.Exists(path))
            {
                return false;
            }

            TryDeleteFile(marker);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal Action<string> DeleteCompletedDirectory { get; init; } =
        static path => Directory.Delete(path, recursive: true);

    private void DeleteSessionDirectory(string sessionDirectory)
    {
        var fullPath = Path.GetFullPath(sessionDirectory);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, _sessionsRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _))
        {
            throw new InvalidOperationException("Only an exact private session directory can be removed.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static void EnsureNonEmpty(string path, string description)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new FileNotFoundException($"The {description} was not found.", path);
        }

        if (information.Length == 0)
        {
            throw new InvalidDataException($"The {description} is empty.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A unique staging remnant cannot replace or corrupt the destination.
        }
        catch (UnauthorizedAccessException)
        {
            // A unique staging remnant cannot replace or corrupt the destination.
        }
    }
}
