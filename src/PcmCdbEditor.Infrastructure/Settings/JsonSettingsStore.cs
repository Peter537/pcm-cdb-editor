using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Internal;

namespace PcmCdbEditor.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _settingsPath;

    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<EditorPreferences> LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return Normalize(document.Preferences?.ToModel() ?? CreateDefaultPreferences());
    }

    public async Task SavePreferencesAsync(
        EditorPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(
                _settingsPath,
                document with { Preferences = PreferencesDocument.From(Normalize(preferences)) },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TableViewState?> LoadTableViewStateAsync(
        string schemaSignature,
        string tableName,
        CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.TableViewStates.GetValueOrDefault(BuildKey(schemaSignature, tableName))?.ToModel();
    }

    public async Task SaveTableViewStateAsync(
        TableViewState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var states = new Dictionary<string, TableViewStateDocument>(
            document.TableViewStates,
            StringComparer.OrdinalIgnoreCase)
        {
            [BuildKey(state.SchemaSignature, state.TableName)] = TableViewStateDocument.From(state)
        };
        await AtomicJsonFile.WriteAsync(
                _settingsPath,
                document with { TableViewStates = states },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SettingsDocument> LoadAsync(CancellationToken cancellationToken)
    {
        var document = await AtomicJsonFile
            .ReadOrCreateAsync(_settingsPath, CreateDefaultDocument, cancellationToken)
            .ConfigureAwait(false);
        return document with
        {
            TableViewStates = new Dictionary<string, TableViewStateDocument>(
                document.TableViewStates ?? [],
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string BuildKey(string schemaSignature, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return $"{schemaSignature}\u001f{tableName}";
    }

    private static EditorPreferences CreateDefaultPreferences() => new(
        ApplicationTheme.System,
        GridDensity.Compact,
        pageSize: 100,
        ForeignKeyDisplayMode.RawAndName);

    private static EditorPreferences Normalize(EditorPreferences preferences)
    {
        var theme = Enum.IsDefined(preferences.Theme) ? preferences.Theme : ApplicationTheme.System;
        var density = Enum.IsDefined(preferences.Density) ? preferences.Density : GridDensity.Compact;
        var displayMode = Enum.IsDefined(preferences.ForeignKeyDisplayMode)
            ? preferences.ForeignKeyDisplayMode
            : ForeignKeyDisplayMode.RawAndName;
        var pageSize = preferences.PageSize is 100 or 250 ? preferences.PageSize : 100;
        var recentFiles = preferences.RecentFiles
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12);
        return new EditorPreferences(theme, density, pageSize, displayMode, recentFiles);
    }

    private static SettingsDocument CreateDefaultDocument() => new(
        PreferencesDocument.From(CreateDefaultPreferences()),
        new Dictionary<string, TableViewStateDocument>(StringComparer.OrdinalIgnoreCase));

    private sealed record SettingsDocument(
        PreferencesDocument? Preferences,
        Dictionary<string, TableViewStateDocument> TableViewStates);

    private sealed record PreferencesDocument(
        ApplicationTheme Theme,
        GridDensity Density,
        int PageSize,
        ForeignKeyDisplayMode ForeignKeyDisplayMode,
        string[] RecentFiles)
    {
        public static PreferencesDocument From(EditorPreferences preferences) => new(
            preferences.Theme,
            preferences.Density,
            preferences.PageSize,
            preferences.ForeignKeyDisplayMode,
            preferences.RecentFiles.ToArray());

        public EditorPreferences ToModel() => new(
            Theme,
            Density,
            PageSize,
            ForeignKeyDisplayMode,
            RecentFiles ?? []);
    }

    private sealed record TableViewStateDocument(
        string SchemaSignature,
        string TableName,
        ColumnDisplayStateDocument[] Columns,
        SortDescriptorDocument[] Sorts,
        GridDensity Density,
        int FrozenColumnCount)
    {
        public static TableViewStateDocument From(TableViewState state) => new(
            state.SchemaSignature,
            state.TableName,
            state.Columns.Select(ColumnDisplayStateDocument.From).ToArray(),
            state.Sorts.Select(SortDescriptorDocument.From).ToArray(),
            state.Density,
            state.FrozenColumnCount);

        public TableViewState ToModel() => new(
            SchemaSignature,
            TableName,
            (Columns ?? []).Select(static column => column.ToModel()),
            (Sorts ?? []).Select(static sort => sort.ToModel()),
            Density,
            FrozenColumnCount);
    }

    private sealed record ColumnDisplayStateDocument(
        string ColumnName,
        double Width,
        int DisplayIndex,
        bool IsVisible,
        bool IsFrozen)
    {
        public static ColumnDisplayStateDocument From(ColumnDisplayState state) => new(
            state.ColumnName,
            state.Width,
            state.DisplayIndex,
            state.IsVisible,
            state.IsFrozen);

        public ColumnDisplayState ToModel() => new(ColumnName, Width, DisplayIndex, IsVisible, IsFrozen);
    }

    private sealed record SortDescriptorDocument(string ColumnName, SortDirection Direction)
    {
        public static SortDescriptorDocument From(SortDescriptor sort) => new(sort.ColumnName, sort.Direction);

        public SortDescriptor ToModel() => new(ColumnName, Direction);
    }
}
