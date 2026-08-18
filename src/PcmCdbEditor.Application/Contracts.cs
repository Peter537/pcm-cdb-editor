using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public enum ConverterFailureCategory
{
    MissingExecutable,
    InvalidInput,
    StartFailure,
    NonZeroExit,
    TimedOut,
    Cancelled,
    MissingOutput,
    EmptyOutput,
    FileSystem
}

public sealed record ConverterDiagnostics(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

public sealed record ConversionResult(string OutputPath, ConverterDiagnostics Diagnostics);

public sealed record ConversionFailure(ConverterFailureCategory Category, string Message, ConverterDiagnostics? Diagnostics);

public sealed class CdbConversionException : Exception
{
    public CdbConversionException(ConversionFailure failure, Exception? innerException = null)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).Message, innerException)
    {
        Failure = failure;
    }

    public ConversionFailure Failure { get; }
}

public interface ICdbConverter
{
    Task<ConversionResult> ExportToSqliteAsync(string workingCdbPath, TimeSpan timeout, CancellationToken cancellationToken);

    Task<ConversionResult> ImportToCdbAsync(
        string workingSqlitePath,
        string temporaryCdbDestination,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record WorkspaceOpenRequest(string SourceCdbPath);

public sealed record WorkspaceSaveResult(EditorSessionState Session, string? BackupPath);

public sealed record RecoverableSession(EditorSessionState Session, string Description);

public interface IWorkspaceService
{
    Task<EditorSessionState> OpenAsync(WorkspaceOpenRequest request, CancellationToken cancellationToken);

    Task<EditorSessionState> RecoverAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<WorkspaceSaveResult> SaveAsync(EditorSessionState session, CancellationToken cancellationToken);

    Task<WorkspaceSaveResult> SaveAsAsync(EditorSessionState session, string destinationCdbPath, CancellationToken cancellationToken);

    Task<EditorSessionState> MarkDirtyAsync(EditorSessionState session, CancellationToken cancellationToken);

    Task CloseAsync(EditorSessionState session, bool discardDirtySession, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecoverableSession>> GetRecoverableSessionsAsync(CancellationToken cancellationToken);

    Task DiscardRecoverableSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task CleanCompletedSessionsAsync(CancellationToken cancellationToken);
}

public interface ITableCatalog
{
    Task<DatabaseSchemaCatalog> DiscoverAsync(string sqlitePath, CancellationToken cancellationToken);
}

public interface ITableDataStore
{
    Task<TablePage> QueryAsync(string sqlitePath, DatabaseSchemaCatalog catalog, TableQuery query, CancellationToken cancellationToken);

    Task<TableSlice> QueryRowsAsync(string sqlitePath, DatabaseSchemaCatalog catalog, TableQuery query, CancellationToken cancellationToken);

    Task<long> CountAsync(string sqlitePath, DatabaseSchemaCatalog catalog, string tableName, FilterExpression? filter, CancellationToken cancellationToken);

    Task<long> CountAsync(string sqlitePath, DatabaseSchemaCatalog catalog, TableQuery query, CancellationToken cancellationToken);

    Task<EditResult> UpdateCellAsync(string sqlitePath, DatabaseSchemaCatalog catalog, CellUpdateOperation operation, CancellationToken cancellationToken);

    Task<EditResult> UpdateRowAsync(string sqlitePath, DatabaseSchemaCatalog catalog, RowUpdateOperation operation, CancellationToken cancellationToken);

    Task<EditResult> InsertRowAsync(string sqlitePath, DatabaseSchemaCatalog catalog, RowInsertionOperation operation, CancellationToken cancellationToken);

    Task<EditResult> DeleteRowAsync(string sqlitePath, DatabaseSchemaCatalog catalog, RowDeletionOperation operation, CancellationToken cancellationToken);
}

public interface IEditHistory
{
    EditHistoryState State { get; }

    void Record(EditOperation operation);

    void Record(EditOperation operation, IEnumerable<RowReplayGuard> undoGuards);

    EditOperation TakeUndo();

    EditOperation TakeRedo();

    EditHistoryReplay TakeUndoReplay();

    EditHistoryReplay TakeRedoReplay();

    void CompleteUndo(EditOperation operation);

    void CompleteRedo(EditOperation operation);

    void CompleteUndo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> redoGuards);

    void CompleteRedo(EditHistoryReplay replay, IEnumerable<RowReplayGuard> undoGuards);

    void RestoreFailedUndo(EditOperation operation);

    void RestoreFailedRedo(EditOperation operation);

    void RestoreFailedUndo(EditHistoryReplay replay);

    void RestoreFailedRedo(EditHistoryReplay replay);

    void MarkSavedBaseline();

    void Clear();
}

/// <summary>
/// Applies one checked-out history operation in a single database transaction.
/// Implementations validate every persisted row guard before changing data and
/// return the guards needed for the opposite replay direction.
/// </summary>
public interface IEditOperationReplayer
{
    Task<EditReplayResult> ReplayAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        EditHistoryReplay replay,
        CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<EditorPreferences> LoadPreferencesAsync(CancellationToken cancellationToken);

    Task SavePreferencesAsync(EditorPreferences preferences, CancellationToken cancellationToken);

    Task<TableViewState?> LoadTableViewStateAsync(string schemaSignature, string tableName, CancellationToken cancellationToken);

    Task SaveTableViewStateAsync(TableViewState state, CancellationToken cancellationToken);
}

public sealed record FileAssociationState(bool IsRegistered, string? ExecutablePath, string? Problem);

public interface IFileAssociationService
{
    Task<FileAssociationState> InspectAsync(CancellationToken cancellationToken);

    Task RegisterAsync(string executablePath, CancellationToken cancellationToken);

    Task RemoveAsync(CancellationToken cancellationToken);
}

public sealed record GridSelection(RowIdentity? CurrentRow, string? CurrentColumn, IReadOnlyList<RowIdentity> SelectedRows);

public sealed record GridViewport(RowIdentity? FirstVisibleRow, int HorizontalOffset);

public interface ITableGridAdapter
{
    event EventHandler<GridSelection>? SelectionChanged;

    event EventHandler<EditOperation>? EditCommitted;

    GridSelection CaptureSelection();

    GridViewport CaptureViewport();

    void Bind(TableSchema schema, IReadOnlyList<TypedRow> rows, TableViewState? state);

    void RestoreSelection(GridSelection selection);

    void RestoreViewport(GridViewport viewport);
}

public interface IRiderRecoveryService
{
    Task<MaintenanceCapability> CheckCapabilityAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<IReadOnlyList<RiderTeamOption>> ListTeamsAsync(
        string sqlitePath,
        CancellationToken cancellationToken);

    Task<RiderRecoveryPreview> PreviewAsync(
        string sqlitePath,
        RiderRecoveryTarget target,
        CancellationToken cancellationToken);

    Task<RiderRecoveryPreview> PreviewAsync(string sqlitePath, IReadOnlyCollection<long> cyclistIds, CancellationToken cancellationToken);

    Task<MaintenanceApplyResult> ApplyAsync(string sqlitePath, RiderRecoveryPreview preview, CancellationToken cancellationToken);
}

public interface IRiderCreationService
{
    Task<MaintenanceCapability> CheckCapabilityAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<RiderCreationDraft> PrepareAsync(
        string sqlitePath,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RiderLookupOption>> SearchLookupAsync(
        string sqlitePath,
        RiderLookupTarget target,
        string query,
        int maxResults,
        CancellationToken cancellationToken);

    Task<RiderCreationPreview> PreviewAsync(
        string sqlitePath,
        RiderCreationInput input,
        CancellationToken cancellationToken);

    Task<MaintenanceApplyResult> ApplyAsync(
        string sqlitePath,
        RiderCreationPreview preview,
        CancellationToken cancellationToken);
}

public interface IJanuaryFirstRepairService
{
    Task<MaintenanceCapability> CheckCapabilityAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<JanuaryFirstRepairPreview> PreviewAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<MaintenanceApplyResult> ApplyAsync(string sqlitePath, JanuaryFirstRepairPreview preview, CancellationToken cancellationToken);
}

public interface ICountryQuotaMaintenanceService
{
    Task<MaintenanceCapability> CheckCapabilityAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<CountryQuotaPreview> PreviewAsync(string sqlitePath, CancellationToken cancellationToken);

    Task<MaintenanceApplyResult> ApplyAsync(string sqlitePath, CountryQuotaPreview preview, CancellationToken cancellationToken);
}
