using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Internal;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Workspace;

public sealed class WorkspaceService : IWorkspaceService
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
        Directory.CreateDirectory(sessionDirectory);
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
            LastBackupPath: null);
        try
        {
            state = await TransitionAsync(state, EditorSessionLifecycle.CopyingSource, cancellationToken)
                .ConfigureAwait(false);
            File.Copy(source, workingCdb, overwrite: false);
            EnsureNonEmpty(workingCdb, "working CDB");
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
            return await TransitionAsync(state, EditorSessionLifecycle.Ready, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteSessionDirectory(sessionDirectory);
            throw;
        }
        catch
        {
            DeleteSessionDirectory(sessionDirectory);
            throw;
        }
    }

    public async Task<EditorSessionState> RecoverAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The recoverable editor session was not found.");
        ValidateSessionPaths(state);
        EnsureNonEmpty(state.WorkingCdbPath, "recoverable working CDB");
        await ValidateSqliteAsync(state.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
        if (!state.IsDirty)
        {
            throw new InvalidOperationException("The session has no unsaved changes to recover.");
        }

        return await PersistAsync(
                state with
                {
                    Lifecycle = EditorSessionLifecycle.Ready,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<WorkspaceSaveResult> SaveAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveCoreAsync(session, session.SaveTargetCdbPath, cancellationToken);
    }

    public Task<WorkspaceSaveResult> SaveAsAsync(
        EditorSessionState session,
        string destinationCdbPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveCoreAsync(session, destinationCdbPath, cancellationToken);
    }

    public Task<EditorSessionState> MarkDirtyAsync(
        EditorSessionState session,
        CancellationToken cancellationToken) =>
        PersistRecoveryStateAsync(session, isDirty: true, cancellationToken);

    internal Task<EditorSessionState> PersistSavedBaselineAsync(
        EditorSessionState session,
        CancellationToken cancellationToken) =>
        PersistRecoveryStateAsync(session, isDirty: false, cancellationToken);

    private Task<EditorSessionState> PersistRecoveryStateAsync(
        EditorSessionState session,
        bool isDirty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionPaths(session);
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

            var state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (state is null || !state.IsDirty)
            {
                continue;
            }

            try
            {
                ValidateSessionPaths(state);
                EnsureNonEmpty(state.WorkingCdbPath, "recoverable working CDB");
                await ValidateSqliteAsync(state.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or InvalidOperationException
                                              or SqliteException)
            {
                continue;
            }

            var description = string.Create(
                CultureInfo.InvariantCulture,
                $"Unsaved {Path.GetFileName(state.SourceCdbPath)} session from {state.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC");
            recoverable.Add(new RecoverableSession(
                state with { Lifecycle = EditorSessionLifecycle.Recoverable },
                description));
        }

        return recoverable
            .OrderByDescending(static item => item.Session.UpdatedAtUtc)
            .ToArray();
    }

    public Task DiscardRecoverableSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteSessionDirectory(SessionDirectory(sessionId));
        return Task.CompletedTask;
    }

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

            var state = await LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (state is not null
                && state.Lifecycle is EditorSessionLifecycle.Closed or EditorSessionLifecycle.Cancelled)
            {
                DeleteSessionDirectory(directory);
            }
        }
    }

    private async Task<WorkspaceSaveResult> SaveCoreAsync(
        EditorSessionState session,
        string destinationCdbPath,
        CancellationToken cancellationToken)
    {
        ValidateSessionPaths(session);
        var destination = ValidateCdbDestination(destinationCdbPath);
        EnsureNonEmpty(session.WorkingCdbPath, "working CDB");
        await ValidateSqliteAsync(session.WorkingSqlitePath, cancellationToken).ConfigureAwait(false);
        var saving = await TransitionAsync(session, EditorSessionLifecycle.Saving, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stagedCdb = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.cdb");
        string? backupPath = null;
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
            var destinationInformation = new FileInfo(destination);
            destinationInformation.Refresh();
            if (destinationInformation.Exists && destinationInformation.Length > 0)
            {
                Directory.CreateDirectory(_backupsRoot);
                backupPath = Path.Combine(
                    _backupsRoot,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Path.GetFileNameWithoutExtension(destination)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.cdb"));
                File.Copy(destination, backupPath, overwrite: false);
                EnsureNonEmpty(backupPath, "backup CDB");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The destination replacement is the non-cancellable commit point. Once it
            // succeeds, persist the matching clean session state even if the caller's
            // cancellation token is signalled concurrently.
            ReplaceAtomically(stagedCdb, destination);
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

        var committedSave = new WorkspaceSaveResult(
            saving with
            {
                SaveTargetCdbPath = destination,
                IsDirty = false,
                Lifecycle = EditorSessionLifecycle.Ready,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastBackupPath = backupPath
            },
            backupPath);
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
            cancellationToken);

    private string SessionDirectory(Guid sessionId) =>
        Path.Combine(_sessionsRoot, sessionId.ToString("N"));

    private void ValidateSessionPaths(EditorSessionState state)
    {
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
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    private static void ReplaceAtomically(string stagedPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(stagedPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(stagedPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            var rollbackPath = destinationPath + $".rollback-{Guid.NewGuid():N}";
            File.Move(destinationPath, rollbackPath);
            try
            {
                File.Move(stagedPath, destinationPath);
                TryDeleteFile(rollbackPath);
            }
            catch
            {
                if (!File.Exists(destinationPath) && File.Exists(rollbackPath))
                {
                    File.Move(rollbackPath, destinationPath);
                }

                throw;
            }
        }
    }

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
