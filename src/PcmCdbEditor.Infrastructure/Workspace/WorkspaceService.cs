using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Internal;
using PcmCdbEditor.Infrastructure.Sqlite;

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
                sessionId, expectedUpdatedAtUtc, cancellationToken).ConfigureAwait(false);
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

    internal Task<EditorSessionState> PersistSavedBaselineAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        if (session.MutationPending || session.HistoryUncertain != false)
        {
            throw new InvalidOperationException("Uncertain history cannot establish a clean baseline without saving.");
        }

        return PersistRecoveryStateAsync(session, isDirty: false, cancellationToken);
    }

    private Task<EditorSessionState> PersistRecoveryStateAsync(
        EditorSessionState session,
        bool isDirty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionPaths(session);
        RequireSessionOwnership(session.SessionId);
        return PersistAsync(
            session with
            {
                IsDirty = isDirty,
                Lifecycle = EditorSessionLifecycle.Ready,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public async Task CloseAsync(
        EditorSessionState session,
        bool discardDirtySession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionPaths(session);
        RequireSessionOwnership(session.SessionId);
        if (session.IsDirty && !discardDirtySession)
        {
            throw new InvalidOperationException("The session has unsaved changes and was not approved for discard.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await PersistAsync(
                session with
                {
                    Lifecycle = EditorSessionLifecycle.Closed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken)
            .ConfigureAwait(false);
        DeleteSessionDirectory(session.SessionDirectory);
        ReleaseSession(session.SessionId);
    }

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
                await ValidateSqliteAsync(state.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);

                var description = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unsaved {Path.GetFileName(state.SourceCdbPath)} session from {state.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC");
                recoverable.Add(new RecoverableSession(
                    state with { Lifecycle = EditorSessionLifecycle.Recoverable },
                    description));
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

    public async Task DiscardRecoverableSessionAsync(
        Guid sessionId,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream ownership = AcquireSessionOwnership(sessionId);
        EditorSessionState state = await ReadReviewedRecoveryAsync(
            sessionId, expectedUpdatedAtUtc, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DeleteSessionDirectory(state.SessionDirectory);
    }

    private async Task<EditorSessionState> ReadReviewedRecoveryAsync(
        Guid sessionId,
        DateTimeOffset? expectedUpdatedAtUtc,
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
        await ValidateSqliteAsync(state.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
        if (expectedUpdatedAtUtc.HasValue &&
            await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) != state)
        {
            throw new InvalidOperationException("The working session changed since it was listed. Refresh to review it again.");
        }

        return state;
    }

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
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var sessionId))
            {
                continue;
            }

            try
            {
                using FileStream ownership = AcquireSessionOwnership(sessionId);
                EditorSessionState? state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (state is not null && state.SessionId == sessionId && !HasRecoverableWork(state)
                    && state.Lifecycle is EditorSessionLifecycle.Closed or EditorSessionLifecycle.Cancelled)
                {
                    ValidateSessionPaths(state);
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteSessionDirectory(directory);
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
        Directory.CreateDirectory(destinationDirectory);
        var saving = await TransitionAsync(session, EditorSessionLifecycle.Saving, cancellationToken)
            .ConfigureAwait(false);
        var stagedCdb = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.cdb");
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
                displacedBackup = Path.Combine(destinationDirectory, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileNameWithoutExtension(destination)}-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.cdb"));
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
        catch
        {
            TryDeleteFile(stagedCdb);
            await PersistAsync(
                    saving with
                    {
                        Lifecycle = EditorSessionLifecycle.Ready,
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
            return committedSave with { Session = ready };
        }
        catch (Exception exception)
        {
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
            () => ValidateSqliteCoreAsync(path, cancellationToken),
            cancellationToken);

    private static async Task ValidateSqliteCoreAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNonEmpty(path, "working SQLite database");
        await using var connection = SqliteSupport.CreateConnection(path, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
