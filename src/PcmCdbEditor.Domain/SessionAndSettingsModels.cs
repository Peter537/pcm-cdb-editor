namespace PcmCdbEditor.Domain;

public enum EditorSessionLifecycle
{
    Creating,
    CopyingSource,
    Converting,
    DiscoveringSchema,
    Ready,
    Saving,
    Cancelled,
    Faulted,
    Recoverable,
    Closed
}

public sealed record EditorSessionState(
    Guid SessionId,
    string SourceCdbPath,
    string SaveTargetCdbPath,
    string SessionDirectory,
    string WorkingCdbPath,
    string WorkingSqlitePath,
    bool IsDirty,
    EditorSessionLifecycle Lifecycle,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastBackupPath,
    bool MutationPending = false,
    bool? HistoryUncertain = null)
{
    public SaveDestinationSnapshot? SaveDestinationBaseline { get; init; }
    public WorkspaceSaveAttempt? PendingSaveAttempt { get; init; }

    public IReadOnlyList<WorkspaceSaveAttempt>? DeferredSaveAttempts { get; init; }
}

public sealed record WorkspaceSaveAttempt(Guid Id, string DestinationPath, string StagingDirectory)
{
    public string? StagingDirectoryIdentity { get; init; }
    public bool CleanupNeedsEvidence { get; init; }
}

public sealed record SaveDestinationSnapshot(string Path, long? Length, string? Sha256);

public enum ApplicationTheme
{
    System,
    Light,
    Dark
}

public enum GridDensity
{
    Compact,
    Comfortable
}

public sealed record ColumnDisplayState(
    string ColumnName,
    double Width,
    int DisplayIndex,
    bool IsVisible,
    bool IsFrozen);

public sealed record TableViewState
{
    public TableViewState(
        string schemaSignature,
        string tableName,
        IEnumerable<ColumnDisplayState> columns,
        GridDensity density,
        int frozenColumnCount)
    {
        SchemaSignature = schemaSignature;
        TableName = tableName;
        Columns = ModelCollections.Freeze(columns);
        Density = density;
        FrozenColumnCount = frozenColumnCount;
    }

    public string SchemaSignature { get; }

    public string TableName { get; }

    public IReadOnlyList<ColumnDisplayState> Columns { get; }

    public GridDensity Density { get; }

    public int FrozenColumnCount { get; }
}

public sealed record EditorPreferences
{
    public EditorPreferences(
        ApplicationTheme theme,
        GridDensity density,
        int pageSize,
        ForeignKeyDisplayMode foreignKeyDisplayMode,
        IEnumerable<string>? recentFiles = null)
    {
        Theme = theme;
        Density = density;
        PageSize = pageSize;
        ForeignKeyDisplayMode = foreignKeyDisplayMode;
        RecentFiles = ModelCollections.Freeze(recentFiles);
    }

    public ApplicationTheme Theme { get; }

    public GridDensity Density { get; }

    public int PageSize { get; }

    public ForeignKeyDisplayMode ForeignKeyDisplayMode { get; }

    public IReadOnlyList<string> RecentFiles { get; }
}
