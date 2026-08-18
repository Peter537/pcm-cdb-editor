using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text;
using PcmCdbEditor.Application;
using PcmCdbEditor.App.ViewModels;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.History;
using PcmCdbEditor.Infrastructure.Workspace;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using EditorApplicationTheme = PcmCdbEditor.Domain.ApplicationTheme;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private const int DefaultPageSize = 100;
    private const int SearchDelayMilliseconds = 250;

    private readonly string? _launchPath;
    private readonly IWorkspaceService _workspaceService;
    private readonly ITableCatalog _tableCatalog;
    private readonly ITableDataStore _tableDataStore;
    private readonly IRiderRecoveryService _riderRecoveryService;
    private readonly IRiderCreationService _riderCreationService;
    private readonly IJanuaryFirstRepairService _januaryFirstRepairService;
    private readonly ICountryQuotaMaintenanceService _countryQuotaMaintenanceService;
    private readonly ISettingsStore _settingsStore;
    private readonly IFileAssociationService _fileAssociationService;
    private readonly SqliteEditOperationReplayer _editOperationReplayer = new();
    private readonly ExclusiveOperationGate _operationGate = new();
    private readonly LatestRequestGate _tableLoadGate = new();
    private readonly GridContentBindingSession _gridBindingSession = new();
    private readonly MutationWriteAhead _mutationWriteAhead;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<TableListItem> _allTables = [];
    private readonly Dictionary<string, TabViewItem> _tableTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TabViewItem, CancellationTokenSource> _countCancellations = [];

    private AppWindow? _appWindow;
    private EditorSessionState? _session;
    private DatabaseSchemaCatalog? _catalog;
    private ExclusiveOperationGate.ExclusiveOperationLease? _operationLease;
    private TableLoadRequest? _activeTableLoad;
    private bool _operationMutationPrepared;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _disposed;
    private TypedRow? _selectedRow;
    private EditHistory? _editHistory;
    private EditorPreferences _preferences = new(
        EditorApplicationTheme.System,
        GridDensity.Compact,
        DefaultPageSize,
        ForeignKeyDisplayMode.RawAndName);
    private bool _suppressTableSelection;
    private bool _suppressTabSelection;
    private bool _suppressCurrentTableSearch;
    private bool _suppressPageSizeSelection;
    private NavigationViewItem? _lastContentNavigationItem;
    private Guid? _maintenanceTargetsSessionId;
    private Guid? _maintenanceTeamsSessionId;
    private long[] _selectedRecoveryRiderIds = [];
    private Guid? _riderCreationSessionId;
    private RiderCreationDraft? _riderCreationDraft;
    private RiderCreationPreview? _riderCreationPreview;
    private RiderLookupOption? _riderCreationTeam;
    private RiderLookupOption? _riderCreationRegion;
    private RiderLookupOption? _riderCreationType;
    private RiderLookupOption? _riderFavoriteRaceCandidate;
    private RiderLookupTarget? _riderTeamLookup;
    private RiderLookupTarget? _riderRegionLookup;
    private RiderLookupTarget? _riderTypeLookup;
    private RiderLookupTarget? _riderFavoriteRaceLookup;
    private readonly ObservableCollection<RiderLookupOption> _riderFavoriteRaces = [];
    private readonly RiderGameDisplayNameState _riderGameDisplayNameState = new();
    private readonly Dictionary<AutoSuggestBox, CancellationTokenSource> _riderLookupCancellations = [];
    private readonly List<RiderAbilityEditor> _riderAbilityEditors = [];
    private readonly List<RiderAdvancedFieldEditor> _riderAdvancedEditors = [];
    private readonly List<RiderAdvancedFieldEditor> _contractAdvancedEditors = [];
    private int _riderCreationStep;
    private int _riderCreationMaxVisitedStep;
    private bool _suppressRiderLookupText;
    private bool _suppressFavoriteRaceText;
    private bool _suppressRiderGameDisplayNameEvents;

    public MainWindow(
        string? launchPath,
        IWorkspaceService workspaceService,
        ITableCatalog tableCatalog,
        ITableDataStore tableDataStore,
        IRiderRecoveryService riderRecoveryService,
        IRiderCreationService riderCreationService,
        IJanuaryFirstRepairService januaryFirstRepairService,
        ICountryQuotaMaintenanceService countryQuotaMaintenanceService,
        ISettingsStore settingsStore,
        IFileAssociationService fileAssociationService)
    {
        _launchPath = launchPath;
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _tableCatalog = tableCatalog ?? throw new ArgumentNullException(nameof(tableCatalog));
        _tableDataStore = tableDataStore ?? throw new ArgumentNullException(nameof(tableDataStore));
        _riderRecoveryService = riderRecoveryService ?? throw new ArgumentNullException(nameof(riderRecoveryService));
        _riderCreationService = riderCreationService ?? throw new ArgumentNullException(nameof(riderCreationService));
        _januaryFirstRepairService = januaryFirstRepairService ?? throw new ArgumentNullException(nameof(januaryFirstRepairService));
        _countryQuotaMaintenanceService = countryQuotaMaintenanceService ??
            throw new ArgumentNullException(nameof(countryQuotaMaintenanceService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _fileAssociationService = fileAssociationService ??
            throw new ArgumentNullException(nameof(fileAssociationService));
        _mutationWriteAhead = new MutationWriteAhead(_workspaceService);

        InitializeComponent();
        RiderFavoriteRacesList.ItemsSource = _riderFavoriteRaces;
        _lastContentNavigationItem = TablesNavigationItem;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        TableGrid.SelectionChanged += TableGrid_SelectionChanged;
        TableGrid.EditCommitted += TableGrid_EditCommitted;
        TableGrid.EditRejected += TableGrid_EditRejected;
        Activated += MainWindow_Activated;
    }

    public ShellState ViewModel { get; } = new();

    private void ConfigureWindow()
    {
        IntPtr windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1360, 840));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        _appWindow.Closing += AppWindow_Closing;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        await Task.Yield();
        try
        {
            _preferences = await _settingsStore.LoadPreferencesAsync(_lifetimeCancellation.Token);
            ApplyPreferences();
            await _workspaceService.CleanCompletedSessionsAsync(_lifetimeCancellation.Token);
            bool recovered = await OfferRecoveryAsync(_lifetimeCancellation.Token);
            if (!recovered && IsCdbPath(_launchPath))
            {
                await OpenPathAsync(_launchPath!);
            }
            else if (!recovered)
            {
                ResetToNoFile();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            ResetToNoFile();
            PresentError(
                "Startup recovery was unavailable",
                SafeFailureMessage(exception, "The saved recovery information could not be inspected."));
        }
    }

    private async Task<bool> OfferRecoveryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RecoverableSession> sessions =
            await _workspaceService.GetRecoverableSessionsAsync(cancellationToken);
        for (var index = 0; index < sessions.Count; index++)
        {
            RecoverableSession recoverable = sessions[index];
            ViewModel.State = ShellOperationState.Recovery;
            ViewModel.Status = "An unsaved working session is available.";
            string additional = sessions.Count - index - 1 == 1
                ? " One older session is also available."
                : sessions.Count - index - 1 > 1
                    ? $" {sessions.Count - index - 1:N0} older sessions are also available."
                    : string.Empty;
            var dialog = CreateDialog(
                "Resume unsaved work?",
                $"{recoverable.Description}.{additional} Resume this isolated session, discard it, or leave it untouched for next time.",
                "Resume",
                "Discard",
                "Not now");
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return await RecoverSessionAsync(recoverable.Session.SessionId, cancellationToken);
            }

            if (result == ContentDialogResult.Secondary)
            {
                await _workspaceService.DiscardRecoverableSessionAsync(
                    recoverable.Session.SessionId,
                    cancellationToken);
                continue;
            }

            // The close button is the deliberate "leave untouched" path.
            return false;
        }

        return false;
    }

    private async Task<bool> RecoverSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        SetBusyPresentation(
            "Resuming working session",
            "Validating the isolated CDB and SQLite working files.",
            allowCancellation: false);
        try
        {
            EditorSessionState session = await _workspaceService.RecoverAsync(sessionId, cancellationToken);
            DatabaseSchemaCatalog catalog = await _tableCatalog.DiscoverAsync(
                session.WorkingSqlitePath,
                cancellationToken);
            await ActivateSessionAsync(session, catalog, cancellationToken);
            bool recoveredInterruptedReplay = _editHistory?.State.RecoveredInterruptedReplay == true;
            if (recoveredInterruptedReplay)
            {
                PresentWarning(
                    "Unsaved work resumed after an interrupted undo or redo",
                    "The app recovered the interrupted action. Before another undo or redo, it will check the saved rows so that a change that may already have finished is not applied twice.");
            }
            else
            {
                PresentSuccess(
                    "Unsaved work resumed",
                    "The recovered working copy is open. Save it when you are ready to safely replace the destination CDB.");
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            ResetToNoFile();
            PresentError(
                "Could not resume the session",
                SafeFailureMessage(exception, "The recoverable working files were left untouched."));
            return false;
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".cdb");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? selected = await picker.PickSingleFileAsync();
        if (selected is not null)
        {
            await OpenPathAsync(selected.Path);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        if (!ValidateOpenPath(path))
        {
            return;
        }

        if (!TryBeginOperation("Open database", out CancellationToken cancellationToken))
        {
            return;
        }

        EditorSessionState? openedSession = null;
        try
        {
            try
            {
                if (!await CloseCurrentSessionForReplacementAsync(cancellationToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!_lifetimeCancellation.IsCancellationRequested)
                {
                    PresentWarning("Open cancelled", "The current working session remains open.");
                }
                return;
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                PresentError(
                    "Could not close the current session",
                    SafeFailureMessage(exception, "The current working session remains open."));
                return;
            }

            SetBusyPresentation("Opening database", "Copying the source before conversion.");
            ViewModel.DatabaseName = Path.GetFileName(path);
            openedSession = await _workspaceService.OpenAsync(
                new WorkspaceOpenRequest(path),
                cancellationToken);
            DatabaseSchemaCatalog catalog = await _tableCatalog.DiscoverAsync(
                openedSession.WorkingSqlitePath,
                cancellationToken);
            await ActivateSessionAsync(openedSession, catalog, cancellationToken);
            PresentSuccess(
                "Working copy ready",
                "The original CDB remains untouched until you choose Save.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (openedSession is not null)
            {
                await TryCloseFailedOpenAsync(openedSession);
            }

            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                ResetToNoFile();
                PresentWarning("Open cancelled", "No source file was changed.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (openedSession is not null)
            {
                await TryCloseFailedOpenAsync(openedSession);
            }

            ResetToNoFile();
            PresentError(
                "Could not open the database",
                SafeFailureMessage(exception, "The source file was not changed."));
        }
        finally
        {
            EndOperation();
        }
    }

    private bool ValidateOpenPath(string path)
    {
        if (!IsCdbPath(path))
        {
            PresentError("Choose a CDB file", "Only files with the .cdb extension can be opened.");
            return false;
        }

        try
        {
            var information = new FileInfo(path);
            if (!information.Exists || information.Length == 0)
            {
                PresentError("Could not open the database", "The selected CDB does not exist or is empty.");
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PresentError("Could not inspect the database", "The selected CDB could not be read.");
            return false;
        }

        return true;
    }

    private async Task<bool> CloseCurrentSessionForReplacementAsync(
        CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return true;
        }

        await PersistActiveTableStateAsync(cancellationToken);

        bool discard = false;
        if (_session.IsDirty)
        {
            var dialog = CreateDialog(
                "Unsaved changes",
                "Save this working session before opening another database, discard it, or keep editing.",
                "Save",
                "Discard",
                "Keep editing");
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return false;
            }

            if (result == ContentDialogResult.Primary && !await SaveCurrentAsync(cancellationToken))
            {
                return false;
            }

            discard = result == ContentDialogResult.Secondary;
        }

        EditorSessionState session = _session;
        await _workspaceService.CloseAsync(session, discard, cancellationToken);
        ResetToNoFile();
        return true;
    }

    private async Task ActivateSessionAsync(
        EditorSessionState session,
        DatabaseSchemaCatalog catalog,
        CancellationToken cancellationToken)
    {
        DisposeTableCoordinators();
        _session = session;
        _catalog = catalog;
        ResetMaintenanceTargetsForSession(session.SessionId);
        _tableTabs.Clear();
        TableTabs.TabItems.Clear();
        ViewModel.Tabs.Clear();
        AttachEditHistory(session);
        _allTables.Clear();

        foreach (TableSchema table in catalog.Tables.OrderBy(static table => table.Name, StringComparer.OrdinalIgnoreCase))
        {
            string kindLabel = table.ObjectKind == TableObjectKind.View
                ? "View"
                : table.EditCapability == TableEditCapability.Editable
                    ? "Table"
                    : "Read only";
            _allTables.Add(new TableListItem(
                table.Name,
                kindLabel,
                table.EditCapability != TableEditCapability.Editable));
        }

        ViewModel.HasDatabase = true;
        ViewModel.DatabaseName = Path.GetFileName(session.SaveTargetCdbPath);
        ApplyTableFilter(string.Empty);
        HideEmptyState();

        if (ViewModel.Tables.Count > 0)
        {
            _suppressTableSelection = true;
            TablesList.SelectedIndex = 0;
            _suppressTableSelection = false;
            await OpenTableAsync(
                ViewModel.Tables[0].Name,
                searchText: string.Empty,
                cancellationToken,
                pageSize: _preferences.PageSize);
        }
        else
        {
            ShowEmptyState(
                "No tables found",
                "The working SQLite database contains no user tables or views.",
                allowOpen: true);
            ViewModel.TableSummary = "0 tables";
        }

        await RememberRecentFileAsync(session.SourceCdbPath, cancellationToken);
        SetReadyState();
        if (ReferenceEquals(Navigation.SelectedItem, CreateRiderNavigationItem))
        {
            await InitializeRiderCreationAsync(session, cancellationToken);
        }
    }

    private async void TablesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTableSelection || TablesList.SelectedItem is not TableListItem selected)
        {
            return;
        }

        if (_tableTabs.TryGetValue(selected.Name, out TabViewItem? existing))
        {
            if (_activeTableLoad is not null)
            {
                ShellOperationState returnState = CancelActiveTableLoad()?.ReturnState ??
                    (_session?.IsDirty == true ? ShellOperationState.Dirty : ShellOperationState.Ready);
                FinishTableLoadPresentation(returnState);
            }

            if (!ReferenceEquals(TableTabs.SelectedItem, existing))
            {
                TableTabs.SelectedItem = existing;
            }
            else
            {
                EnsureTabBound(existing);
            }
            return;
        }

        await LoadSelectedTableAsync(selected.Name, string.Empty);
    }

    private async Task LoadSelectedTableAsync(string tableName, string searchText)
    {
        if (_session is null || _catalog is null)
        {
            return;
        }

        TableLoadRequest request = BeginQuery(tableName, showLoadingSurface: true);
        try
        {
            await OpenTableAsync(
                tableName,
                searchText,
                request.Token,
                pageSize: _preferences.PageSize,
                loadRequest: request);
            request.Lease.ThrowIfSuperseded(request.Token);
        }
        catch (OperationCanceledException) when (
            request.Token.IsCancellationRequested || !request.Lease.IsCurrent)
        {
            RestorePreviousTable(request, "Table load cancelled.");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (request.Lease.IsCurrent)
            {
                RestorePreviousTable(request, "The previous table remains available.");
                PresentError(
                    "Could not load the table",
                    SafeFailureMessage(exception, "The working database was not changed."));
            }
        }
        finally
        {
            EndQuery(request);
        }
    }

    private async Task OpenTableAsync(
        string tableName,
        string searchText,
        CancellationToken cancellationToken,
        FilterExpression? filter = null,
        int pageSize = DefaultPageSize,
        long offset = 0,
        IReadOnlyList<SortDescriptor>? sorts = null,
        TableFilterDefinition? filterDefinition = null,
        TableLoadRequest? loadRequest = null)
    {
        EditorSessionState session = _session
            ?? throw new InvalidOperationException("A working session is required.");
        DatabaseSchemaCatalog catalog = _catalog
            ?? throw new InvalidOperationException("A schema catalog is required.");
        if (!catalog.TryGetTable(tableName, out TableSchema table))
        {
            throw new InvalidDataException("The selected table is no longer in the schema catalog.");
        }

        GlobalSearchRequest? search = null;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string[] eligibleColumns = table.Columns
                .Where(static column => !column.IsHidden && column.Affinity != SqliteAffinity.Blob)
                .Select(static column => column.Name)
                .ToArray();
            if (eligibleColumns.Length == 0)
            {
                throw new InvalidOperationException("This table has no columns that support text search.");
            }

            search = new GlobalSearchRequest(searchText, eligibleColumns);
        }

        _tableTabs.TryGetValue(table.Name, out TabViewItem? existingTab);
        TableTabContext? existingContext = existingTab?.Tag as TableTabContext;
        TableViewState? viewState = existingContext?.ViewState;
        if (existingContext is null)
        {
            viewState = await _settingsStore.LoadTableViewStateAsync(
                catalog.SchemaSignature,
                table.Name,
                cancellationToken);
            EnsureTableLoadCurrent(loadRequest, cancellationToken);
            if (viewState is not null &&
                (!viewState.SchemaSignature.Equals(catalog.SchemaSignature, StringComparison.Ordinal) ||
                 !viewState.TableName.Equals(table.Name, StringComparison.OrdinalIgnoreCase)))
            {
                viewState = null;
            }
        }

        IReadOnlyList<SortDescriptor> effectiveSorts =
            ForeignKeySortDescriptorMapper.Restore(
                catalog,
                table,
                _preferences.ForeignKeyDisplayMode,
                sorts ?? existingContext?.Sorts ?? viewState?.Sorts);
        var query = new TableQuery(
            table.Name,
            new PageRequest(0, VirtualTableQueryCoordinator.ChunkSize),
            sorts: effectiveSorts,
            filter: filter,
            search: search,
            foreignKeyDisplayMode: _preferences.ForeignKeyDisplayMode);
        bool canReuseCoordinator = existingContext is not null &&
            existingContext.SearchText.Equals(searchText, StringComparison.Ordinal) &&
            ReferenceEquals(existingContext.Filter, filter) &&
            existingContext.Sorts.SequenceEqual(query.Sorts) &&
            existingContext.ForeignKeyDisplayMode == _preferences.ForeignKeyDisplayMode;
        var coordinator = canReuseCoordinator
            ? existingContext!.Coordinator
            : new VirtualTableQueryCoordinator(
                _tableDataStore,
                session.WorkingSqlitePath,
                catalog,
                query);
        bool ownsCoordinator = !canReuseCoordinator;
        try
        {
            if (ownsCoordinator &&
                existingTab is not null &&
                existingContext is not null &&
                CancelVirtualCount(existingTab) &&
                existingTab.Tag is TableTabContext currentExisting &&
                ReferenceEquals(currentExisting.Coordinator, existingContext.Coordinator) &&
                currentExisting.CountState.Status == TableRowCountStatus.Loading)
            {
                existingContext = currentExisting with { CountState = TableRowCountState.Cancelled };
                existingTab.Tag = existingContext;
            }

            VirtualChunk<TypedRow> chunk = await coordinator.LoadChunkContainingAsync(
                offset,
                cancellationToken);
            EnsureTableLoadCurrent(loadRequest, cancellationToken);

            int chunkIndex = checked((int)(offset - chunk.Offset));
            TypedRow[] rows = chunk.Items.Skip(chunkIndex).Take(pageSize).ToArray();
            bool provisionalHasMore = chunkIndex + rows.Length < chunk.Items.Count ||
                chunk.Items.Count == VirtualTableQueryCoordinator.ChunkSize;
            var page = new TablePage(
                table.Name,
                new PageRequest(offset, pageSize),
                totalRows: -1,
                rows,
                provisionalHasMore);

            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            EnsureTableLoadCurrent(loadRequest, cancellationToken);

            TableRowCountState countState = canReuseCoordinator
                ? existingContext!.CountState
                : TableRowCountState.Loading;
            var context = new TableTabContext(
                table,
                page,
                searchText,
                filter,
                filterDefinition ?? existingContext?.FilterDefinition ?? TableFilterDefinition.Empty,
                query.Sorts,
                viewState,
                coordinator,
                _preferences.ForeignKeyDisplayMode,
                countState,
                IsInvalidated: false,
                Selection: existingContext?.Selection ?? new GridSelection(null, null, []),
                Viewport: existingContext?.Viewport ?? new GridViewport(null, 0));
            if (!_tableTabs.TryGetValue(table.Name, out TabViewItem? tab))
            {
                tab = new TabViewItem
                {
                    Header = table.Name,
                    IsClosable = true,
                };
                _tableTabs.Add(table.Name, tab);
                TableTabs.TabItems.Add(tab);
                ViewModel.Tabs.Add(new TableTabState(
                    table.Name,
                    "Count pending",
                    table.EditCapability != TableEditCapability.Editable));
            }

            TableTabContext? staleContext = !canReuseCoordinator
                ? tab.Tag as TableTabContext
                : null;
            tab.Tag = context;
            ownsCoordinator = false;
            staleContext?.Coordinator.Dispose();

            _suppressTabSelection = true;
            try
            {
                TableTabs.SelectedItem = tab;
            }
            finally
            {
                _suppressTabSelection = false;
            }

            EnsureTabBound(tab);
        }
        finally
        {
            if (ownsCoordinator)
            {
                coordinator.Dispose();
            }
        }
    }

    private void EnsureTabBound(TabViewItem tab)
    {
        if (tab.Tag is not TableTabContext context)
        {
            return;
        }

        bool switchingTabs = !_gridBindingSession.IsBoundTo(tab);
        TableGrid.SetDensity(_preferences.Density);
        _gridBindingSession.BindIfChanged(
            tab,
            context.Page.Rows,
            context.ViewState,
            () => TableGrid.Bind(context.Schema, context.Page.Rows, context.ViewState));
        if (switchingTabs)
        {
            TableGrid.RestoreSelection(context.Selection);
            TableGrid.RestoreViewport(context.Viewport);
        }

        SetTableLoadingSurface(isVisible: false);
        SetTableGridPresented(isPresented: true);
        EmptyState.Visibility = Visibility.Collapsed;
        BusyIndicator.Visibility = Visibility.Collapsed;
        UpdateTabChrome(tab, context);
        if (context.CountState.Status != TableRowCountStatus.Available &&
            !_countCancellations.ContainsKey(tab))
        {
            StartVirtualCount(tab, context);
        }
    }

    private void UpdateTabChrome(TabViewItem tab, TableTabContext context)
    {
        if (!ReferenceEquals(TableTabs.SelectedItem, tab))
        {
            return;
        }

        _suppressCurrentTableSearch = true;
        CurrentTableSearchBox.Text = context.SearchText;
        _suppressCurrentTableSearch = false;
        _suppressPageSizeSelection = true;
        PageSizeBox.SelectedIndex = context.Page.Request.Limit == 250 ? 1 : 0;
        _suppressPageSizeSelection = false;

        ViewModel.Status = context.Schema.EditCapability == TableEditCapability.Editable
            ? "Ready"
            : "Read-only table";
        UpdateCountAndPagingChrome(tab, context);
        ViewModel.PageSizeLabel = $"{context.Page.Request.Limit:N0} rows/page";
        FiltersButton.Content = context.FilterDefinition.RuleCount == 0
            ? "Filters"
            : $"Filters ({context.FilterDefinition.RuleCount:N0})";
        SortButton.Content = context.Sorts.Count == 0
            ? "Sort"
            : $"Sort ({context.Sorts.Count:N0})";
        SetInsertRowActionsEnabled(_operationLease is null &&
            _activeTableLoad is null &&
            context.Schema.EditCapability == TableEditCapability.Editable);
        UpdateHistoryButtons();
    }

    private void UpdateCountAndPagingChrome(TabViewItem tab, TableTabContext context)
    {
        if (!ReferenceEquals(TableTabs.SelectedItem, tab) || _activeTableLoad is not null)
        {
            return;
        }

        long first = context.Page.Rows.Count == 0 ? 0 : context.Page.Request.Offset + 1;
        long last = context.Page.Request.Offset + context.Page.Rows.Count;
        string totalLabel = context.CountState.Status switch
        {
            TableRowCountStatus.Available => $"{context.CountState.Value:N0}",
            TableRowCountStatus.Loading => "counting…",
            TableRowCountStatus.Cancelled => "count cancelled",
            TableRowCountStatus.Failed => "count unavailable",
            _ => "count unknown",
        };
        ViewModel.TableSummary = context.Page.Rows.Count == 0
            ? $"{context.Schema.Name} · 0 visible rows · {totalLabel}"
            : $"{context.Schema.Name} · {first:N0}–{last:N0} of {totalLabel} rows";
        bool canNavigatePages = _operationLease is null;
        PreviousPageButton.IsEnabled = canNavigatePages && context.Page.Request.Offset > 0;
        NextPageButton.IsEnabled = canNavigatePages && context.Page.HasMore;
        if (context.CountState.Status == TableRowCountStatus.Available &&
            context.CountState.Value.HasValue)
        {
            NextPageButton.IsEnabled = canNavigatePages &&
                context.Page.Request.Offset + context.Page.Rows.Count <
                context.CountState.Value.Value;
        }
    }

    private void StartVirtualCount(TabViewItem tab, TableTabContext context)
    {
        CancelVirtualCount(tab);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _countCancellations.Add(tab, cancellation);
        if (tab.Tag is TableTabContext current &&
            ReferenceEquals(current.Coordinator, context.Coordinator))
        {
            var loading = current with { CountState = TableRowCountState.Loading };
            tab.Tag = loading;
            UpdateTabCountLabel(loading.Schema.Name, "Count pending");
            UpdateCountAndPagingChrome(tab, loading);
        }

        _ = LoadVirtualCountAsync(tab, context.Coordinator, cancellation);
    }

    private async Task LoadVirtualCountAsync(
        TabViewItem tab,
        VirtualTableQueryCoordinator coordinator,
        CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        try
        {
            TableRowCountState countState = await coordinator.LoadCountAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (tab.Tag is TableTabContext current &&
                ReferenceEquals(current.Coordinator, coordinator) &&
                IsCurrentCount(tab, cancellation))
            {
                tab.Tag = current with { CountState = countState };
                UpdateTabCountLabel(current.Schema.Name, FormatCount(countState.Value ?? 0));
                if (ReferenceEquals(TableTabs.SelectedItem, tab))
                {
                    UpdateCountAndPagingChrome(tab, (TableTabContext)tab.Tag);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Independent lazy count cancellation does not invalidate the loaded row window.
        }
        catch (Exception)
        {
            try
            {
                if (tab.Tag is TableTabContext current &&
                    ReferenceEquals(current.Coordinator, coordinator) &&
                    IsCurrentCount(tab, cancellation))
                {
                    tab.Tag = current with
                    {
                        CountState = TableRowCountState.Failed("The row count could not be computed."),
                    };
                    UpdateTabCountLabel(current.Schema.Name, "Count unavailable");
                    if (ReferenceEquals(TableTabs.SelectedItem, tab))
                    {
                        UpdateCountAndPagingChrome(tab, (TableTabContext)tab.Tag);
                    }
                }
            }
            catch (Exception)
            {
                // Lazy count reporting is best-effort and must never fault its discarded task.
            }
        }
        finally
        {
            if (IsCurrentCount(tab, cancellation))
            {
                _countCancellations.Remove(tab);
            }
            cancellation.Dispose();
        }
    }

    private void UpdateTabCountLabel(string tableName, string countLabel)
    {
        TableTabState? tabState = ViewModel.Tabs.FirstOrDefault(item =>
            item.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (tabState is null)
        {
            return;
        }

        int index = ViewModel.Tabs.IndexOf(tabState);
        ViewModel.Tabs[index] = tabState with { CountLabel = countLabel };
    }

    private bool IsCurrentCount(TabViewItem tab, CancellationTokenSource cancellation) =>
        _countCancellations.TryGetValue(tab, out CancellationTokenSource? current) &&
        ReferenceEquals(current, cancellation);

    private bool CancelVirtualCount(TabViewItem tab)
    {
        if (_countCancellations.Remove(tab, out CancellationTokenSource? cancellation))
        {
            cancellation.Cancel();
            return true;
        }

        return false;
    }

    private void TableGrid_SelectionChanged(object? sender, GridSelection selection)
    {
        InspectorValues.ItemsSource = null;
        _selectedRow = null;
        SetSelectedRowActionsEnabled(isEnabled: false);
        _selectedRecoveryRiderIds = [];
        UseSelectedRiderRowsButton.IsEnabled = false;
        if (selection.CurrentRow is null ||
            TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            InspectorHeading.Text = "No row selected";
            return;
        }

        TypedRow? row = context.Page.Rows.FirstOrDefault(candidate =>
            candidate.Identity is not null && candidate.Identity.Equals(selection.CurrentRow));
        TypedRow[] selectedRows = context.Page.Rows
            .Where(candidate => candidate.Identity is not null &&
                selection.SelectedRows.Any(identity => candidate.Identity.Equals(identity)))
            .ToArray();
        long[] selectedCyclistIds = selectedRows
            .Select(TryReadCyclistId)
            .Where(static id => id.HasValue && id.Value > 0)
            .Select(static id => id!.Value)
            .Distinct()
            .Order()
            .ToArray();
        _selectedRecoveryRiderIds = selectedCyclistIds;
        UseSelectedRiderRowsButton.IsEnabled = RecoveryIdsModeRadioButton.IsChecked == true
            && selectedCyclistIds.Length != 0;
        if (row is null)
        {
            InspectorHeading.Text = "No row selected";
            return;
        }

        InspectorHeading.Text = context.Schema.EditCapability == TableEditCapability.Editable
            ? "Selected editable row"
            : "Selected read-only row";
        InspectorValues.ItemsSource = context.Schema.Columns
            .Where(static column => !column.IsHidden)
            .OrderBy(static column => column.Ordinal)
            .Select(column => new InspectorValueItem(
                column.Name,
                row.Values.TryGetValue(column.Name, out SqliteValue value)
                    ? FormatSqliteValue(value)
                    : "NULL"))
            .ToArray();
        _selectedRow = row;
        bool canMutate = _operationLease is null &&
            context.Schema.EditCapability == TableEditCapability.Editable &&
            row.Identity is not null;
        SetSelectedRowActionsEnabled(canMutate);
    }

    private async void TableGrid_EditCommitted(object? sender, EditOperation operation)
    {
        if (operation is not RowUpdateOperation update ||
            _session is null ||
            _catalog is null ||
            TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context ||
            !context.Schema.Name.Equals(update.TableName, StringComparison.OrdinalIgnoreCase))
        {
            PresentWarning("Inline edit unavailable", "The table changed before the cell edit could be applied.");
            return;
        }

        if (_operationLease is not null || _activeTableLoad is not null)
        {
            PresentWarning("Inline edit unavailable", "Wait for the current database operation to finish, then try again.");
            return;
        }

        TypedRow? visibleRow = context.Page.Rows.FirstOrDefault(row =>
            row.Identity is not null && row.Identity.Equals(update.Identity));
        if (visibleRow is null || visibleRow.Revision != update.ExpectedRevision)
        {
            PresentWarning("Row changed", "The visible row is stale. Reload it before editing.");
            await RefreshSelectedTableAsync();
            return;
        }

        await ExecuteEditAsync(update, async cancellationToken =>
            await _tableDataStore.UpdateRowAsync(
                _session.WorkingSqlitePath,
                _catalog,
                update,
                cancellationToken));
    }

    private void TableGrid_EditRejected(object? sender, string message) =>
        PresentInformation("Use the typed row editor", message);

    private static long? TryReadCyclistId(TypedRow row)
    {
        foreach (string columnName in new[] { "IDcyclist", "fkIDcyclist" })
        {
            if (row.Values.TryGetValue(columnName, out SqliteValue value) &&
                value.Kind == SqliteValueKind.Integer)
            {
                return value.IntegerValue;
            }
        }

        return null;
    }

    private static string FormatSqliteValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null => "NULL",
        SqliteValueKind.Integer => value.IntegerValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => value.RealValue.ToString(
            "R",
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        SqliteValueKind.Blob => $"BLOB · {GetBlobLength(value.BlobBase64):N0} bytes (read only)",
        _ => string.Empty,
    };

    private static int GetBlobLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        int padding = base64.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : base64.EndsWith('=')
                ? 1
                : 0;
        return checked((base64.Length / 4 * 3) - padding);
    }

    private static string FormatEditorValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Integer => value.IntegerValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => value.RealValue.ToString(
            "R",
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        _ => string.Empty,
    };

    private static SqliteValueKind DefaultStorageClass(ColumnSchema column) => column.Affinity switch
    {
        SqliteAffinity.Integer => SqliteValueKind.Integer,
        SqliteAffinity.Real or SqliteAffinity.Numeric => SqliteValueKind.Real,
        _ => SqliteValueKind.Text,
    };

    private async void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetEditableSelection(out TableTabContext context, out TypedRow row))
        {
            return;
        }

        ColumnSchema[] columns = context.Schema.Columns
            .Where(column =>
                !column.IsHidden &&
                !column.IsGenerated &&
                !column.IsPrimaryKey &&
                (!row.Values.TryGetValue(column.Name, out SqliteValue value) ||
                 value.Kind != SqliteValueKind.Blob))
            .OrderBy(static column => column.Ordinal)
            .ToArray();
        var editors = CreateRowEditors(columns, row.Values, allowDefault: false);
        ContentDialogResult result = await ShowRowEditorAsync("Edit row", editors, "Update");
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (!TryReadRowEditors(editors, changedOnly: true, out Dictionary<string, SqliteValue> values, out string? error))
        {
            PresentWarning("Invalid row value", error!);
            return;
        }

        if (values.Count == 0)
        {
            PresentInformation("No row changes", "Every typed value is unchanged.");
            return;
        }

        var oldValues = values.Keys.ToDictionary(
            static name => name,
            name => row.Values[name],
            StringComparer.OrdinalIgnoreCase);
        var operation = new RowUpdateOperation(
            Guid.NewGuid(),
            context.Schema.Name,
            DateTimeOffset.UtcNow,
            row.Identity!,
            oldValues,
            values,
            row.Revision);
        await ExecuteEditAsync(operation, async cancellationToken =>
            await _tableDataStore.UpdateRowAsync(
                _session!.WorkingSqlitePath,
                _catalog!,
                operation,
                cancellationToken));
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetEditableSelection(out TableTabContext context, out TypedRow row))
        {
            return;
        }

        var dialog = CreateDialog(
            "Delete selected row?",
            "This changes only the isolated working SQLite database until Save. The complete typed row is retained for Undo.",
            "Delete",
            string.Empty,
            "Cancel");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var operation = new RowDeletionOperation(
            Guid.NewGuid(),
            context.Schema.Name,
            DateTimeOffset.UtcNow,
            row);
        await ExecuteEditAsync(operation, async cancellationToken =>
            await _tableDataStore.DeleteRowAsync(
                _session!.WorkingSqlitePath,
                _catalog!,
                operation,
                cancellationToken));
    }

    private async void InsertRow_Click(object sender, RoutedEventArgs e)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context ||
            context.Schema.EditCapability != TableEditCapability.Editable)
        {
            PresentWarning("Insert unavailable", "Choose an editable table first.");
            return;
        }

        ColumnSchema[] columns = context.Schema.Columns
            .Where(static column => !column.IsHidden && !column.IsGenerated)
            .OrderBy(static column => column.Ordinal)
            .ToArray();
        var editors = CreateRowEditors(columns, values: null, allowDefault: true);
        ContentDialogResult result = await ShowRowEditorAsync("Insert row", editors, "Insert");
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (!TryReadRowEditors(editors, changedOnly: false, out Dictionary<string, SqliteValue> values, out string? error))
        {
            PresentWarning("Invalid row value", error!);
            return;
        }

        var operation = new RowInsertionOperation(
            Guid.NewGuid(),
            context.Schema.Name,
            DateTimeOffset.UtcNow,
            values);
        await ExecuteEditAsync(operation, async cancellationToken =>
            await _tableDataStore.InsertRowAsync(
                _session!.WorkingSqlitePath,
                _catalog!,
                operation,
                cancellationToken));
    }

    private bool TryGetEditableSelection(out TableTabContext context, out TypedRow row)
    {
        if (TableTabs.SelectedItem is TabViewItem tab &&
            tab.Tag is TableTabContext selectedContext &&
            selectedContext.Schema.EditCapability == TableEditCapability.Editable &&
            _selectedRow?.Identity is not null)
        {
            context = selectedContext;
            row = _selectedRow;
            return true;
        }

        context = null!;
        row = null!;
        PresentWarning("Row action unavailable", "Select an identified row from an editable table.");
        return false;
    }

    private static List<RowValueEditor> CreateRowEditors(
        ColumnSchema[] columns,
        IReadOnlyDictionary<string, SqliteValue>? values,
        bool allowDefault)
    {
        var editors = new List<RowValueEditor>(columns.Length);
        foreach (ColumnSchema column in columns)
        {
            SqliteValue existing = SqliteValue.Null;
            bool hasValue = values is not null && values.TryGetValue(column.Name, out existing);
            SqliteValue value = hasValue
                ? existing
                : column.IsNullable
                    ? SqliteValue.Null
                    : SqliteValue.Text(string.Empty);
            if (value.Kind == SqliteValueKind.Blob)
            {
                continue;
            }

            bool canUseDefault = allowDefault &&
                (column.DefaultExpression is not null ||
                 column.IsPrimaryKey && column.Affinity == SqliteAffinity.Integer);
            SqliteValueKind initialStorageClass = hasValue &&
                (value.Kind is SqliteValueKind.Integer or SqliteValueKind.Real or SqliteValueKind.Text)
                    ? value.Kind
                    : DefaultStorageClass(column);
            var textBox = new TextBox
            {
                Header = "Value",
                Text = value.Kind == SqliteValueKind.Null || value.Kind == SqliteValueKind.Text && !hasValue
                    ? string.Empty
                    : FormatEditorValue(value),
                IsEnabled = value.Kind != SqliteValueKind.Null && !canUseDefault,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(textBox, $"{column.Name} value");
            var storageClassPicker = new ComboBox
            {
                Header = "Storage class",
                ItemsSource = new[]
                {
                    SqliteValueKind.Integer,
                    SqliteValueKind.Real,
                    SqliteValueKind.Text,
                },
                SelectedItem = initialStorageClass,
                IsEnabled = value.Kind != SqliteValueKind.Null && !canUseDefault,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(storageClassPicker, $"{column.Name} storage class");
            var nullBox = new CheckBox
            {
                Content = "NULL",
                IsChecked = value.Kind == SqliteValueKind.Null,
                IsEnabled = column.IsNullable,
            };
            AutomationProperties.SetName(nullBox, $"Set {column.Name} to NULL");
            var defaultBox = new CheckBox
            {
                Content = "Use database default",
                IsChecked = canUseDefault,
                IsEnabled = canUseDefault,
            };
            AutomationProperties.SetName(defaultBox, $"Use database default for {column.Name}");
            void RefreshEditorAvailability()
            {
                bool acceptsValue = nullBox.IsChecked != true && defaultBox.IsChecked != true;
                textBox.IsEnabled = acceptsValue;
                storageClassPicker.IsEnabled = acceptsValue;
            }

            nullBox.Checked += (_, _) => RefreshEditorAvailability();
            nullBox.Unchecked += (_, _) => RefreshEditorAvailability();
            defaultBox.Checked += (_, _) =>
            {
                nullBox.IsChecked = false;
                RefreshEditorAvailability();
            };
            defaultBox.Unchecked += (_, _) => RefreshEditorAvailability();
            editors.Add(new RowValueEditor(
                column,
                hasValue ? value : null,
                textBox,
                storageClassPicker,
                nullBox,
                defaultBox));
        }

        return editors;
    }

    private async Task<ContentDialogResult> ShowRowEditorAsync(
        string title,
        IReadOnlyList<RowValueEditor> editors,
        string primaryButtonText)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose the SQLite storage class explicitly. Existing BLOB values remain read-only.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        foreach (RowValueEditor editor in editors)
        {
            var columnEditor = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(0, 4, 0, 8),
            };
            columnEditor.Children.Add(new TextBlock
            {
                Text = editor.Column.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            columnEditor.Children.Add(new TextBlock
            {
                Text = $"Declared affinity: {editor.Column.Affinity}",
                FontSize = 12,
                Opacity = 0.72,
            });
            var valueFields = new Grid { ColumnSpacing = 8 };
            valueFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            valueFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(editor.StorageClassPicker, 0);
            Grid.SetColumn(editor.TextBox, 1);
            valueFields.Children.Add(editor.StorageClassPicker);
            valueFields.Children.Add(editor.TextBox);
            columnEditor.Children.Add(valueFields);
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            options.Children.Add(editor.NullBox);
            if (editor.UseDefaultBox.IsEnabled)
            {
                options.Children.Add(editor.UseDefaultBox);
            }
            columnEditor.Children.Add(options);
            panel.Children.Add(columnEditor);
        }

        var scroll = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 520,
            Width = 520,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = title,
            Content = scroll,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync();
    }

    private static bool TryReadRowEditors(
        IReadOnlyList<RowValueEditor> editors,
        bool changedOnly,
        out Dictionary<string, SqliteValue> values,
        out string? error)
    {
        values = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
        foreach (RowValueEditor editor in editors)
        {
            if (editor.UseDefaultBox.IsChecked == true)
            {
                continue;
            }

            if (editor.NullBox.IsChecked == true)
            {
                if (!editor.Column.IsNullable)
                {
                    error = $"{editor.Column.Name} cannot be NULL.";
                    return false;
                }

                if (!changedOnly || editor.OriginalValue is null || editor.OriginalValue.Value.Kind != SqliteValueKind.Null)
                {
                    values[editor.Column.Name] = SqliteValue.Null;
                }
                continue;
            }

            string text = editor.TextBox.Text;
            if (editor.StorageClassPicker.SelectedItem is not SqliteValueKind storageClass)
            {
                error = $"Choose a storage class for {editor.Column.Name}.";
                return false;
            }

            if (changedOnly && editor.OriginalValue is SqliteValue original &&
                original.Kind == storageClass &&
                FormatEditorValue(original).Equals(text, StringComparison.Ordinal))
            {
                continue;
            }

            SqliteValue parsed;
            switch (storageClass)
            {
                case SqliteValueKind.Integer when long.TryParse(
                    text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long integer):
                    parsed = SqliteValue.Integer(integer);
                    break;
                case SqliteValueKind.Real when double.TryParse(
                        text,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double real) &&
                    double.IsFinite(real):
                    parsed = SqliteValue.Real(real);
                    break;
                case SqliteValueKind.Text:
                    parsed = SqliteValue.Text(text);
                    break;
                default:
                    error = storageClass == SqliteValueKind.Integer
                        ? $"{editor.Column.Name} needs a valid invariant whole number."
                        : $"{editor.Column.Name} needs a finite invariant real number.";
                    return false;
            }

            if (!changedOnly || editor.OriginalValue is not SqliteValue originalValue || !parsed.Equals(originalValue))
            {
                values[editor.Column.Name] = parsed;
            }
        }

        error = null;
        return true;
    }

    private async Task ExecuteEditAsync(
        EditOperation operation,
        Func<CancellationToken, Task<EditResult>> apply)
    {
        if (_session is null || _catalog is null)
        {
            return;
        }

        if (!TryBeginOperation("Row change", out CancellationToken cancellationToken))
        {
            return;
        }

        bool mutationCommitted = false;
        try
        {
            await PrepareMutationWriteAheadAsync(_session);
            EditResult result = await apply(cancellationToken);
            if (result.AffectedRows != 1)
            {
                throw new InvalidDataException("The row operation did not affect exactly one row.");
            }

            mutationCommitted = true;
            InvalidateTableCaches();
            RecordCommittedEdit(operation, result);

            UpdateHistoryButtons();
            await RefreshSelectedTableAsync();
            PresentSuccess("Working copy updated", "One row changed in the isolated database. Save to replace the CDB destination.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mutationCommitted)
            {
                await HandlePostCommitFailureAsync("The row changed, but the app could not update the session's recovery information.");
            }
            else if (_operationMutationPrepared)
            {
                await HandleUnconfirmedMutationCancellationAsync(
                    "Row operation cancelled",
                    "The app could not confirm whether the database transaction finished. It kept the working session for recovery so that a change that finished late is not overlooked. Review the working copy before making more changes.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (mutationCommitted)
            {
                await HandlePostCommitFailureAsync("The row changed, but the app could not update the session's recovery information.");
            }
            else if (_operationMutationPrepared)
            {
                await HandlePreparedMutationFailureAsync(
                    "Could not change the row",
                    SafeFailureMessage(
                        exception,
                        "The app could not confirm whether the transaction finished. You can still recover this session; review the working copy before trying again."));
            }
            else
            {
                PresentError(
                    "Could not change the row",
                    SafeFailureMessage(exception, "The working row was not changed."));
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private void RecordCommittedEdit(EditOperation operation, EditResult result)
    {
        EditHistory history = _editHistory
            ?? throw new InvalidOperationException("The active session has no edit history.");
        switch (operation)
        {
            case CellUpdateOperation or RowUpdateOperation:
            {
                TypedRow current = result.CurrentRow
                    ?? throw new InvalidDataException("An updated row was not read back for guarded history.");
                history.Record(operation, [RowReplayGuard.Present(operation.TableName, current)]);
                break;
            }

            case RowInsertionOperation insertion:
            {
                TypedRow inserted = result.CurrentRow
                    ?? throw new InvalidDataException("An inserted row was not read back for guarded history.");
                var recordedInsertion = new RowInsertionOperation(
                    insertion.OperationId,
                    insertion.TableName,
                    insertion.CreatedAtUtc,
                    insertion.Values,
                    inserted.Identity,
                    inserted);
                history.Record(recordedInsertion, [RowReplayGuard.Present(insertion.TableName, inserted)]);
                break;
            }

            case RowDeletionOperation deletion:
            {
                TypedRow deleted = result.CurrentRow ?? deletion.DeletedRow;
                RowIdentity identity = deleted.Identity
                    ?? throw new InvalidDataException("A deleted row had no stable identity for guarded history.");
                var recordedDeletion = new RowDeletionOperation(
                    deletion.OperationId,
                    deletion.TableName,
                    deletion.CreatedAtUtc,
                    deleted);
                history.Record(recordedDeletion, [RowReplayGuard.Absent(deletion.TableName, identity)]);
                break;
            }

            default:
                throw new NotSupportedException(
                    $"Edit history does not support operation type '{operation.GetType().Name}'.");
        }
    }

    private sealed record RowValueEditor(
        ColumnSchema Column,
        SqliteValue? OriginalValue,
        TextBox TextBox,
        ComboBox StorageClassPicker,
        CheckBox NullBox,
        CheckBox UseDefaultBox);

    private sealed record RiderAbilityEditor(
        RiderAbilityDefinition Definition,
        NumberBox Current,
        NumberBox Limit,
        TextBlock Warning);

    private sealed class RiderAdvancedFieldEditor
    {
        public RiderAdvancedFieldEditor(
            RiderCreationField field,
            FrameworkElement valueEditor,
            CheckBox? nullBox,
            CheckBox? useDefaultBox)
        {
            Field = field;
            ValueEditor = valueEditor;
            NullBox = nullBox;
            UseDefaultBox = useDefaultBox;
        }

        public RiderCreationField Field { get; }

        public FrameworkElement ValueEditor { get; }

        public CheckBox? NullBox { get; }

        public CheckBox? UseDefaultBox { get; }

        public RiderLookupOption? SelectedLookup { get; set; }

        public CancellationTokenSource? SearchCancellation { get; set; }
    }

    private sealed record RiderRoleOption(RiderContractRole Role, string Label)
    {
        public override string ToString() => $"{Label} · {(int)Role}";
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        await ReplayEditAsync(isUndo: true);
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        await ReplayEditAsync(isUndo: false);
    }

    private async Task ReplayEditAsync(bool isUndo)
    {
        if (_session is null || _catalog is null || _editHistory is null || ViewModel.IsBusy)
        {
            return;
        }

        if (!TryBeginOperation(isUndo ? "Undo" : "Redo", out CancellationToken cancellationToken))
        {
            UpdateHistoryButtons();
            return;
        }

        EditHistoryReplay pending;
        try
        {
            pending = isUndo ? _editHistory.TakeUndoReplay() : _editHistory.TakeRedoReplay();
        }
        catch (InvalidOperationException)
        {
            UpdateHistoryButtons();
            EndOperation();
            return;
        }

        bool replayCommitted = false;
        try
        {
            await PrepareMutationWriteAheadAsync(_session);
            EditReplayResult result = await _editOperationReplayer.ReplayAsync(
                _session.WorkingSqlitePath,
                _catalog,
                pending,
                cancellationToken);
            replayCommitted = true;
            InvalidateTableCaches();
            if (isUndo)
            {
                _editHistory.CompleteUndo(pending, result.OppositeGuards);
            }
            else
            {
                _editHistory.CompleteRedo(pending, result.OppositeGuards);
            }

            await RefreshSelectedTableAsync();
            await ReconcileSessionWithHistoryBaselineAsync();
            string affectedRowLabel = result.AffectedRows == 1 ? "row" : "rows";
            PresentSuccess(
                isUndo ? "Change undone" : "Change redone",
                $"The app updated {result.AffectedRows:N0} {affectedRowLabel} in the working copy as one transaction.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (replayCommitted)
            {
                await HandlePostCommitFailureAsync("The undo or redo finished, but the app could not save its recovery information.");
            }
            else if (_operationMutationPrepared)
            {
                RestoreFailedReplay(pending, isUndo);
                await HandleUnconfirmedMutationCancellationAsync(
                    "History replay cancelled",
                    "The app could not confirm whether the undo or redo finished. Reload before trying again. The app will check the saved rows first so that it does not apply a completed change twice.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (replayCommitted)
            {
                await HandlePostCommitFailureAsync("The undo or redo finished, but the app could not save its recovery information.");
            }
            else if (_operationMutationPrepared)
            {
                RestoreFailedReplay(pending, isUndo);
                await HandlePreparedMutationFailureAsync(
                    isUndo ? "Could not undo the change" : "Could not redo the change",
                    SafeFailureMessage(
                        exception,
                        "The change remains available in Undo or Redo, and you can still recover this session."));
            }
            else
            {
                RestoreFailedReplay(pending, isUndo);
                PresentError(
                    isUndo ? "Could not undo the change" : "Could not redo the change",
                    SafeFailureMessage(exception, "The history entry remains available."));
            }
        }
        finally
        {
            UpdateHistoryButtons();
            EndOperation();
        }
    }

    private void RestoreFailedReplay(EditHistoryReplay replay, bool isUndo)
    {
        if (isUndo)
        {
            _editHistory!.RestoreFailedUndo(replay);
        }
        else
        {
            _editHistory!.RestoreFailedRedo(replay);
        }
    }

    private void UpdateHistoryButtons()
    {
        EditHistoryState? state = _editHistory?.State;
        UndoButton.IsEnabled = state?.CanUndo == true && !ViewModel.IsBusy && _operationLease is null;
        RedoButton.IsEnabled = state?.CanRedo == true && !ViewModel.IsBusy && _operationLease is null;
    }

    private void AttachEditHistory(EditorSessionState session)
    {
        string historyPath = Path.Combine(session.SessionDirectory, "edit-history.json");
        _editHistory = new EditHistory(historyPath);
        UpdateHistoryButtons();
    }

    private void DetachEditHistory()
    {
        _editHistory = null;
        UpdateHistoryButtons();
    }

    private bool TryMarkHistorySavedBaseline()
    {
        try
        {
            _editHistory?.MarkSavedBaseline();
            return true;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return false;
        }
    }

    private async Task ReconcileSessionWithHistoryBaselineAsync()
    {
        if (_operationLease is null)
        {
            throw new InvalidOperationException("History baseline reconciliation requires an exclusive operation lease.");
        }

        EditorSessionState currentSession = _session
            ?? throw new InvalidOperationException("The active working session closed during history replay.");
        EditHistoryState historyState = _editHistory?.State
            ?? throw new InvalidOperationException("The active session has no edit history.");
        if (historyState.HasPendingReplay)
        {
            throw new InvalidOperationException("History baseline reconciliation cannot run during a pending replay.");
        }

        if (historyState.IsDirty)
        {
            if (!currentSession.IsDirty)
            {
                throw new InvalidDataException("The workspace lost its dirty recovery marker during history replay.");
            }

            ViewModel.State = ShellOperationState.Dirty;
            return;
        }

        if (_workspaceService is not WorkspaceService workspaceService)
        {
            throw new InvalidOperationException(
                "The workspace implementation cannot persist a clean history baseline.");
        }

        EditorSessionState persisted = await workspaceService.PersistSavedBaselineAsync(
            currentSession,
            CancellationToken.None);
        if (persisted.SessionId != currentSession.SessionId ||
            !persisted.WorkingSqlitePath.Equals(
                currentSession.WorkingSqlitePath,
                StringComparison.OrdinalIgnoreCase) ||
            persisted.IsDirty)
        {
            throw new InvalidDataException("The workspace did not persist the clean history baseline.");
        }

        _session = persisted;
        ViewModel.State = ShellOperationState.Ready;
    }

    private async Task PrepareMutationWriteAheadAsync(EditorSessionState expectedSession)
    {
        if (_operationLease is null)
        {
            throw new InvalidOperationException("A database mutation requires an exclusive operation lease.");
        }

        EditorSessionState currentSession = _session
            ?? throw new InvalidOperationException("The active working session closed before the mutation boundary.");
        if (currentSession.SessionId != expectedSession.SessionId ||
            !currentSession.WorkingSqlitePath.Equals(
                expectedSession.WorkingSqlitePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active working session changed before the mutation boundary.");
        }

        _session = currentSession with { IsDirty = true };
        ViewModel.State = ShellOperationState.Dirty;
        _session = await _mutationWriteAhead.PrepareAsync(currentSession);
        _operationMutationPrepared = true;
    }

    private void InvalidateTableCaches()
    {
        TableTabContext[] contexts = _tableTabs.Values
            .Select(static tab => tab.Tag)
            .OfType<TableTabContext>()
            .ToArray();
        foreach (VirtualTableQueryCoordinator coordinator in contexts
                     .Select(static context => context.Coordinator)
                     .Distinct())
        {
            try
            {
                coordinator.Invalidate();
            }
            catch (ObjectDisposedException)
            {
                // A tab closed between snapshot and invalidation has no reusable cache.
            }
        }

        foreach (TabViewItem tab in _tableTabs.Values)
        {
            if (tab.Tag is TableTabContext context)
            {
                tab.Tag = context with
                {
                    CountState = TableRowCountState.Unknown,
                    IsInvalidated = true,
                };
            }
        }

        for (var index = 0; index < ViewModel.Tabs.Count; index++)
        {
            ViewModel.Tabs[index] = ViewModel.Tabs[index] with { CountLabel = "Count pending" };
        }
    }

    private async Task HandlePostCommitFailureAsync(string message)
    {
        ViewModel.State = ShellOperationState.Dirty;
        InvalidateTableCaches();
        try
        {
            await RefreshSelectedTableAsync();
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            // Keep the critical recovery message; refresh failure must not hide the committed mutation.
        }

        PresentError(
            "Working copy changed; save before closing",
            $"{message} Keep this window open and use Save or Save As before closing.");
    }

    private async Task HandleUnconfirmedMutationCancellationAsync(string title, string message)
    {
        ViewModel.State = ShellOperationState.Dirty;
        InvalidateTableCaches();
        try
        {
            await RefreshSelectedTableAsync();
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            // The uncertainty warning is more important than a secondary refresh failure.
        }

        PresentWarning(title, message);
    }

    private async Task HandlePreparedMutationFailureAsync(string title, string message)
    {
        ViewModel.State = ShellOperationState.Dirty;
        InvalidateTableCaches();
        try
        {
            await RefreshSelectedTableAsync();
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            // Preserve the mutation/recovery warning when a secondary refresh also fails.
        }

        PresentError(title, message);
    }

    private async void CurrentTableSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressCurrentTableSearch ||
            args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            return;
        }

        TableLoadRequest request = BeginQuery(context.Schema.Name, showLoadingSurface: false);
        try
        {
            await Task.Delay(SearchDelayMilliseconds, request.Token);
            request.Lease.ThrowIfSuperseded(request.Token);
            ShowTableLoading(request, context.Schema.Name);
            await OpenTableAsync(
                context.Schema.Name,
                sender.Text,
                request.Token,
                context.Filter,
                context.Page.Request.Limit,
                offset: 0,
                sorts: context.Sorts,
                filterDefinition: context.FilterDefinition,
                loadRequest: request);
        }
        catch (OperationCanceledException) when (
            request.Token.IsCancellationRequested || !request.Lease.IsCurrent)
        {
            // A later keystroke or session operation superseded this query.
            RestorePreviousTable(request, "Search cancelled.");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (request.Lease.IsCurrent)
            {
                RestorePreviousTable(request, "The previous table remains available.");
                PresentError(
                    "Could not search the table",
                    SafeFailureMessage(exception, "The working database was not changed."));
            }
        }
        finally
        {
            EndQuery(request);
        }
    }

    private void TableSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyTableFilter(sender.Text);
    }

    private async void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPageSizeSelection ||
            TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            return;
        }

        int pageSize = PageSizeBox.SelectedIndex == 1 ? 250 : DefaultPageSize;
        _preferences = new EditorPreferences(
            _preferences.Theme,
            _preferences.Density,
            pageSize,
            _preferences.ForeignKeyDisplayMode,
            _preferences.RecentFiles);
        try
        {
            await _settingsStore.SavePreferencesAsync(_preferences, _lifetimeCancellation.Token);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentWarning(
                "Page-size preference was not saved",
                SafeFailureMessage(exception, "The current table can still be reloaded."));
        }
        await LoadTableQueryAsync(context, context.SearchText, context.Filter, pageSize);
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab || tab.Tag is not TableTabContext context)
        {
            return;
        }

        long offset = Math.Max(0, context.Page.Request.Offset - context.Page.Request.Limit);
        await LoadTableQueryAsync(
            context,
            context.SearchText,
            context.Filter,
            context.Page.Request.Limit,
            offset);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context ||
            (!context.Page.HasMore &&
             context.CountState.Status != TableRowCountStatus.Available))
        {
            return;
        }

        if (context.CountState.Status == TableRowCountStatus.Available &&
            context.CountState.Value.HasValue &&
            context.Page.Request.Offset + context.Page.Rows.Count >= context.CountState.Value.Value)
        {
            return;
        }

        long offset = context.Page.Request.Offset + context.Page.Rows.Count;
        await LoadTableQueryAsync(
            context,
            context.SearchText,
            context.Filter,
            context.Page.Request.Limit,
            offset);
    }

    private async void Filters_Click(object sender, RoutedEventArgs e)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            PresentWarning("Choose a table", "Open a table before applying a filter.");
            return;
        }

        ColumnSchema[] columns = context.Schema.Columns
            .Where(static column => !column.IsHidden && column.Affinity != SqliteAffinity.Blob)
            .OrderBy(static column => column.Ordinal)
            .ToArray();
        if (columns.Length == 0)
        {
            PresentWarning("Filter unavailable", "This table has no columns supported by typed filters.");
            return;
        }

        TableFilterDialogOutcome? outcome = await ShowTableFilterDialogAsync(context, columns);
        if (outcome is null)
        {
            return;
        }

        await LoadTableQueryAsync(
            context,
            context.SearchText,
            outcome.Filter,
            context.Page.Request.Limit,
            filterDefinition: outcome.Definition);
    }

    private async Task<TableFilterDialogOutcome?> ShowTableFilterDialogAsync(
        TableTabContext context,
        ColumnSchema[] columns)
    {
        var quickRows = new List<FilterRuleEditor>();
        var advancedRows = new List<FilterRuleEditor>();
        var quickRulesPanel = new StackPanel { Spacing = 6 };
        var advancedRulesPanel = new StackPanel { Spacing = 6 };
        var quickEmptyText = new TextBlock
        {
            Text = "No quick rules. Add one to require another condition with AND.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var advancedEmptyText = new TextBlock
        {
            Text = "No advanced rules. Add rules, then combine their numbers in the expression.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var expressionBox = new TextBox
        {
            Header = "Advanced expression",
            PlaceholderText = "Example: 1 AND (2 OR 3)",
            Text = context.FilterDefinition.AdvancedExpression,
            IsSpellCheckEnabled = false,
        };
        AutomationProperties.SetName(expressionBox, "Advanced filter expression");
        AutomationProperties.SetHelpText(
            expressionBox,
            "Use numbered rules, AND, OR, and parentheses. AND is evaluated before OR.");

        var validationText = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(validationText, "Filter validation error");
        AutomationProperties.SetLiveSetting(validationText, AutomationLiveSetting.Assertive);

        void RefreshQuickRules()
        {
            quickRulesPanel.Children.Clear();
            for (var index = 0; index < quickRows.Count; index++)
            {
                FilterRuleEditor editor = quickRows[index];
                SetFilterRuleLabel(editor, $"Quick rule {index + 1}");
                quickRulesPanel.Children.Add(editor.Root);
            }

            quickEmptyText.Visibility = quickRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void AddQuickRule(FilterRuleDraft draft, bool moveFocus)
        {
            FilterRuleEditor editor = CreateFilterRuleEditor(columns, draft, number: 0);
            editor.RemoveButton.Click += (_, _) =>
            {
                quickRows.Remove(editor);
                RefreshQuickRules();
            };
            quickRows.Add(editor);
            RefreshQuickRules();
            if (moveFocus)
            {
                editor.ColumnPicker.Focus(FocusState.Programmatic);
            }
        }

        void RefreshAdvancedRules()
        {
            advancedRulesPanel.Children.Clear();
            foreach (FilterRuleEditor editor in advancedRows)
            {
                SetFilterRuleLabel(editor, $"Rule {editor.Number}");
                advancedRulesPanel.Children.Add(editor.Root);
            }

            advancedEmptyText.Visibility = advancedRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void AddAdvancedRule(FilterRuleDraft draft, bool moveFocus)
        {
            FilterRuleEditor editor = CreateFilterRuleEditor(columns, draft, draft.Number);
            editor.RemoveButton.Click += (_, _) =>
            {
                advancedRows.Remove(editor);
                RefreshAdvancedRules();
            };
            advancedRows.Add(editor);
            RefreshAdvancedRules();
            if (moveFocus)
            {
                editor.ColumnPicker.Focus(FocusState.Programmatic);
            }
        }

        foreach (FilterRuleDraft rule in context.FilterDefinition.QuickRules)
        {
            AddQuickRule(rule, moveFocus: false);
        }

        foreach (FilterRuleDraft rule in context.FilterDefinition.AdvancedRules)
        {
            AddAdvancedRule(rule, moveFocus: false);
        }

        RefreshQuickRules();
        RefreshAdvancedRules();

        var addQuickButton = new Button { Content = "Add quick rule" };
        addQuickButton.Click += (_, _) => AddQuickRule(
            new FilterRuleDraft(0, columns[0].Name, FilterOperator.Contains, string.Empty),
            moveFocus: true);
        AutomationProperties.SetHelpText(
            addQuickButton,
            "Quick rules are combined with each other and the advanced expression using AND.");
        var addAdvancedButton = new Button { Content = "Add advanced rule" };
        addAdvancedButton.Click += (_, _) =>
        {
            int nextNumber = advancedRows.Count == 0
                ? 1
                : checked(advancedRows.Max(static row => row.Number) + 1);
            AddAdvancedRule(
                new FilterRuleDraft(
                    nextNumber,
                    columns[0].Name,
                    FilterOperator.Contains,
                    string.Empty),
                moveFocus: true);
            expressionBox.Text = string.IsNullOrWhiteSpace(expressionBox.Text)
                ? nextNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{expressionBox.Text.Trim()} AND {nextNumber}";
        };
        AutomationProperties.SetHelpText(
            addAdvancedButton,
            "Adds a numbered rule that can be referenced in the advanced expression.");

        var content = new StackPanel { Spacing = 12, Width = 640 };
        content.Children.Add(new TextBlock
        {
            Text = "Quick rules always use AND. Advanced rules accept AND, OR, and parentheses; AND runs first.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(CreateDialogSectionHeading("Quick rules"));
        content.Children.Add(quickEmptyText);
        content.Children.Add(quickRulesPanel);
        content.Children.Add(addQuickButton);
        content.Children.Add(CreateDialogSectionHeading("Advanced rules"));
        content.Children.Add(advancedEmptyText);
        content.Children.Add(advancedRulesPanel);
        content.Children.Add(addAdvancedButton);
        content.Children.Add(expressionBox);
        content.Children.Add(validationText);
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Filter current table",
            Content = scrollViewer,
            PrimaryButtonText = "Apply",
            SecondaryButtonText = "Clear filters",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        TableFilterDialogOutcome? outcome = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                FilterRuleDraft[] quickDrafts = quickRows
                    .Select(static editor => editor.ToDraft(number: 0))
                    .ToArray();
                FilterCondition[] quickConditions = quickRows
                    .Select(editor => BuildFilterCondition(editor, columns))
                    .ToArray();
                FilterRuleDraft[] advancedDrafts = advancedRows
                    .Select(static editor => editor.ToDraft(editor.Number))
                    .ToArray();
                NumberedFilterRule[] advancedRules = advancedRows
                    .Select(editor => new NumberedFilterRule(
                        editor.Number,
                        BuildFilterCondition(editor, columns)))
                    .ToArray();
                FilterExpression? advancedFilter = null;
                if (advancedRules.Length > 0)
                {
                    advancedFilter = AdvancedFilterExpressionParser.Parse(
                        expressionBox.Text,
                        advancedRules);
                }
                else if (!string.IsNullOrWhiteSpace(expressionBox.Text))
                {
                    throw new FilterParseException(
                        "Remove the advanced expression or add the numbered rules it references.");
                }

                var definition = new TableFilterDefinition(
                    quickDrafts,
                    advancedDrafts,
                    expressionBox.Text.Trim());
                outcome = new TableFilterDialogOutcome(
                    definition,
                    CombineWithAnd(quickConditions, advancedFilter));
                validationText.Text = string.Empty;
                validationText.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) when (exception is FilterParseException or InvalidDataException)
            {
                args.Cancel = true;
                validationText.Text = $"Check the filter: {exception.Message}";
                validationText.Visibility = Visibility.Visible;
                AutomationProperties.SetName(
                    validationText,
                    $"Filter validation error: {exception.Message}");
            }
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => outcome,
            ContentDialogResult.Secondary => new TableFilterDialogOutcome(
                TableFilterDefinition.Empty,
                Filter: null),
            _ => null,
        };
    }

    private static FilterRuleEditor CreateFilterRuleEditor(
        ColumnSchema[] columns,
        FilterRuleDraft draft,
        int number)
    {
        string[] columnNames = columns.Select(static column => column.Name).ToArray();
        int columnIndex = Array.FindIndex(columnNames, name =>
            name.Equals(draft.ColumnName, StringComparison.OrdinalIgnoreCase));
        var columnPicker = new ComboBox
        {
            Header = "Column",
            ItemsSource = columnNames,
            SelectedIndex = Math.Max(0, columnIndex),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var operatorPicker = new ComboBox
        {
            Header = "Operator",
            DisplayMemberPath = nameof(FilterOperatorChoice.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var valueBox = new TextBox
        {
            Header = "Value",
            Text = draft.Value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsSpellCheckEnabled = false,
        };
        var removeButton = new Button
        {
            Content = "Remove",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(removeButton, 1);
        heading.Children.Add(label);
        heading.Children.Add(removeButton);

        var fields = new Grid { ColumnSpacing = 8 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(operatorPicker, 1);
        Grid.SetColumn(valueBox, 2);
        fields.Children.Add(columnPicker);
        fields.Children.Add(operatorPicker);
        fields.Children.Add(valueBox);

        var root = new Grid
        {
            Padding = new Thickness(8),
            RowSpacing = 4,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(fields, 1);
        root.Children.Add(heading);
        root.Children.Add(fields);

        var editor = new FilterRuleEditor(
            number,
            root,
            label,
            columnPicker,
            operatorPicker,
            valueBox,
            removeButton);

        void UpdateValueState()
        {
            if (operatorPicker.SelectedItem is not FilterOperatorChoice choice)
            {
                return;
            }

            bool needsValue = choice.Operator is not (FilterOperator.IsNull or FilterOperator.IsNotNull);
            valueBox.IsEnabled = needsValue;
            valueBox.PlaceholderText = choice.Operator is
                FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith
                    ? "Literal text; %, _ and \\ are not wildcards"
                    : "Invariant typed value";
        }

        void UpdateOperators(FilterOperator preferred)
        {
            ColumnSchema selectedColumn = columns[Math.Max(0, columnPicker.SelectedIndex)];
            IReadOnlyList<FilterOperatorChoice> choices = GetFilterOperatorChoices(selectedColumn);
            operatorPicker.ItemsSource = choices;
            int selectedIndex = choices
                .Select(static choice => choice.Operator)
                .ToList()
                .IndexOf(preferred);
            if (selectedIndex < 0)
            {
                selectedIndex = choices
                    .Select(static choice => choice.Operator)
                    .ToList()
                    .IndexOf(FilterOperator.Equals);
            }

            operatorPicker.SelectedIndex = Math.Max(0, selectedIndex);
            UpdateValueState();
        }

        columnPicker.SelectionChanged += (_, _) =>
        {
            FilterOperator preferred = operatorPicker.SelectedItem is FilterOperatorChoice current
                ? current.Operator
                : FilterOperator.Contains;
            UpdateOperators(preferred);
        };
        operatorPicker.SelectionChanged += (_, _) => UpdateValueState();
        UpdateOperators(draft.Operator);
        SetFilterRuleLabel(editor, number > 0 ? $"Rule {number}" : "Quick rule");
        return editor;
    }

    private static List<FilterOperatorChoice> GetFilterOperatorChoices(ColumnSchema column)
    {
        var choices = new List<FilterOperatorChoice>
        {
            new(FilterOperator.Contains, "Contains"),
            new(FilterOperator.StartsWith, "Starts with"),
            new(FilterOperator.EndsWith, "Ends with"),
            new(FilterOperator.Equals, "Equals"),
            new(FilterOperator.NotEquals, "Not equal"),
        };
        if (column.Affinity is SqliteAffinity.Integer or SqliteAffinity.Real or SqliteAffinity.Numeric)
        {
            choices.Add(new FilterOperatorChoice(FilterOperator.GreaterThan, "Greater than"));
            choices.Add(new FilterOperatorChoice(FilterOperator.GreaterThanOrEqual, "Greater than or equal"));
            choices.Add(new FilterOperatorChoice(FilterOperator.LessThan, "Less than"));
            choices.Add(new FilterOperatorChoice(FilterOperator.LessThanOrEqual, "Less than or equal"));
        }

        choices.Add(new FilterOperatorChoice(FilterOperator.IsNull, "Is NULL"));
        choices.Add(new FilterOperatorChoice(FilterOperator.IsNotNull, "Is not NULL"));
        return choices;
    }

    private static void SetFilterRuleLabel(FilterRuleEditor editor, string label)
    {
        editor.Label.Text = label;
        AutomationProperties.SetName(editor.ColumnPicker, $"{label} column");
        AutomationProperties.SetName(editor.OperatorPicker, $"{label} operator");
        AutomationProperties.SetName(editor.ValueBox, $"{label} value");
        AutomationProperties.SetHelpText(
            editor.ValueBox,
            "Pattern operators treat percent, underscore, and backslash as literal characters.");
        AutomationProperties.SetName(editor.RemoveButton, $"Remove {label.ToLowerInvariant()}");
    }

    private static FilterCondition BuildFilterCondition(
        FilterRuleEditor editor,
        ColumnSchema[] columns)
    {
        if (editor.ColumnPicker.SelectedItem is not string columnName ||
            editor.OperatorPicker.SelectedItem is not FilterOperatorChoice operatorChoice)
        {
            throw new FilterParseException($"{editor.Label.Text} is incomplete.");
        }

        ColumnSchema column = columns.First(candidate =>
            candidate.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (!TryParseFilterValue(column, operatorChoice.Operator, editor.ValueBox.Text, out SqliteValue value))
        {
            editor.ValueBox.Focus(FocusState.Programmatic);
            throw new FilterParseException(
                $"{editor.Label.Text} needs an invariant value compatible with {column.Affinity} affinity.");
        }

        return new FilterCondition(column.Name, operatorChoice.Operator, value);
    }

    private static FilterExpression? CombineWithAnd(
        IEnumerable<FilterCondition> quickConditions,
        FilterExpression? advancedFilter)
    {
        var expressions = quickConditions.Cast<FilterExpression>().ToList();
        if (advancedFilter is not null)
        {
            expressions.Add(advancedFilter);
        }

        return expressions.Count switch
        {
            0 => null,
            1 => expressions[0],
            _ => new FilterGroup(FilterGroupOperator.And, expressions),
        };
    }

    private static TextBlock CreateDialogSectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            PresentWarning("Choose a table", "Open a table before applying a sort.");
            return;
        }

        DatabaseSchemaCatalog catalog = _catalog
            ?? throw new InvalidOperationException("A schema catalog is required.");
        TableSortOption[] options = ForeignKeySortDescriptorMapper.GetOptions(
            catalog,
            context.Schema,
            context.ForeignKeyDisplayMode);
        if (options.Length == 0)
        {
            PresentWarning("Sort unavailable", "This table has no visible columns to sort.");
            return;
        }

        IReadOnlyList<SortDescriptor>? sorts = await ShowTableSortDialogAsync(context, options);
        if (sorts is null)
        {
            return;
        }

        await LoadTableQueryAsync(
            context,
            context.SearchText,
            context.Filter,
            context.Page.Request.Limit,
            offset: 0,
            sorts);
    }

    private async Task<IReadOnlyList<SortDescriptor>?> ShowTableSortDialogAsync(
        TableTabContext context,
        TableSortOption[] options)
    {
        var sortRows = new List<SortRuleEditor>();
        var sortRowsPanel = new StackPanel { Spacing = 6 };
        var validationText = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(validationText, "Sort validation error");
        AutomationProperties.SetLiveSetting(validationText, AutomationLiveSetting.Assertive);
        var addSortButton = new Button { Content = "Add sort column" };

        void RefreshSortRows()
        {
            sortRowsPanel.Children.Clear();
            for (var index = 0; index < sortRows.Count; index++)
            {
                SortRuleEditor editor = sortRows[index];
                string priorityLabel = $"Priority {index + 1}";
                editor.PriorityLabel.Text = priorityLabel;
                AutomationProperties.SetName(editor.ColumnPicker, $"{priorityLabel} column");
                AutomationProperties.SetName(editor.DirectionPicker, $"{priorityLabel} direction");
                AutomationProperties.SetName(editor.MoveUpButton, $"Move {priorityLabel.ToLowerInvariant()} up");
                AutomationProperties.SetName(editor.MoveDownButton, $"Move {priorityLabel.ToLowerInvariant()} down");
                AutomationProperties.SetName(editor.RemoveButton, $"Remove {priorityLabel.ToLowerInvariant()}");
                editor.MoveUpButton.IsEnabled = index > 0;
                editor.MoveDownButton.IsEnabled = index < sortRows.Count - 1;
                sortRowsPanel.Children.Add(editor.Root);
            }

            addSortButton.IsEnabled = sortRows.Count < options.Length;
        }

        void AddSortRule(SortDescriptor? descriptor = null, bool moveFocus = true)
        {
            string selectedColumn = descriptor?.ColumnName ?? options.First(option =>
                !sortRows.Any(row =>
                    row.ColumnPicker.SelectedItem is TableSortOption selected &&
                    selected.DescriptorColumnName.Equals(
                        option.DescriptorColumnName,
                        StringComparison.OrdinalIgnoreCase))).DescriptorColumnName;
            SortRuleEditor editor = CreateSortRuleEditor(
                options,
                selectedColumn,
                descriptor?.Direction ?? SortDirection.Ascending);
            editor.RemoveButton.Click += (_, _) =>
            {
                sortRows.Remove(editor);
                RefreshSortRows();
            };
            editor.MoveUpButton.Click += (_, _) =>
            {
                int index = sortRows.IndexOf(editor);
                if (index > 0)
                {
                    sortRows.RemoveAt(index);
                    sortRows.Insert(index - 1, editor);
                    RefreshSortRows();
                    editor.ColumnPicker.Focus(FocusState.Programmatic);
                }
            };
            editor.MoveDownButton.Click += (_, _) =>
            {
                int index = sortRows.IndexOf(editor);
                if (index >= 0 && index < sortRows.Count - 1)
                {
                    sortRows.RemoveAt(index);
                    sortRows.Insert(index + 1, editor);
                    RefreshSortRows();
                    editor.ColumnPicker.Focus(FocusState.Programmatic);
                }
            };
            editor.ColumnPicker.SelectionChanged += (_, _) => RefreshSortRows();
            sortRows.Add(editor);
            RefreshSortRows();
            if (moveFocus)
            {
                editor.ColumnPicker.Focus(FocusState.Programmatic);
            }
        }

        foreach (SortDescriptor descriptor in context.Sorts)
        {
            AddSortRule(descriptor, moveFocus: false);
        }

        if (sortRows.Count == 0)
        {
            AddSortRule(moveFocus: false);
        }

        addSortButton.Click += (_, _) => AddSortRule();
        AutomationProperties.SetHelpText(
            addSortButton,
            "Adds the next sort priority. Duplicate columns are rejected when the sort is applied.");

        var content = new StackPanel { Spacing = 12, Width = 620 };
        content.Children.Add(new TextBlock
        {
            Text = "Priority runs from top to bottom. Stable identity columns are appended automatically for deterministic paging.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(sortRowsPanel);
        content.Children.Add(addSortButton);
        content.Children.Add(validationText);
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            MaxHeight = 540,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Sort current table",
            Content = scrollViewer,
            PrimaryButtonText = "Apply",
            SecondaryButtonText = "Clear sorts",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        IReadOnlyList<SortDescriptor>? acceptedSorts = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            TableSortOption[] selectedOptions = sortRows
                .Select(static editor => (TableSortOption)editor.ColumnPicker.SelectedItem)
                .ToArray();
            TableSortOption? duplicate = selectedOptions
                .GroupBy(
                    static option => option.DescriptorColumnName,
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1)
                ?.First();
            if (duplicate is not null)
            {
                args.Cancel = true;
                validationText.Text =
                    $"Choose {duplicate.Label} only once, then use the arrow buttons to set its priority.";
                validationText.Visibility = Visibility.Visible;
                AutomationProperties.SetName(
                    validationText,
                    $"Sort validation error: {validationText.Text}");
                sortRows.First(row =>
                    row.ColumnPicker.SelectedItem is TableSortOption selected &&
                    selected.DescriptorColumnName.Equals(
                        duplicate.DescriptorColumnName,
                        StringComparison.OrdinalIgnoreCase)).ColumnPicker.Focus(FocusState.Programmatic);
                return;
            }

            acceptedSorts = sortRows
                .Select(static editor => new SortDescriptor(
                    ((TableSortOption)editor.ColumnPicker.SelectedItem).DescriptorColumnName,
                    editor.DirectionPicker.SelectedIndex == 1
                        ? SortDirection.Descending
                        : SortDirection.Ascending))
                .ToArray();
            validationText.Text = string.Empty;
            validationText.Visibility = Visibility.Collapsed;
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => acceptedSorts,
            ContentDialogResult.Secondary => [],
            _ => null,
        };
    }

    private static SortRuleEditor CreateSortRuleEditor(
        TableSortOption[] options,
        string selectedColumn,
        SortDirection direction)
    {
        int selectedIndex = Array.FindIndex(options, option =>
            option.DescriptorColumnName.Equals(selectedColumn, StringComparison.OrdinalIgnoreCase));
        var priorityLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var moveUpButton = new Button
        {
            Content = "Up",
            Width = 52,
            Height = 32,
        };
        var moveDownButton = new Button
        {
            Content = "Down",
            Width = 60,
            Height = 32,
        };
        ToolTipService.SetToolTip(moveUpButton, "Move up");
        ToolTipService.SetToolTip(moveDownButton, "Move down");
        var removeButton = new Button
        {
            Content = "Remove",
            Height = 32,
        };
        var heading = new Grid { ColumnSpacing = 4 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(moveUpButton, 1);
        Grid.SetColumn(moveDownButton, 2);
        Grid.SetColumn(removeButton, 3);
        heading.Children.Add(priorityLabel);
        heading.Children.Add(moveUpButton);
        heading.Children.Add(moveDownButton);
        heading.Children.Add(removeButton);

        var columnPicker = new ComboBox
        {
            Header = "Column",
            ItemsSource = options,
            DisplayMemberPath = nameof(TableSortOption.Label),
            SelectedIndex = Math.Max(0, selectedIndex),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var directionPicker = new ComboBox
        {
            Header = "Direction",
            ItemsSource = new[] { "Ascending", "Descending" },
            SelectedIndex = direction == SortDirection.Descending ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fields = new Grid { ColumnSpacing = 8 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(directionPicker, 1);
        fields.Children.Add(columnPicker);
        fields.Children.Add(directionPicker);

        var root = new Grid { Padding = new Thickness(8), RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(fields, 1);
        root.Children.Add(heading);
        root.Children.Add(fields);
        return new SortRuleEditor(
            root,
            priorityLabel,
            columnPicker,
            directionPicker,
            moveUpButton,
            moveDownButton,
            removeButton);
    }

    private async Task LoadTableQueryAsync(
        TableTabContext context,
        string searchText,
        FilterExpression? filter,
        int pageSize,
        long offset = 0,
        IReadOnlyList<SortDescriptor>? sorts = null,
        TableFilterDefinition? filterDefinition = null)
    {
        TableLoadRequest request = BeginQuery(context.Schema.Name, showLoadingSurface: true);
        try
        {
            await OpenTableAsync(
                context.Schema.Name,
                searchText,
                request.Token,
                filter,
                pageSize,
                offset,
                sorts ?? context.Sorts,
                filterDefinition ?? context.FilterDefinition,
                loadRequest: request);
        }
        catch (OperationCanceledException) when (
            request.Token.IsCancellationRequested || !request.Lease.IsCurrent)
        {
            // A newer table query replaced this request.
            RestorePreviousTable(request, "Table query cancelled.");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (request.Lease.IsCurrent)
            {
                RestorePreviousTable(request, "The previous table remains available.");
                PresentError(
                    "Could not update the table query",
                    SafeFailureMessage(exception, "The working database was not changed."));
            }
        }
        finally
        {
            EndQuery(request);
        }
    }

    private static bool TryParseFilterValue(
        ColumnSchema column,
        FilterOperator filterOperator,
        string text,
        out SqliteValue value)
    {
        if (filterOperator is FilterOperator.IsNull or FilterOperator.IsNotNull)
        {
            value = SqliteValue.Null;
            return true;
        }

        if (filterOperator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith)
        {
            value = SqliteValue.Text(text);
            return true;
        }

        switch (column.Affinity)
        {
            case SqliteAffinity.Integer when long.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long integer):
                value = SqliteValue.Integer(integer);
                return true;
            case SqliteAffinity.Real or SqliteAffinity.Numeric when double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double real) && double.IsFinite(real):
                value = SqliteValue.Real(real);
                return true;
            case SqliteAffinity.Text:
                value = SqliteValue.Text(text);
                return true;
            default:
                value = SqliteValue.Null;
                return false;
        }
    }

    private void ApplyTableFilter(string text)
    {
        string? selectedName = (TablesList.SelectedItem as TableListItem)?.Name;
        if (selectedName is null &&
            TableTabs.SelectedItem is TabViewItem selectedTab &&
            selectedTab.Tag is TableTabContext selectedContext)
        {
            selectedName = selectedContext.Schema.Name;
        }

        _suppressTableSelection = true;
        ViewModel.Tables.Clear();
        foreach (TableListItem table in _allTables.Where(table =>
                     string.IsNullOrWhiteSpace(text) ||
                     table.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Tables.Add(table);
        }

        TablesList.SelectedItem = selectedName is null
            ? null
            : ViewModel.Tables.FirstOrDefault(table =>
                table.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        _suppressTableSelection = false;
        NoTablesState.Visibility = ViewModel.Tables.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTablesTitle.Text = ViewModel.HasDatabase ? "No matching tables" : "No tables loaded";
        NoTablesDescription.Text = ViewModel.HasDatabase
            ? "Clear the table search to show every table and view."
            : "Open a CDB file first.";
        AutomationProperties.SetName(NoTablesState, NoTablesTitle.Text);
    }

    private async void TableTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabSelection)
        {
            return;
        }

        if (_activeTableLoad is not null)
        {
            ShellOperationState returnState = CancelActiveTableLoad()?.ReturnState ??
                (_session?.IsDirty == true ? ShellOperationState.Dirty : ShellOperationState.Ready);
            FinishTableLoadPresentation(returnState);
        }

        Guid? sessionId = _session?.SessionId;
        TableTabContext? stateToPersist = null;
        if (e.RemovedItems.FirstOrDefault() is TabViewItem previous)
        {
            CaptureCurrentTabState(previous);
            stateToPersist = previous.Tag as TableTabContext;
        }

        TabViewItem? selected = TableTabs.SelectedItem as TabViewItem;
        if (selected is not null)
        {
            if (selected.Tag is TableTabContext context &&
                (context.IsInvalidated ||
                 context.ForeignKeyDisplayMode != _preferences.ForeignKeyDisplayMode))
            {
                await LoadTableQueryAsync(
                    context,
                    context.SearchText,
                    context.Filter,
                    context.Page.Request.Limit,
                    context.Page.Request.Offset,
                    context.Sorts,
                    context.FilterDefinition);
            }
            else
            {
                EnsureTabBound(selected);
            }
        }

        SynchronizeTableSelectionToTab(TableTabs.SelectedItem as TabViewItem);

        await PersistCapturedTableStateAsync(
            stateToPersist,
            sessionId,
            _lifetimeCancellation.Token);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentAsync();
    }

    private async Task<bool> SaveCurrentAsync(CancellationToken? ownedOperationToken = null)
    {
        if (_session is null || ViewModel.IsBusy)
        {
            PresentWarning("Save is unavailable", "No ready working session is open.");
            return false;
        }

        bool ownsOperation = !ownedOperationToken.HasValue;
        CancellationToken cancellationToken;
        if (ownsOperation)
        {
            if (!TryBeginOperation("Save", out cancellationToken))
            {
                return false;
            }
        }
        else
        {
            if (_operationLease is null)
            {
                throw new InvalidOperationException("A nested save requires the caller's exclusive operation lease.");
            }

            cancellationToken = ownedOperationToken!.Value;
        }

        ViewModel.State = ShellOperationState.Saving;
        ViewModel.Status = "Saving database…";
        BusyIndicator.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;
        PresentInformation("Saving database", "Converting to a staged CDB before the destination is replaced.");
        try
        {
            WorkspaceSaveResult result = await _workspaceService.SaveAsync(_session, cancellationToken);
            _session = result.Session;
            bool historyBaselineSaved = TryMarkHistorySavedBaseline();
            SetReadyState();
            if (historyBaselineSaved)
            {
                PresentSuccess(
                    "Database saved",
                    result.BackupPath is null
                        ? "The destination was replaced from a validated staged CDB."
                        : "The destination was replaced and the previous CDB was backed up.");
            }
            else
            {
                PresentWarning(
                    "Database saved; reopen before editing",
                    "The destination was replaced safely, but the app could not mark the current Undo history as saved. Reopen the database before making more edits.");
            }
            return true;
        }
        catch (WorkspaceSaveCommitException exception)
        {
            await AdoptCommittedSaveAfterMetadataFailureAsync(
                exception.CommittedSave,
                rememberDestination: false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreSessionState();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                PresentWarning("Save cancelled", "The destination was not replaced.");
            }

            return false;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            RestoreSessionState();
            PresentError(
                "Could not save the database",
                SafeFailureMessage(exception, "The destination was not replaced."));
            return false;
        }
        finally
        {
            BusyIndicator.Visibility = Visibility.Collapsed;
            if (ownsOperation)
            {
                EndOperation();
            }
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || ViewModel.IsBusy)
        {
            PresentWarning("Save as is unavailable", "No ready working session is open.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_session.SaveTargetCdbPath),
            DefaultFileExtension = ".cdb",
        };
        picker.FileTypeChoices.Add("PCM database", [".cdb"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? selected = await picker.PickSaveFileAsync();
        if (selected is null)
        {
            return;
        }

        if (!TryBeginOperation("Save as", out CancellationToken cancellationToken))
        {
            return;
        }

        ViewModel.State = ShellOperationState.Saving;
        ViewModel.Status = "Saving a new CDB…";
        BusyIndicator.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;
        PresentInformation("Saving database as", "Converting to a staged CDB before the chosen destination is replaced.");
        try
        {
            WorkspaceSaveResult result = await _workspaceService.SaveAsAsync(
                _session,
                selected.Path,
                cancellationToken);
            _session = result.Session;
            bool historyBaselineSaved = TryMarkHistorySavedBaseline();
            ViewModel.DatabaseName = Path.GetFileName(result.Session.SaveTargetCdbPath);
            await RememberRecentFileAsync(
                result.Session.SaveTargetCdbPath,
                CancellationToken.None);
            SetReadyState();
            if (historyBaselineSaved)
            {
                PresentSuccess(
                    "Database saved as",
                    result.BackupPath is null
                        ? "The new destination was created from a validated staged CDB."
                        : "The destination was replaced and its previous CDB was backed up.");
            }
            else
            {
                PresentWarning(
                    "Database saved as; reopen before editing",
                    "The chosen destination was replaced safely, but the app could not mark the current Undo history as saved. Reopen the database before making more edits.");
            }
        }
        catch (WorkspaceSaveCommitException exception)
        {
            await AdoptCommittedSaveAfterMetadataFailureAsync(
                exception.CommittedSave,
                rememberDestination: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreSessionState();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                PresentWarning("Save as cancelled", "The chosen destination was not replaced.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            RestoreSessionState();
            PresentError(
                "Could not save the database as",
                SafeFailureMessage(exception, "The chosen destination was not replaced."));
        }
        finally
        {
            BusyIndicator.Visibility = Visibility.Collapsed;
            EndOperation();
        }
    }

    private async Task AdoptCommittedSaveAfterMetadataFailureAsync(
        WorkspaceSaveResult committedSave,
        bool rememberDestination)
    {
        _session = committedSave.Session;
        bool historyBaselineSaved = TryMarkHistorySavedBaseline();
        if (rememberDestination)
        {
            ViewModel.DatabaseName = Path.GetFileName(committedSave.Session.SaveTargetCdbPath);
            await RememberRecentFileAsync(
                committedSave.Session.SaveTargetCdbPath,
                CancellationToken.None);
        }

        SetReadyState();
        if (historyBaselineSaved)
        {
            PresentWarning(
                "Destination saved; reopen before editing",
                "The destination was saved, but the app could not update the saved session information. Reopen the saved CDB before making more edits.");
        }
        else
        {
            PresentWarning(
                "Destination saved; reopen before editing",
                "The destination was saved, but the app could not update the saved session information or mark the current Undo history as saved. Reopen the saved CDB before making more edits.");
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            Dispose();
            return;
        }

        if (_operationGate.IsActive)
        {
            args.Cancel = true;
            PresentWarning(
                "Close is unavailable",
                "Wait for the current database operation to finish, or cancel it before closing.");
            return;
        }

        if (_session is null)
        {
            Dispose();
            return;
        }

        args.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        if (!TryBeginOperation("Close", out CancellationToken cancellationToken))
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            await PersistActiveTableStateAsync(cancellationToken);
            bool discard = false;
            if (_session.IsDirty)
            {
                var dialog = CreateDialog(
                    "Save changes before closing?",
                    "This isolated working session has unsaved database changes.",
                    "Save and close",
                    "Discard and close",
                    "Keep editing");
                ContentDialogResult result = await dialog.ShowAsync();
                if (result == ContentDialogResult.None)
                {
                    return;
                }

                if (result == ContentDialogResult.Primary && !await SaveCurrentAsync(cancellationToken))
                {
                    return;
                }

                discard = result == ContentDialogResult.Secondary;
            }

            EditorSessionState session = _session;
            await _workspaceService.CloseAsync(session, discard, cancellationToken);
            _session = null;
            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Process shutdown owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError(
                "Could not close the working session",
                SafeFailureMessage(exception, "The window remains open so the session is not lost."));
        }
        finally
        {
            _closeInProgress = false;
            EndOperation();
        }
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsInspectorOpen = false;
        RowInspectorToggle.IsChecked = false;
        ApplyInspectorPresentation(Workspace.ActualWidth);
    }

    private void RowInspectorToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsInspectorOpen = RowInspectorToggle.IsChecked == true;
        ApplyInspectorPresentation(Workspace.ActualWidth);
    }

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 980)
        {
            Navigation.IsPaneOpen = false;
        }

        ApplyInspectorPresentation(e.NewSize.Width);

        Navigation.PaneDisplayMode = e.NewSize.Width < 720
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.LeftCompact;
    }

    private void ApplyInspectorPresentation(double workspaceWidth)
    {
        bool isVisible = ViewModel.IsInspectorOpen && workspaceWidth >= 980;
        Inspector.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = isVisible ? new GridLength(300) : new GridLength(0);
        AutomationProperties.SetName(
            RowInspectorToggle,
            ViewModel.IsInspectorOpen ? "Hide row inspector" : "Show row inspector");
    }

    private async void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            try
            {
                await ShowSettingsAsync();
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                PresentError(
                    "Settings were unavailable",
                    SafeFailureMessage(exception, "No database row was changed."));
            }
            finally
            {
                RestoreContentNavigationSelection();
            }
            return;
        }

        if (args.SelectedItem is NavigationViewItem selectedNavigationItem)
        {
            _lastContentNavigationItem = selectedNavigationItem;
        }

        string? tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        TablesView.Visibility = tag is null or "tables" ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceView.Visibility = tag == "maintenance" ? Visibility.Visible : Visibility.Collapsed;
        RiderCreationView.Visibility = tag == "create-rider" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryView.Visibility = tag == "recovery" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "maintenance")
        {
            ViewModel.Status = _session is null
                ? "Open a CDB file to use maintenance tools."
                : "Choose a maintenance tool to check and preview.";
            if (_session is not null)
            {
                await LoadMaintenanceTeamsAsync(_session, _lifetimeCancellation.Token);
            }
        }
        else if (tag == "create-rider")
        {
            ViewModel.Status = _session is null
                ? "Open a CDB file to create a rider."
                : "Preparing the Create Rider workflow…";
            if (_session is not null)
            {
                await InitializeRiderCreationAsync(_session, _lifetimeCancellation.Token);
            }
        }
        else if (tag == "recovery")
        {
            ViewModel.Status = "Recovery sessions are stored as isolated working copies.";
        }
        else if (_session is not null)
        {
            SetReadyState();
        }
    }

    private void RestoreContentNavigationSelection()
    {
        Navigation.SelectedItem = _lastContentNavigationItem ?? TablesNavigationItem;
    }

    private async Task ShowSettingsAsync()
    {
        var themePicker = new ComboBox
        {
            Header = "Theme",
            ItemsSource = Enum.GetValues<EditorApplicationTheme>(),
            SelectedItem = _preferences.Theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var densityPicker = new ComboBox
        {
            Header = "Grid density",
            ItemsSource = Enum.GetValues<GridDensity>(),
            SelectedItem = _preferences.Density,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var pageSizePicker = new ComboBox
        {
            Header = "Rows per page",
            ItemsSource = new[] { 100, 250 },
            SelectedItem = _preferences.PageSize is 250 ? 250 : 100,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var foreignKeyPicker = new ComboBox
        {
            Header = "Foreign-key display",
            ItemsSource = Enum.GetValues<ForeignKeyDisplayMode>(),
            SelectedItem = _preferences.ForeignKeyDisplayMode,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var recentFiles = new ListView
        {
            Header = "Recent CDB files",
            ItemsSource = _preferences.RecentFiles,
            MaxHeight = 150,
            SelectionMode = ListViewSelectionMode.Single,
        };
        var clearRecentButton = new Button { Content = "Clear recent files" };
        clearRecentButton.Click += (_, _) =>
        {
            recentFiles.ItemsSource = Array.Empty<string>();
            recentFiles.SelectedItem = null;
        };

        FileAssociationState association = await _fileAssociationService.InspectAsync(
            _lifetimeCancellation.Token);
        var associationStatus = new TextBlock
        {
            Text = association.IsRegistered
                ? "This executable is registered in Open with for .cdb files."
                : "This executable is not registered in Open with for .cdb files.",
            TextWrapping = TextWrapping.Wrap,
        };
        var associationButton = new Button
        {
            Content = association.IsRegistered ? "Remove Open with entry" : "Register Open with entry",
        };
        associationButton.Click += async (_, _) =>
        {
            try
            {
                if (association.IsRegistered)
                {
                    await _fileAssociationService.RemoveAsync(_lifetimeCancellation.Token);
                }
                else
                {
                    string executablePath = Environment.ProcessPath
                        ?? throw new InvalidOperationException("The application executable path is unavailable.");
                    await _fileAssociationService.RegisterAsync(
                        executablePath,
                        _lifetimeCancellation.Token);
                }

                association = await _fileAssociationService.InspectAsync(_lifetimeCancellation.Token);
                associationStatus.Text = association.IsRegistered
                    ? "This executable is registered in Open with for .cdb files."
                    : "This executable is not registered in Open with for .cdb files.";
                associationButton.Content = association.IsRegistered
                    ? "Remove Open with entry"
                    : "Register Open with entry";
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                associationStatus.Text = SafeFailureMessage(
                    exception,
                    "The Open with registration was not changed.");
            }
        };

        var content = new StackPanel { Spacing = 10, Width = 500 };
        content.Children.Add(themePicker);
        content.Children.Add(densityPicker);
        content.Children.Add(pageSizePicker);
        content.Children.Add(foreignKeyPicker);
        content.Children.Add(recentFiles);
        content.Children.Add(clearRecentButton);
        content.Children.Add(associationStatus);
        content.Children.Add(associationButton);
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Settings",
            Content = new ScrollViewer { Content = content, MaxHeight = 560 },
            PrimaryButtonText = "Save preferences",
            SecondaryButtonText = "Open selected recent file",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            if (recentFiles.SelectedItem is string recentPath)
            {
                await OpenPathAsync(recentPath);
            }
            else
            {
                PresentWarning("Choose a recent file", "Select one recent CDB before opening it.");
            }
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var updatedPreferences = new EditorPreferences(
            (EditorApplicationTheme)themePicker.SelectedItem,
            (GridDensity)densityPicker.SelectedItem,
            (int)pageSizePicker.SelectedItem,
            (ForeignKeyDisplayMode)foreignKeyPicker.SelectedItem,
            recentFiles.ItemsSource is IEnumerable<string> currentRecentFiles
                ? currentRecentFiles
                : []);
        bool foreignKeyModeChanged =
            updatedPreferences.ForeignKeyDisplayMode != _preferences.ForeignKeyDisplayMode;
        await _settingsStore.SavePreferencesAsync(updatedPreferences, _lifetimeCancellation.Token);
        _preferences = updatedPreferences;
        ApplyPreferences();
        if (foreignKeyModeChanged)
        {
            InvalidateOpenTableQueriesForForeignKeyMode();
        }

        if (TableTabs.SelectedItem is TabViewItem selectedTab &&
            selectedTab.Tag is TableTabContext context)
        {
            await LoadTableQueryAsync(
                context,
                context.SearchText,
                context.Filter,
                _preferences.PageSize,
                offset: 0,
                context.Sorts);
        }
        PresentSuccess("Preferences saved", "Theme, density, paging, and foreign-key display were updated.");
    }

    private void InvalidateOpenTableQueriesForForeignKeyMode()
    {
        foreach (TabViewItem tab in _tableTabs.Values)
        {
            CancelVirtualCount(tab);
            if (tab.Tag is not TableTabContext context)
            {
                continue;
            }

            context.Coordinator.Invalidate();
            tab.Tag = context with
            {
                CountState = TableRowCountState.Unknown,
                IsInvalidated = true,
            };
        }
    }

    private async void Density_Click(object sender, RoutedEventArgs e)
    {
        GridDensity density = _preferences.Density == GridDensity.Compact
            ? GridDensity.Comfortable
            : GridDensity.Compact;
        _preferences = new EditorPreferences(
            _preferences.Theme,
            density,
            _preferences.PageSize,
            _preferences.ForeignKeyDisplayMode,
            _preferences.RecentFiles);
        try
        {
            await _settingsStore.SavePreferencesAsync(_preferences, _lifetimeCancellation.Token);
            ApplyPreferences();
            if (TableTabs.SelectedItem is TabViewItem selected)
            {
                TableGrid.SetDensity(_preferences.Density);
                if (selected.Tag is TableTabContext context)
                {
                    UpdateTabChrome(selected, context);
                }
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError(
                "Density was not changed",
                SafeFailureMessage(exception, "No database row was changed."));
        }
    }

    private async void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDialog(
            "Keyboard shortcuts",
            "Ctrl+O Open · Ctrl+S Save · Ctrl+Z Undo · Ctrl+Y Redo · Enter or F2 Edit cell · Arrow keys Navigate grid",
            "Close",
            string.Empty,
            string.Empty);
        await dialog.ShowAsync();
    }

    private void ApplyPreferences()
    {
        WindowRoot.RequestedTheme = _preferences.Theme switch
        {
            EditorApplicationTheme.Light => ElementTheme.Light,
            EditorApplicationTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        TableGrid.SetDensity(_preferences.Density);
    }

    private async Task RememberRecentFileAsync(string path, CancellationToken cancellationToken)
    {
        string[] recentFiles = new[] { Path.GetFullPath(path) }
            .Concat(_preferences.RecentFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        _preferences = new EditorPreferences(
            _preferences.Theme,
            _preferences.Density,
            _preferences.PageSize,
            _preferences.ForeignKeyDisplayMode,
            recentFiles);
        try
        {
            await _settingsStore.SavePreferencesAsync(_preferences, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentWarning(
                "Recent files were not updated",
                SafeFailureMessage(exception, "The database remains open and no row content was stored."));
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operationLease?.Cancel();
        _activeTableLoad?.Cancellation.Cancel();
        CancelOperationButton.IsEnabled = false;
        ViewModel.Status = "Cancelling safely…";
    }

    private async void CheckRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        try
        {
            IReadOnlyList<RecoverableSession> recoverable =
                await _workspaceService.GetRecoverableSessionsAsync(_lifetimeCancellation.Token);
            PresentInformation(
                "Recovery check complete",
                recoverable.Count == 0
                    ? "No recoverable unsaved sessions were found."
                    : $"{recoverable.Count:N0} recoverable session(s) will be offered at the next startup.");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError(
                "Could not check recovery sessions",
                SafeFailureMessage(exception, "No recovery session was changed."));
        }
    }

    private void RecoveryTargetMode_Changed(object sender, RoutedEventArgs e)
    {
        if (RiderIdsTextBox is null || RiderRecoveryTeamComboBox is null)
        {
            return;
        }

        bool useTeam = RecoveryTeamModeRadioButton.IsChecked == true;
        RiderIdsTextBox.IsEnabled = !useTeam;
        UseSelectedRiderRowsButton.IsEnabled = !useTeam && _selectedRecoveryRiderIds.Length != 0;
        RiderRecoveryTeamComboBox.IsEnabled = useTeam
            && _maintenanceTeamsSessionId == _session?.SessionId
            && RiderRecoveryTeamComboBox.Items.Count != 0;
    }

    private void UseSelectedRiderRows_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecoveryRiderIds.Length == 0)
        {
            PresentWarning(
                "No rider IDs in the selection",
                "Select rows containing an integer IDcyclist or fkIDcyclist value, then try again.");
            return;
        }

        RecoveryIdsModeRadioButton.IsChecked = true;
        RiderIdsTextBox.Text = string.Join(", ", _selectedRecoveryRiderIds);
        RiderIdsTextBox.Focus(FocusState.Programmatic);
        RiderIdsTextBox.SelectAll();
    }

    private void ResetMaintenanceTargetsForSession(Guid? sessionId)
    {
        if (_maintenanceTargetsSessionId == sessionId)
        {
            return;
        }

        _maintenanceTargetsSessionId = sessionId;
        _maintenanceTeamsSessionId = null;
        _selectedRecoveryRiderIds = [];
        RiderIdsTextBox.Text = string.Empty;
        UseSelectedRiderRowsButton.IsEnabled = false;
        RiderRecoveryTeamComboBox.ItemsSource = null;
        RiderRecoveryTeamComboBox.SelectedItem = null;
        RiderRecoveryTeamComboBox.IsEnabled = false;
        RiderRecoveryTeamComboBox.PlaceholderText = "Loading teams…";
        RiderRecoveryTeamStatusText.Text = sessionId is null
            ? "Open a database to load teams. Rider IDs remain available without team tables."
            : "Team choices have not been loaded for this session yet.";
        ResetRiderCreationForSession(sessionId);
    }

    private async Task LoadMaintenanceTeamsAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        if (_maintenanceTeamsSessionId == session.SessionId)
        {
            RecoveryTargetMode_Changed(this, new RoutedEventArgs());
            return;
        }

        RiderRecoveryTeamStatusText.Text = "Loading teams…";
        RiderRecoveryTeamComboBox.IsEnabled = false;
        try
        {
            IReadOnlyList<RiderTeamOption> teams = await _riderRecoveryService.ListTeamsAsync(
                session.WorkingSqlitePath,
                cancellationToken);
            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            _maintenanceTeamsSessionId = session.SessionId;
            RiderRecoveryTeamComboBox.ItemsSource = teams;
            RiderRecoveryTeamStatusText.Text = teams.Count == 0
                ? "No teams were found. Rider IDs can still be entered directly."
                : $"{teams.Count:N0} teams loaded for this session.";
            RecoveryTargetMode_Changed(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation or shutdown owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            _maintenanceTeamsSessionId = session.SessionId;
            RiderRecoveryTeamStatusText.Text =
                "Team lookup is unavailable for this database. Enter rider IDs directly instead.";
            RiderRecoveryTeamComboBox.PlaceholderText = "Team lookup unavailable";
            RecoveryIdsModeRadioButton.IsChecked = true;
        }
    }

    private void ResetRiderCreationForSession(Guid? sessionId)
    {
        foreach (CancellationTokenSource cancellation in _riderLookupCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _riderLookupCancellations.Clear();
        foreach (RiderAdvancedFieldEditor editor in _riderAdvancedEditors.Concat(_contractAdvancedEditors))
        {
            editor.SearchCancellation?.Cancel();
            editor.SearchCancellation?.Dispose();
        }

        _riderCreationSessionId = null;
        _riderCreationDraft = null;
        _riderCreationPreview = null;
        _riderCreationTeam = null;
        _riderCreationRegion = null;
        _riderCreationType = null;
        _riderFavoriteRaceCandidate = null;
        _riderTeamLookup = null;
        _riderRegionLookup = null;
        _riderTypeLookup = null;
        _riderFavoriteRaceLookup = null;
        _riderFavoriteRaces.Clear();
        _riderGameDisplayNameState.Reset(string.Empty, string.Empty);
        _riderAbilityEditors.Clear();
        _riderAdvancedEditors.Clear();
        _contractAdvancedEditors.Clear();
        RiderAbilityRowsPanel.Children.Clear();
        RiderAdvancedFieldsPanel.Children.Clear();
        ContractAdvancedFieldsPanel.Children.Clear();
        _suppressRiderGameDisplayNameEvents = true;
        RiderFirstNameTextBox.Text = string.Empty;
        RiderLastNameTextBox.Text = string.Empty;
        RiderGameDisplayNameTextBox.Text = string.Empty;
        _suppressRiderGameDisplayNameEvents = false;
        RiderPhotoTextBox.Text = string.Empty;
        RiderSoundNameTextBox.Text = string.Empty;
        RiderBirthDatePicker.Date = null;
        RiderHeightNumberBox.Value = double.NaN;
        RiderWeightNumberBox.Value = double.NaN;
        RiderBulkCurrentNumberBox.Value = double.NaN;
        RiderBulkLimitNumberBox.Value = double.NaN;
        RiderPotentialNumberBox.Value = 3.0;
        RiderWageNumberBox.Value = double.NaN;
        RiderContractEndYearNumberBox.Value = double.NaN;
        RiderRoleComboBox.ItemsSource = null;
        RiderRoleComboBox.SelectedItem = null;
        RiderMissingLimitsAcknowledgement.IsChecked = false;
        RiderMissingLimitsAcknowledgement.Visibility = Visibility.Collapsed;
        RiderReviewSummaryText.Text = string.Empty;
        RiderReviewFavoriteRacesText.Text = string.Empty;
        RiderReviewAbilitiesText.Text = string.Empty;
        RiderReviewTechnicalValuesTextBox.Text = string.Empty;
        RiderReviewWarningInfo.IsOpen = false;
        RiderAbilityWarningInfo.IsOpen = false;
        _suppressRiderLookupText = true;
        RiderTeamSuggestBox.Text = string.Empty;
        RiderRegionSuggestBox.Text = string.Empty;
        RiderTypeSuggestBox.Text = string.Empty;
        RiderTeamSuggestBox.ItemsSource = null;
        RiderRegionSuggestBox.ItemsSource = null;
        RiderTypeSuggestBox.ItemsSource = null;
        _suppressRiderLookupText = false;
        _suppressFavoriteRaceText = true;
        RiderFavoriteRaceSuggestBox.Text = string.Empty;
        RiderFavoriteRaceSuggestBox.ItemsSource = null;
        _suppressFavoriteRaceText = false;
        AddFavoriteRaceButton.IsEnabled = false;
        RiderFavoriteRaceStatusText.Text = "No favorite races selected.";
        RiderCreationStepContentControl.IsEnabled = false;
        RiderCreationNextButton.IsEnabled = false;
        CreateRiderButton.IsEnabled = false;
        RiderCreationCapabilityInfo.IsOpen = true;
        RiderCreationCapabilityInfo.Severity = InfoBarSeverity.Informational;
        RiderCreationCapabilityInfo.Title = sessionId is null
            ? "Open a CDB to create a rider"
            : "Checking Create Rider requirements";
        RiderCreationCapabilityInfo.Message = sessionId is null
            ? "The wizard checks the open database schema before enabling creation."
            : "The open database is being checked without changing it.";
        _riderCreationStep = 0;
        _riderCreationMaxVisitedStep = 0;
        ShowRiderCreationStep(0);
    }

    private async Task InitializeRiderCreationAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        if (_riderCreationSessionId == session.SessionId && _riderCreationDraft is not null)
        {
            ViewModel.Status = $"Create Rider draft retained · Step {_riderCreationStep + 1} of 6.";
            return;
        }

        ResetRiderCreationForSession(session.SessionId);
        try
        {
            MaintenanceCapability capability = await _riderCreationService.CheckCapabilityAsync(
                session.WorkingSqlitePath,
                cancellationToken);
            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            if (!capability.IsEnabled)
            {
                RiderCreationCapabilityInfo.Severity = InfoBarSeverity.Warning;
                RiderCreationCapabilityInfo.Title = "Create Rider is unavailable for this database";
                RiderCreationCapabilityInfo.Message = FormatCapabilityDetails(capability);
                ViewModel.Status = "The open database does not support Create Rider.";
                return;
            }

            RiderCreationDraft draft = await _riderCreationService.PrepareAsync(
                session.WorkingSqlitePath,
                cancellationToken);
            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            _riderCreationSessionId = session.SessionId;
            _riderCreationDraft = draft;
            _riderTeamLookup = RequireRiderLookup(draft, "DYN_cyclist", "fkIDteam");
            _riderRegionLookup = RequireRiderLookup(draft, "DYN_cyclist", "fkIDregion");
            _riderTypeLookup = RequireRiderLookup(draft, "DYN_cyclist", "fkIDtype_rider");
            _riderFavoriteRaceLookup = draft.FavoriteRaceLookupTarget;
            RiderBirthDatePicker.MaxDate = new DateTimeOffset(
                draft.SaveDate.Year,
                draft.SaveDate.Month,
                draft.SaveDate.Day,
                0,
                0,
                0,
                TimeSpan.Zero);
            RiderContractEndYearNumberBox.Minimum = draft.SaveYear;
            string profileGuidance = CreateObservedProfileGuidance(draft);
            RiderProfileGuidanceText.Text = profileGuidance;
            AutomationProperties.SetHelpText(RiderHeightNumberBox, profileGuidance);
            AutomationProperties.SetHelpText(RiderWeightNumberBox, profileGuidance);
            RiderRoleComboBox.ItemsSource = CreateRiderRoleOptions();
            BuildRiderAbilityEditors(draft);
            BuildRiderAdvancedEditors(draft);
            RiderCreationStepContentControl.IsEnabled = true;
            RiderCreationNextButton.IsEnabled = true;
            RiderCreationCapabilityInfo.IsOpen = false;
            ViewModel.Status = "Create Rider is ready. Complete Identity to continue.";
            await LoadInitialRiderLookupsAsync(session, cancellationToken);
            RiderFirstNameTextBox.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation, session replacement, or shutdown owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (_session?.SessionId != session.SessionId)
            {
                return;
            }

            RiderCreationCapabilityInfo.IsOpen = true;
            RiderCreationCapabilityInfo.Severity = InfoBarSeverity.Error;
            RiderCreationCapabilityInfo.Title = "Create Rider could not be prepared";
            RiderCreationCapabilityInfo.Message = SafeFailureMessage(
                exception,
                "The database was not changed. Reopen the CDB and try again.");
            ViewModel.Status = "Create Rider could not be prepared.";
        }
    }

    private static string FormatCapabilityDetails(MaintenanceCapability capability)
    {
        var details = new List<string>(capability.Reasons);
        if (capability.MissingTables.Count != 0)
        {
            details.Add($"Missing tables: {string.Join(", ", capability.MissingTables)}.");
        }

        if (capability.MissingColumns.Count != 0)
        {
            details.Add($"Missing columns: {string.Join(", ", capability.MissingColumns)}.");
        }

        return details.Count == 0
            ? "The required rider-creation schema is unavailable."
            : string.Join(' ', details);
    }

    private static RiderLookupTarget RequireRiderLookup(
        RiderCreationDraft draft,
        string tableName,
        string columnName) =>
        draft.Fields.FirstOrDefault(field =>
                field.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                && field.Column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))?.LookupTarget
            ?? throw new InvalidDataException($"The required lookup for {tableName}.{columnName} is unavailable.");

    private static RiderRoleOption[] CreateRiderRoleOptions() =>
    [
        new(RiderContractRole.AbsoluteLeader, "Absolute leader"),
        new(RiderContractRole.AbsoluteSprinter, "Absolute sprinter"),
        new(RiderContractRole.Leader, "Leader"),
        new(RiderContractRole.Sprinter, "Sprinter"),
        new(RiderContractRole.ImportantRider, "Important rider"),
        new(RiderContractRole.LuxuryTeammate, "Luxury teammate"),
        new(RiderContractRole.Teammate, "Teammate")
    ];

    private static string CreateObservedProfileGuidance(RiderCreationDraft draft)
    {
        string height = draft.ObservedMinimumHeight is int minimumHeight
            && draft.ObservedMaximumHeight is int maximumHeight
            ? $"Observed height range in this save: {minimumHeight}–{maximumHeight} cm."
            : "This save has no positive rider heights to use as guidance.";
        string weight = draft.ObservedMinimumWeight is int minimumWeight
            && draft.ObservedMaximumWeight is int maximumWeight
            ? $"Observed weight range: {minimumWeight}–{maximumWeight} kg."
            : "This save has no positive rider weights to use as guidance.";
        return $"Birth date, height, and weight are required. {height} {weight} These ranges are guidance only.";
    }

    private void BuildRiderAbilityEditors(RiderCreationDraft draft)
    {
        RiderAbilityRowsPanel.Children.Clear();
        _riderAbilityEditors.Clear();
        foreach (RiderAbilityDefinition definition in draft.Abilities)
        {
            var current = new NumberBox
            {
                Minimum = 50,
                Maximum = 85,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            var limit = new NumberBox
            {
                Minimum = 50,
                Maximum = 85,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            AutomationProperties.SetAutomationId(current, $"RiderAbilityCurrent_{definition.Key}");
            AutomationProperties.SetName(current, $"{definition.Label} Current ability");
            AutomationProperties.SetAutomationId(limit, $"RiderAbilityLimit_{definition.Key}");
            AutomationProperties.SetName(limit, $"{definition.Label} ability Limit, optional");
            var warning = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCautionBrush"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            var label = new TextBlock
            {
                Text = definition.Label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(current, 1);
            Grid.SetColumn(limit, 2);
            grid.Children.Add(label);
            grid.Children.Add(current);
            grid.Children.Add(limit);
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(grid);
            row.Children.Add(warning);
            RiderAbilityRowsPanel.Children.Add(row);
            var editor = new RiderAbilityEditor(definition, current, limit, warning);
            current.Tag = editor;
            limit.Tag = editor;
            current.ValueChanged += RiderAbilityValue_Changed;
            limit.ValueChanged += RiderAbilityValue_Changed;
            _riderAbilityEditors.Add(editor);
        }
    }

    private void BuildRiderAdvancedEditors(RiderCreationDraft draft)
    {
        RiderAdvancedFieldsPanel.Children.Clear();
        ContractAdvancedFieldsPanel.Children.Clear();
        _riderAdvancedEditors.Clear();
        _contractAdvancedEditors.Clear();
        foreach (RiderCreationField field in draft.Fields.Where(static field => field.IsEditable && !field.IsLocked))
        {
            (StackPanel panel, RiderAdvancedFieldEditor editor) = CreateRiderAdvancedFieldEditor(field);
            if (field.TableName.Equals("DYN_cyclist", StringComparison.OrdinalIgnoreCase))
            {
                RiderAdvancedFieldsPanel.Children.Add(panel);
                _riderAdvancedEditors.Add(editor);
            }
            else
            {
                ContractAdvancedFieldsPanel.Children.Add(panel);
                _contractAdvancedEditors.Add(editor);
            }
        }

        RiderAdvancedExpander.Header = $"Rider game data ({_riderAdvancedEditors.Count + 1:N0})";
        ContractAdvancedExpander.Header = $"Contract game data ({_contractAdvancedEditors.Count:N0})";
        if (_riderAdvancedEditors.Count == 0)
        {
            RiderAdvancedFieldsPanel.Children.Add(new TextBlock { Text = "No additional rider fields are writable." });
        }

        if (_contractAdvancedEditors.Count == 0)
        {
            ContractAdvancedFieldsPanel.Children.Add(new TextBlock { Text = "No additional contract fields are writable." });
        }
    }

    private (StackPanel Panel, RiderAdvancedFieldEditor Editor) CreateRiderAdvancedFieldEditor(
        RiderCreationField field)
    {
        FrameworkElement valueEditor;
        if (field.LookupTarget is not null)
        {
            valueEditor = new AutoSuggestBox
            {
                PlaceholderText = "Search by name or ID",
                Text = FormatAdvancedLookupDefault(field),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }
        else if (field.Column.Affinity is SqliteAffinity.Integer or SqliteAffinity.Real or SqliteAffinity.Numeric)
        {
            valueEditor = new NumberBox
            {
                Value = field.Value.Kind switch
                {
                    SqliteValueKind.Integer => field.Value.IntegerValue,
                    SqliteValueKind.Real => field.Value.RealValue,
                    _ => double.NaN,
                },
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
        }
        else
        {
            valueEditor = new TextBox
            {
                Text = field.Value.Kind == SqliteValueKind.Text ? field.Value.TextValue ?? string.Empty : string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
        }

        AutomationProperties.SetAutomationId(
            valueEditor,
            $"RiderAdvanced_{field.TableName}_{field.Column.Name}");
        AutomationProperties.SetName(valueEditor, field.Label);
        string? helpText = CreateAdvancedFieldHelpText(field);
        if (helpText is not null)
        {
            AutomationProperties.SetHelpText(valueEditor, helpText);
        }
        CheckBox? nullBox = field.Column.IsNullable && !field.UsesDatabaseDefault
            ? new CheckBox
            {
                Content = "Store NULL",
                IsChecked = field.Value.Kind == SqliteValueKind.Null,
            }
            : null;
        CheckBox? useDefaultBox = field.UsesDatabaseDefault
            ? new CheckBox
            {
                Content = "Use database default",
                IsChecked = true,
            }
            : null;
        var editor = new RiderAdvancedFieldEditor(field, valueEditor, nullBox, useDefaultBox);
        valueEditor.Tag = editor;
        if (valueEditor is AutoSuggestBox suggestBox)
        {
            suggestBox.TextChanged += RiderAdvancedLookup_TextChanged;
            suggestBox.SuggestionChosen += RiderAdvancedLookup_SuggestionChosen;
        }

        if (nullBox is not null)
        {
            nullBox.Tag = editor;
            nullBox.Checked += RiderAdvancedMode_Changed;
            nullBox.Unchecked += RiderAdvancedMode_Changed;
        }

        if (useDefaultBox is not null)
        {
            useDefaultBox.Tag = editor;
            useDefaultBox.Checked += RiderAdvancedMode_Changed;
            useDefaultBox.Unchecked += RiderAdvancedMode_Changed;
        }

        UpdateAdvancedEditorEnabled(editor);
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{field.TableName}.{field.Column.Name} · {field.Column.Affinity}",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        if (helpText is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = helpText,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
        panel.Children.Add(valueEditor);
        if (nullBox is not null)
        {
            panel.Children.Add(nullBox);
        }

        if (useDefaultBox is not null)
        {
            panel.Children.Add(useDefaultBox);
        }

        return (panel, editor);
    }

    private static string? CreateAdvancedFieldHelpText(RiderCreationField field)
    {
        if (field.LookupTarget is not null)
        {
            return $"Searches {field.LookupTarget.TargetTable} and shows its readable label with the stored ID.";
        }

        if (field.Column.Name.StartsWith("fkID", StringComparison.OrdinalIgnoreCase))
        {
            return "No unambiguous lookup relationship was found. Enter the stored numeric ID directly.";
        }

        if (field.Column.Name.Equals("value_f_current_ability", StringComparison.OrdinalIgnoreCase))
        {
            return "Defaults to the arithmetic mean of the 14 Current abilities. This is not claimed to reproduce the game's internal formula.";
        }

        return null;
    }

    private static string FormatAdvancedLookupDefault(RiderCreationField field) =>
        field.Value.Kind == SqliteValueKind.Integer
            ? $"ID {field.Value.IntegerValue} (clean default)"
            : string.Empty;

    private void RiderAdvancedMode_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RiderAdvancedFieldEditor editor)
        {
            UpdateAdvancedEditorEnabled(editor);
        }
    }

    private static void UpdateAdvancedEditorEnabled(RiderAdvancedFieldEditor editor)
    {
        bool isEnabled = editor.NullBox?.IsChecked != true
            && editor.UseDefaultBox?.IsChecked != true;
        if (editor.ValueEditor is Control control)
        {
            control.IsEnabled = isEnabled;
        }

        editor.ValueEditor.Opacity = isEnabled ? 1 : 0.55;
    }

    private void RiderIdentityName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRiderGameDisplayNameEvents)
        {
            return;
        }

        if (_riderGameDisplayNameState.UpdateNames(
                RiderFirstNameTextBox.Text,
                RiderLastNameTextBox.Text))
        {
            SetRiderGameDisplayNameText(_riderGameDisplayNameState.Value);
        }

        InvalidateRiderCreationPreview();
    }

    private void RiderGameDisplayName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRiderGameDisplayNameEvents)
        {
            return;
        }

        _riderGameDisplayNameState.Override(RiderGameDisplayNameTextBox.Text);
        InvalidateRiderCreationPreview();
    }

    private void ResetRiderGameDisplayName_Click(object sender, RoutedEventArgs e)
    {
        _riderGameDisplayNameState.Reset(RiderFirstNameTextBox.Text, RiderLastNameTextBox.Text);
        SetRiderGameDisplayNameText(_riderGameDisplayNameState.Value);
        InvalidateRiderCreationPreview();
        RiderGameDisplayNameTextBox.Focus(FocusState.Programmatic);
    }

    private void SetRiderGameDisplayNameText(string value)
    {
        _suppressRiderGameDisplayNameEvents = true;
        RiderGameDisplayNameTextBox.Text = value;
        _suppressRiderGameDisplayNameEvents = false;
    }

    private async void RiderFavoriteRace_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressFavoriteRaceText || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _riderFavoriteRaceCandidate = null;
        AddFavoriteRaceButton.IsEnabled = false;
        if (_riderFavoriteRaceLookup is null
            || _session is null
            || _riderCreationSessionId != _session.SessionId)
        {
            return;
        }

        if (_riderLookupCancellations.Remove(sender, out CancellationTokenSource? oldCancellation))
        {
            oldCancellation.Cancel();
            oldCancellation.Dispose();
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _riderLookupCancellations[sender] = cancellation;
        UpdateFavoriteRaceStatus("Searching races…");
        try
        {
            await Task.Delay(SearchDelayMilliseconds, cancellation.Token);
            IReadOnlyList<RiderLookupOption> options = await _riderCreationService.SearchLookupAsync(
                _session.WorkingSqlitePath,
                _riderFavoriteRaceLookup,
                sender.Text,
                50,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && _riderCreationSessionId == _session?.SessionId)
            {
                sender.ItemsSource = options;
                UpdateFavoriteRaceStatus(options.Count == 0 ? "No matching races found." : null);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer search or database session owns the suggestions.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            sender.ItemsSource = Array.Empty<RiderLookupOption>();
            UpdateFavoriteRaceStatus("Race search is unavailable. Try again or reopen the CDB.");
        }
        finally
        {
            if (_riderLookupCancellations.TryGetValue(sender, out CancellationTokenSource? current)
                && ReferenceEquals(current, cancellation))
            {
                _riderLookupCancellations.Remove(sender);
                cancellation.Dispose();
            }
        }
    }

    private void RiderFavoriteRace_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not RiderLookupOption option)
        {
            return;
        }

        _riderFavoriteRaceCandidate = option;
        _suppressFavoriteRaceText = true;
        sender.Text = option.ToString();
        _suppressFavoriteRaceText = false;
        AddFavoriteRaceButton.IsEnabled = true;
        UpdateFavoriteRaceStatus("Selected. Choose Add race.");
    }

    private void AddFavoriteRace_Click(object sender, RoutedEventArgs e)
    {
        if (_riderFavoriteRaceCandidate is not { } option)
        {
            UpdateFavoriteRaceStatus("Choose a race from the search suggestions first.");
            RiderFavoriteRaceSuggestBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_riderFavoriteRaces.Any(existing => existing.Id == option.Id))
        {
            UpdateFavoriteRaceStatus("That race is already selected.");
            return;
        }

        _riderFavoriteRaces.Add(option);
        _riderFavoriteRaceCandidate = null;
        _suppressFavoriteRaceText = true;
        RiderFavoriteRaceSuggestBox.Text = string.Empty;
        RiderFavoriteRaceSuggestBox.ItemsSource = null;
        _suppressFavoriteRaceText = false;
        AddFavoriteRaceButton.IsEnabled = false;
        InvalidateRiderCreationPreview();
        UpdateFavoriteRaceStatus();
        RiderFavoriteRaceSuggestBox.Focus(FocusState.Programmatic);
    }

    private void RemoveFavoriteRace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RiderLookupOption option
            && _riderFavoriteRaces.Remove(option))
        {
            InvalidateRiderCreationPreview();
            UpdateFavoriteRaceStatus();
            RiderFavoriteRaceSuggestBox.Focus(FocusState.Programmatic);
        }
    }

    private void MoveFavoriteRaceUp_Click(object sender, RoutedEventArgs e) =>
        MoveFavoriteRace(sender, -1);

    private void MoveFavoriteRaceDown_Click(object sender, RoutedEventArgs e) =>
        MoveFavoriteRace(sender, 1);

    private void MoveFavoriteRace(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.Tag is not RiderLookupOption option)
        {
            return;
        }

        int oldIndex = _riderFavoriteRaces.IndexOf(option);
        int newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= _riderFavoriteRaces.Count)
        {
            return;
        }

        _riderFavoriteRaces.Move(oldIndex, newIndex);
        InvalidateRiderCreationPreview();
        UpdateFavoriteRaceStatus();
    }

    private void UpdateFavoriteRaceStatus(string? detail = null)
    {
        string count = _riderFavoriteRaces.Count == 0
            ? "No favorite races selected."
            : _riderFavoriteRaces.Count == 1
                ? "1 favorite race selected."
                : $"{_riderFavoriteRaces.Count:N0} favorite races selected.";
        RiderFavoriteRaceStatusText.Text = string.IsNullOrWhiteSpace(detail)
            ? count
            : _riderFavoriteRaces.Count == 0
                ? detail
                : $"{count} {detail}";
    }

    private void InvalidateRiderCreationPreview()
    {
        _riderCreationPreview = null;
        UpdateCreateRiderButtonState();
    }

    private async Task LoadInitialRiderLookupsAsync(
        EditorSessionState session,
        CancellationToken cancellationToken)
    {
        if (_riderTeamLookup is null || _riderRegionLookup is null || _riderTypeLookup is null)
        {
            return;
        }

        Task<IReadOnlyList<RiderLookupOption>> teams = _riderCreationService.SearchLookupAsync(
            session.WorkingSqlitePath, _riderTeamLookup, string.Empty, 25, cancellationToken);
        Task<IReadOnlyList<RiderLookupOption>> regions = _riderCreationService.SearchLookupAsync(
            session.WorkingSqlitePath, _riderRegionLookup, string.Empty, 25, cancellationToken);
        Task<IReadOnlyList<RiderLookupOption>> types = _riderCreationService.SearchLookupAsync(
            session.WorkingSqlitePath, _riderTypeLookup, string.Empty, 25, cancellationToken);
        await Task.WhenAll(teams, regions, types);
        if (_session?.SessionId != session.SessionId)
        {
            return;
        }

        RiderTeamSuggestBox.ItemsSource = await teams;
        RiderRegionSuggestBox.ItemsSource = await regions;
        RiderTypeSuggestBox.ItemsSource = await types;
    }

    private async void RiderLookup_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressRiderLookupText || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        SetSelectedCoreLookup(sender, null);
        RiderLookupTarget? target = (sender.Tag as string) switch
        {
            "team" => _riderTeamLookup,
            "region" => _riderRegionLookup,
            "type" => _riderTypeLookup,
            _ => null,
        };
        if (target is null || _session is null || _riderCreationSessionId != _session.SessionId)
        {
            return;
        }

        if (_riderLookupCancellations.Remove(sender, out CancellationTokenSource? oldCancellation))
        {
            oldCancellation.Cancel();
            oldCancellation.Dispose();
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _riderLookupCancellations[sender] = cancellation;
        try
        {
            await Task.Delay(SearchDelayMilliseconds, cancellation.Token);
            IReadOnlyList<RiderLookupOption> options = await _riderCreationService.SearchLookupAsync(
                _session.WorkingSqlitePath,
                target,
                sender.Text,
                25,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && _riderCreationSessionId == _session?.SessionId)
            {
                sender.ItemsSource = options;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer query or session owns the suggestions.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            sender.ItemsSource = Array.Empty<RiderLookupOption>();
            RiderCreationCapabilityInfo.IsOpen = true;
            RiderCreationCapabilityInfo.Severity = InfoBarSeverity.Warning;
            RiderCreationCapabilityInfo.Title = $"{target.Label} lookup is unavailable";
            RiderCreationCapabilityInfo.Message = SafeFailureMessage(
                exception,
                "The database was not changed. Try a different search or reopen the CDB.");
        }
        finally
        {
            if (_riderLookupCancellations.TryGetValue(sender, out CancellationTokenSource? current)
                && ReferenceEquals(current, cancellation))
            {
                _riderLookupCancellations.Remove(sender);
                cancellation.Dispose();
            }
        }
    }

    private void RiderLookup_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not RiderLookupOption option)
        {
            return;
        }

        SetSelectedCoreLookup(sender, option);
        _suppressRiderLookupText = true;
        sender.Text = option.ToString();
        _suppressRiderLookupText = false;
        InvalidateRiderCreationPreview();
        if (ReferenceEquals(sender, RiderTeamSuggestBox))
        {
            RiderContractTeamText.Text =
                $"{option.DisplayName} ({option.Id:N0}) will be written to the rider and both contract team fields.";
        }
    }

    private void SetSelectedCoreLookup(AutoSuggestBox sender, RiderLookupOption? option)
    {
        if (ReferenceEquals(sender, RiderTeamSuggestBox))
        {
            _riderCreationTeam = option;
        }
        else if (ReferenceEquals(sender, RiderRegionSuggestBox))
        {
            _riderCreationRegion = option;
        }
        else if (ReferenceEquals(sender, RiderTypeSuggestBox))
        {
            _riderCreationType = option;
        }
    }

    private async void RiderAdvancedLookup_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput
            || sender.Tag is not RiderAdvancedFieldEditor editor
            || editor.Field.LookupTarget is null
            || _session is null
            || _riderCreationSessionId != _session.SessionId)
        {
            return;
        }

        editor.SelectedLookup = null;
        editor.SearchCancellation?.Cancel();
        editor.SearchCancellation?.Dispose();
        editor.SearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        CancellationToken token = editor.SearchCancellation.Token;
        try
        {
            await Task.Delay(SearchDelayMilliseconds, token);
            sender.ItemsSource = await _riderCreationService.SearchLookupAsync(
                _session.WorkingSqlitePath,
                editor.Field.LookupTarget,
                sender.Text,
                25,
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer query or session owns the suggestions.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            sender.ItemsSource = Array.Empty<RiderLookupOption>();
        }
    }

    private static void RiderAdvancedLookup_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (sender.Tag is RiderAdvancedFieldEditor editor
            && args.SelectedItem is RiderLookupOption option)
        {
            editor.SelectedLookup = option;
            sender.Text = option.ToString();
        }
    }

    private void RiderCreationStep_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string value
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int step)
            || step > _riderCreationMaxVisitedStep)
        {
            return;
        }

        if (step == 5)
        {
            OperationInfo.IsOpen = false;
            _ = PrepareRiderCreationReviewAsync();
            return;
        }

        OperationInfo.IsOpen = false;
        ShowRiderCreationStep(step);
        ViewModel.Status = $"Create Rider draft retained · Step {_riderCreationStep + 1} of 6.";
    }

    private void RiderCreationBack_Click(object sender, RoutedEventArgs e)
    {
        if (_riderCreationStep > 0)
        {
            OperationInfo.IsOpen = false;
            ShowRiderCreationStep(_riderCreationStep - 1);
            ViewModel.Status = $"Create Rider draft retained · Step {_riderCreationStep + 1} of 6.";
        }
    }

    private async void RiderCreationNext_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRiderCreationStep(_riderCreationStep, showError: true))
        {
            return;
        }

        if (_riderCreationStep == 4)
        {
            OperationInfo.IsOpen = false;
            await PrepareRiderCreationReviewAsync();
            return;
        }

        OperationInfo.IsOpen = false;
        _riderCreationMaxVisitedStep = Math.Max(_riderCreationMaxVisitedStep, _riderCreationStep + 1);
        ShowRiderCreationStep(_riderCreationStep + 1);
        ViewModel.Status = $"Create Rider draft retained · Step {_riderCreationStep + 1} of 6.";
    }

    private void ShowRiderCreationStep(int step)
    {
        _riderCreationStep = Math.Clamp(step, 0, 5);
        FrameworkElement[] panels =
        [
            RiderIdentityStep,
            RiderProfileStep,
            RiderAbilitiesStep,
            RiderContractStep,
            RiderAdvancedStep,
            RiderReviewStep,
        ];
        Button[] buttons =
        [
            RiderStepIdentityButton,
            RiderStepProfileButton,
            RiderStepAbilitiesButton,
            RiderStepContractButton,
            RiderStepAdvancedButton,
            RiderStepReviewButton,
        ];
        string[] labels = ["Identity", "Profile", "Abilities", "Contract", "Advanced", "Review"];
        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].Visibility = index == _riderCreationStep ? Visibility.Visible : Visibility.Collapsed;
            buttons[index].IsEnabled = index <= _riderCreationMaxVisitedStep;
            buttons[index].FontWeight = index == _riderCreationStep
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            AutomationProperties.SetHelpText(
                buttons[index],
                index == _riderCreationStep ? "Current step" : "Available Create Rider step");
        }

        RiderCreationBackButton.IsEnabled = _riderCreationStep > 0;
        RiderCreationStepStatusText.Text =
            $"Step {_riderCreationStep + 1} of 6 · {labels[_riderCreationStep]}";
        RiderCreationNextButton.Visibility = _riderCreationStep == 5
            ? Visibility.Collapsed
            : Visibility.Visible;
        CreateRiderButton.Visibility = _riderCreationStep == 5
            ? Visibility.Visible
            : Visibility.Collapsed;
        RiderCreationNextButton.Content = _riderCreationStep switch
        {
            0 => "Continue to Profile",
            1 => "Continue to Abilities",
            2 => "Continue to Contract",
            3 => "Continue to Advanced",
            4 => "Review rider",
            _ => "Continue",
        };
        AutomationProperties.SetName(RiderCreationNextButton, RiderCreationNextButton.Content.ToString()!);
        FocusRiderCreationStep(_riderCreationStep);
    }

    private void FocusRiderCreationStep(int step)
    {
        if (!RiderCreationStepContentControl.IsEnabled)
        {
            return;
        }

        Control target = step switch
        {
            0 => RiderFirstNameTextBox,
            1 => RiderBirthDatePicker,
            2 => _riderAbilityEditors.FirstOrDefault()?.Current ?? RiderBulkCurrentNumberBox,
            3 => RiderRoleComboBox,
            4 => RiderAdvancedExpander,
            _ => RiderMissingLimitsAcknowledgement.Visibility == Visibility.Visible
                ? RiderMissingLimitsAcknowledgement
                : CreateRiderButton,
        };
        target.Focus(FocusState.Programmatic);
    }

    private void SetAllCurrentAbilities_Click(object sender, RoutedEventArgs e)
    {
        if (!IsWholeAbilityValue(RiderBulkCurrentNumberBox.Value))
        {
            PresentWarning("Check Current value", "Enter a whole number from 50 to 85 before setting every Current ability.");
            return;
        }

        foreach (RiderAbilityEditor editor in _riderAbilityEditors)
        {
            editor.Current.Value = RiderBulkCurrentNumberBox.Value;
        }
    }

    private void SetAllLimitAbilities_Click(object sender, RoutedEventArgs e)
    {
        if (!IsWholeAbilityValue(RiderBulkLimitNumberBox.Value))
        {
            PresentWarning("Check Limit value", "Enter a whole number from 50 to 85 before setting every ability Limit.");
            return;
        }

        foreach (RiderAbilityEditor editor in _riderAbilityEditors)
        {
            editor.Limit.Value = RiderBulkLimitNumberBox.Value;
        }
    }

    private void RiderAbilityValue_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender.Tag is RiderAbilityEditor editor)
        {
            UpdateRiderAbilityWarning(editor);
        }

        InvalidateRiderCreationPreview();
    }

    private void UpdateRiderAbilityWarning(RiderAbilityEditor editor)
    {
        bool currentValid = IsWholeAbilityValue(editor.Current.Value);
        bool limitEntered = !double.IsNaN(editor.Limit.Value);
        bool limitValid = !limitEntered || IsWholeAbilityValue(editor.Limit.Value);
        bool exceeds = currentValid && limitValid && limitEntered
            && editor.Current.Value > editor.Limit.Value;
        editor.Warning.Text = exceeds
            ? $"{editor.Definition.Label}: Current {editor.Current.Value:N0} exceeds Limit {editor.Limit.Value:N0}. This is allowed and will not be changed."
            : string.Empty;
        editor.Warning.Visibility = exceeds ? Visibility.Visible : Visibility.Collapsed;
        string[] warnings = _riderAbilityEditors
            .Where(static item => item.Warning.Visibility == Visibility.Visible)
            .Select(static item => item.Warning.Text)
            .ToArray();
        RiderAbilityWarningInfo.Message = string.Join(' ', warnings);
        RiderAbilityWarningInfo.IsOpen = warnings.Length != 0;
    }

    private static bool IsWholeAbilityValue(double value) =>
        !double.IsNaN(value) && value is >= 50 and <= 85 && Math.Truncate(value) == value;

    private bool ValidateRiderCreationStep(int step, bool showError)
    {
        switch (step)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(RiderFirstNameTextBox.Text))
                {
                    return RejectRiderStep(
                        showError, "Enter a first name", "First name is required.", RiderFirstNameTextBox);
                }

                if (string.IsNullOrWhiteSpace(RiderLastNameTextBox.Text))
                {
                    return RejectRiderStep(
                        showError, "Enter a last name", "Last name is required.", RiderLastNameTextBox);
                }

                if (_riderCreationTeam is null)
                {
                    return RejectRiderStep(
                        showError, "Choose a team", "Choose a team from the search suggestions.", RiderTeamSuggestBox);
                }

                if (_riderCreationRegion is null)
                {
                    return RejectRiderStep(
                        showError, "Choose a region", "Choose a region from the search suggestions.", RiderRegionSuggestBox);
                }

                if (_riderCreationType is null)
                {
                    return RejectRiderStep(
                        showError, "Choose a rider type", "Choose a rider type from the search suggestions.", RiderTypeSuggestBox);
                }

                return true;

            case 1:
                if (RiderBirthDatePicker.Date is null)
                {
                    return RejectRiderStep(
                        showError, "Enter a birth date", "Birth date is required.", RiderBirthDatePicker);
                }

                if (!IsPositiveWholeNumber(RiderHeightNumberBox.Value))
                {
                    return RejectRiderStep(
                        showError, "Check height", "Height must be a positive whole number of centimetres.", RiderHeightNumberBox);
                }

                if (!IsPositiveWholeNumber(RiderWeightNumberBox.Value))
                {
                    return RejectRiderStep(
                        showError, "Check weight", "Weight must be a positive whole number of kilograms.", RiderWeightNumberBox);
                }

                return true;

            case 2:
                if (!IsPotentialValue(RiderPotentialNumberBox.Value))
                {
                    return RejectRiderStep(
                        showError,
                        "Check potential",
                        "Potential must be from 0.5 to 6.0 in 0.5 increments.",
                        RiderPotentialNumberBox);
                }

                RiderAbilityEditor? invalidCurrent = _riderAbilityEditors.FirstOrDefault(editor =>
                    !IsWholeAbilityValue(editor.Current.Value));
                if (invalidCurrent is not null)
                {
                    return RejectRiderStep(
                        showError,
                        "Check Current abilities",
                        $"{invalidCurrent.Definition.Label} Current must be a whole number from 50 to 85.",
                        invalidCurrent.Current);
                }

                RiderAbilityEditor? invalidLimit = _riderAbilityEditors.FirstOrDefault(editor =>
                    !double.IsNaN(editor.Limit.Value) && !IsWholeAbilityValue(editor.Limit.Value));
                if (invalidLimit is not null)
                {
                    return RejectRiderStep(
                        showError,
                        "Check ability Limits",
                        $"{invalidLimit.Definition.Label} Limit must be blank or a whole number from 50 to 85.",
                        invalidLimit.Limit);
                }

                return _riderAbilityEditors.Count == 14;

            case 3:
                if (RiderRoleComboBox.SelectedItem is not RiderRoleOption)
                {
                    return RejectRiderStep(
                        showError, "Choose a contract role", "Choose one labelled role and stored code.", RiderRoleComboBox);
                }

                if (!IsPositiveWholeNumber(RiderWageNumberBox.Value))
                {
                    return RejectRiderStep(
                        showError, "Check contract wage", "Wage must be a positive whole number.", RiderWageNumberBox);
                }

                if (!IsPositiveWholeNumber(RiderContractEndYearNumberBox.Value)
                    || _riderCreationDraft is null
                    || RiderContractEndYearNumberBox.Value < _riderCreationDraft.SaveYear)
                {
                    string year = _riderCreationDraft?.SaveYear.ToString(CultureInfo.InvariantCulture) ?? "the save year";
                    return RejectRiderStep(
                        showError,
                        "Check contract end year",
                        $"Contract end year must be a whole year no earlier than {year}.",
                        RiderContractEndYearNumberBox);
                }

                return true;

            case 4:
                if (string.IsNullOrWhiteSpace(RiderGameDisplayNameTextBox.Text))
                {
                    return RejectRiderStep(
                        showError,
                        "Check rider display name",
                        "Rider display name cannot be blank. Reset it to the generated value or enter an override.",
                        RiderGameDisplayNameTextBox);
                }

                return TryReadRiderAdvancedValues(out _, out _, out string? error)
                    || RejectRiderStep(
                        showError,
                        "Check Advanced values",
                        error ?? "One or more Advanced values are invalid.",
                        RiderAdvancedExpander);

            default:
                return _riderCreationPreview is not null;
        }
    }

    private bool RejectRiderStep(
        bool showError,
        string title,
        string message,
        Control focusTarget)
    {
        if (showError)
        {
            PresentWarning(title, message);
            focusTarget.Focus(FocusState.Programmatic);
        }

        return false;
    }

    private static bool IsPositiveWholeNumber(double value) =>
        !double.IsNaN(value) && value > 0 && value <= long.MaxValue && Math.Truncate(value) == value;

    private static bool IsPotentialValue(double value) =>
        double.IsFinite(value)
        && value is >= 0.5 and <= 6.0
        && Math.Abs((value * 2) - Math.Round(value * 2)) <= 0.0000001;

    private async Task PrepareRiderCreationReviewAsync()
    {
        if (_session is null || _riderCreationDraft is null
            || _riderCreationSessionId != _session.SessionId || ViewModel.IsBusy)
        {
            PresentWarning("Create Rider is not ready", "Open a supported CDB and wait for the wizard to finish loading.");
            return;
        }

        for (var step = 0; step <= 4; step++)
        {
            if (!ValidateRiderCreationStep(step, showError: true))
            {
                _riderCreationMaxVisitedStep = Math.Max(_riderCreationMaxVisitedStep, step);
                ShowRiderCreationStep(step);
                return;
            }
        }

        if (!TryBuildRiderCreationInput(
                RiderMissingLimitsAcknowledgement.IsChecked == true,
                out RiderCreationInput? input,
                out string? error))
        {
            PresentWarning("Check rider values", error ?? "The rider draft is incomplete.");
            return;
        }

        EditorSessionState session = _session;
        await RunMaintenanceAsync(
            "Rider creation preview",
            async cancellationToken =>
            {
                RiderCreationPreview preview = await _riderCreationService.PreviewAsync(
                    session.WorkingSqlitePath,
                    input,
                    cancellationToken);
                if (_session?.SessionId != session.SessionId)
                {
                    return;
                }

                _riderCreationPreview = preview;
                PopulateRiderCreationReview(preview);
                _riderCreationMaxVisitedStep = 5;
                ShowRiderCreationStep(5);
                ViewModel.Status = "Review the generated rider and contract before creating them.";
            });
    }

    private bool TryBuildRiderCreationInput(
        bool missingLimitsAcknowledged,
        out RiderCreationInput input,
        out string? error)
    {
        input = null!;
        error = null;
        if (_riderCreationTeam is null || _riderCreationRegion is null || _riderCreationType is null
            || RiderBirthDatePicker.Date is not DateTimeOffset birthDate
            || RiderRoleComboBox.SelectedItem is not RiderRoleOption role)
        {
            error = "Identity, Profile, or Contract is incomplete.";
            return false;
        }

        if (!TryReadRiderAdvancedValues(
                out Dictionary<string, SqliteValue> riderAdvanced,
                out Dictionary<string, SqliteValue> contractAdvanced,
                out error))
        {
            return false;
        }

        try
        {
            RiderAbilityInput[] abilities = _riderAbilityEditors.Select(editor =>
                new RiderAbilityInput(
                    editor.Definition.Key,
                    checked((int)editor.Current.Value),
                    double.IsNaN(editor.Limit.Value) ? null : checked((int)editor.Limit.Value)))
                .ToArray();
            input = new RiderCreationInput(
                RiderFirstNameTextBox.Text,
                RiderLastNameTextBox.Text,
                _riderCreationTeam.Id,
                _riderCreationRegion.Id,
                _riderCreationType.Id,
                DateOnly.FromDateTime(birthDate.DateTime),
                checked((int)RiderHeightNumberBox.Value),
                checked((int)RiderWeightNumberBox.Value),
                RiderPhotoTextBox.Text,
                RiderSoundNameTextBox.Text,
                abilities,
                role.Role,
                checked((long)RiderWageNumberBox.Value),
                checked((int)RiderContractEndYearNumberBox.Value),
                missingLimitsAcknowledged,
                riderAdvanced,
                contractAdvanced,
                gameDisplayName: RiderGameDisplayNameTextBox.Text,
                potential: RiderPotentialNumberBox.Value,
                favoriteRaceIds: _riderFavoriteRaces.Select(static race => race.Id));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryReadRiderAdvancedValues(
        out Dictionary<string, SqliteValue> riderValues,
        out Dictionary<string, SqliteValue> contractValues,
        out string? error)
    {
        riderValues = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
        contractValues = new Dictionary<string, SqliteValue>(StringComparer.OrdinalIgnoreCase);
        foreach (RiderAdvancedFieldEditor editor in _riderAdvancedEditors.Concat(_contractAdvancedEditors))
        {
            if (!TryReadRiderAdvancedValue(editor, out bool include, out SqliteValue value, out error))
            {
                return false;
            }

            if (!include)
            {
                continue;
            }

            Dictionary<string, SqliteValue> target = editor.Field.TableName.Equals(
                "DYN_cyclist",
                StringComparison.OrdinalIgnoreCase)
                    ? riderValues
                    : contractValues;
            target.Add(editor.Field.Column.Name, value);
        }

        error = null;
        return true;
    }

    private static bool TryReadRiderAdvancedValue(
        RiderAdvancedFieldEditor editor,
        out bool include,
        out SqliteValue value,
        out string? error)
    {
        include = false;
        value = SqliteValue.Null;
        error = null;
        if (editor.UseDefaultBox?.IsChecked == true)
        {
            return true;
        }

        if (editor.NullBox?.IsChecked == true)
        {
            value = SqliteValue.Null;
            include = editor.Field.Value.Kind != SqliteValueKind.Null;
            return true;
        }

        if (editor.ValueEditor is AutoSuggestBox suggestBox)
        {
            if (editor.SelectedLookup is null)
            {
                if (suggestBox.Text.Equals(
                        FormatAdvancedLookupDefault(editor.Field),
                        StringComparison.Ordinal))
                {
                    return true;
                }

                error = $"Choose {editor.Field.Label} from its search suggestions.";
                return false;
            }

            value = SqliteValue.Integer(editor.SelectedLookup.Id);
        }
        else if (editor.ValueEditor is NumberBox numberBox)
        {
            if (double.IsNaN(numberBox.Value))
            {
                if (!editor.Field.Column.IsNullable)
                {
                    error = $"{editor.Field.Label} requires a value.";
                    return false;
                }

                value = SqliteValue.Null;
            }
            else if (editor.Field.Column.Affinity == SqliteAffinity.Integer)
            {
                if (Math.Truncate(numberBox.Value) != numberBox.Value
                    || numberBox.Value < long.MinValue
                    || numberBox.Value > long.MaxValue)
                {
                    error = $"{editor.Field.Label} must be a whole SQLite integer.";
                    return false;
                }

                value = SqliteValue.Integer(checked((long)numberBox.Value));
            }
            else
            {
                value = SqliteValue.Real(numberBox.Value);
            }
        }
        else if (editor.ValueEditor is TextBox textBox)
        {
            value = SqliteValue.Text(textBox.Text);
        }
        else
        {
            error = $"{editor.Field.Label} uses an unsupported editor.";
            return false;
        }

        include = value != editor.Field.Value || editor.Field.UsesDatabaseDefault;
        return true;
    }

    private void PopulateRiderCreationReview(RiderCreationPreview preview)
    {
        RiderRoleOption role = (RiderRoleOption)RiderRoleComboBox.SelectedItem;
        RiderReviewSummaryText.Text =
            $"{preview.Input.FirstName} {preview.Input.LastName} will be rider {preview.NewCyclistId:N0} with contract {preview.NewContractId:N0}. " +
            $"Game display name: {preview.Input.GameDisplayName}. Potential: {preview.Input.Potential:N1}. " +
            $"Team: {_riderCreationTeam}. Region: {_riderCreationRegion}. Rider type: {_riderCreationType}. " +
            $"Contract: {role.Label} ({(int)role.Role}), wage {preview.Input.Wage:N0}, through {preview.Input.ContractEndYear}. " +
            "Both rows are one atomic change and one Undo operation.";
        RiderReviewFavoriteRacesText.Text = preview.FavoriteRaces.Count == 0
            ? "None selected · stored as ()"
            : string.Join(Environment.NewLine, preview.FavoriteRaces.Select(static race => race.ToString()));
        Dictionary<string, RiderAbilityInput> abilities = preview.Input.Abilities.ToDictionary(
            static ability => ability.Key,
            StringComparer.OrdinalIgnoreCase);
        RiderReviewAbilitiesText.Text = string.Join(
            Environment.NewLine,
            _riderCreationDraft!.Abilities.Select(definition =>
            {
                RiderAbilityInput ability = abilities[definition.Key];
                string limit = ability.Limit?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
                return $"{definition.Label}: Current {ability.Current}, Limit {limit}";
            }));
        RiderReviewWarningInfo.Message = string.Join(' ', preview.Warnings);
        RiderReviewWarningInfo.IsOpen = preview.Warnings.Count != 0;
        RiderMissingLimitsAcknowledgement.Visibility = preview.MissingLimitKeys.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (preview.MissingLimitKeys.Count == 0)
        {
            RiderMissingLimitsAcknowledgement.IsChecked = false;
        }

        var technical = new StringBuilder();
        foreach ((string name, SqliteValue value) in preview.RiderValues.OrderBy(
                     static pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            technical.Append("Rider.");
            technical.Append(name);
            technical.Append(" = ");
            technical.AppendLine(FormatSqliteValue(value));
        }

        foreach ((string name, SqliteValue value) in preview.ContractValues.OrderBy(
                     static pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            technical.Append("Contract.");
            technical.Append(name);
            technical.Append(" = ");
            technical.AppendLine(FormatSqliteValue(value));
        }

        RiderReviewTechnicalValuesTextBox.Text = technical.ToString();
        UpdateCreateRiderButtonState();
    }

    private void RiderMissingLimitsAcknowledgement_Changed(object sender, RoutedEventArgs e) =>
        UpdateCreateRiderButtonState();

    private void UpdateCreateRiderButtonState()
    {
        bool hasCurrentPreview = _riderCreationPreview is not null
            && _session is not null
            && _riderCreationSessionId == _session.SessionId;
        CreateRiderButton.IsEnabled = RiderCreationCommandAvailability.CanCreate(
            hasCurrentPreview,
            _riderCreationPreview?.MissingLimitKeys.Count ?? 0,
            RiderMissingLimitsAcknowledgement.IsChecked == true,
            ViewModel.IsBusy,
            _operationLease is not null);
    }

    private async void CreateRider_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _riderCreationDraft is null
            || _riderCreationSessionId != _session.SessionId || ViewModel.IsBusy)
        {
            return;
        }

        if (!TryBuildRiderCreationInput(
                RiderMissingLimitsAcknowledgement.IsChecked == true,
                out RiderCreationInput? input,
                out string? error))
        {
            PresentWarning("Check rider values", error ?? "The rider draft is incomplete.");
            return;
        }

        if (input.Abilities.Any(static ability => ability.Limit is null)
            && !input.MissingLimitsAcknowledged)
        {
            PresentWarning(
                "Acknowledge blank Limits",
                "Confirm that blank Limits will be stored as database NULL before creating the rider.");
            RiderMissingLimitsAcknowledgement.Focus(FocusState.Programmatic);
            return;
        }

        EditorSessionState session = _session;
        await RunMaintenanceAsync(
            "Rider creation",
            async cancellationToken =>
            {
                RiderCreationPreview preview = await _riderCreationService.PreviewAsync(
                    session.WorkingSqlitePath,
                    input,
                    cancellationToken);
                _riderCreationPreview = preview;
                PopulateRiderCreationReview(preview);
                var dialog = new ContentDialog
                {
                    XamlRoot = WindowRoot.XamlRoot,
                    Title = "Create rider and contract?",
                    Content = $"Create rider {preview.NewCyclistId:N0} ({input.FirstName} {input.LastName}) and contract {preview.NewContractId:N0}. Both rows will be inserted atomically and can be undone together.",
                    PrimaryButtonText = "Create",
                    CloseButtonText = "Keep editing",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                await PrepareMutationWriteAheadAsync(session);
                MaintenanceApplyResult result = await _riderCreationService.ApplyAsync(
                    session.WorkingSqlitePath,
                    preview,
                    cancellationToken);
                await CompleteMaintenanceApplyAsync(session, result);
                if (_session is not null)
                {
                    EditorSessionState currentSession = _session;
                    ResetRiderCreationForSession(currentSession.SessionId);
                    await InitializeRiderCreationAsync(currentSession, cancellationToken);
                }
            });
    }

    private async void PreviewRiderRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMaintenanceSession(out EditorSessionState session))
        {
            return;
        }

        RiderRecoveryTarget target;
        if (RecoveryTeamModeRadioButton.IsChecked == true)
        {
            if (RiderRecoveryTeamComboBox.SelectedItem is not RiderTeamOption team)
            {
                PresentWarning("Choose a team", "Choose one team to resolve its current rider roster.");
                return;
            }

            target = RiderRecoveryTarget.ForTeam(team.TeamId);
        }
        else
        {
            RiderIdParseResult parsed = RiderIdInputParser.Parse(RiderIdsTextBox.Text, "Rider IDs");
            if (!parsed.IsValid)
            {
                PresentWarning("Check rider IDs", parsed.Error!);
                RiderIdsTextBox.Focus(FocusState.Programmatic);
                return;
            }

            target = RiderRecoveryTarget.ForRiderIds(parsed.RiderIds);
        }

        await RunMaintenanceAsync(
            "Rider recovery",
            async cancellationToken =>
            {
                MaintenanceCapability capability = await _riderRecoveryService.CheckCapabilityAsync(
                    session.WorkingSqlitePath,
                    cancellationToken);
                if (!PresentCapabilityFailure(capability))
                {
                    return;
                }

                RiderRecoveryPreview preview = await _riderRecoveryService.PreviewAsync(
                    session.WorkingSqlitePath,
                    target,
                    cancellationToken);
                RiderRecoveryChange[] riderChanges = preview.Changes
                    .Where(static change => change.OldValues != change.NewValues)
                    .ToArray();
                string riderRowLabel = riderChanges.Length == 1 ? "row" : "rows";
                string targetSummary = preview.Target.Kind == RiderRecoveryTargetKind.Team
                    ? $"team {preview.Target.TeamId:N0}"
                    : $"{preview.CyclistIds.Count:N0} distinct rider ID(s)";
                string missingSummary = preview.MissingCyclistIds.Count == 0
                    ? "Every resolved rider has a fitness row."
                    : $"Missing fitness rows: {string.Join(", ", preview.MissingCyclistIds)}.";
                ContentDialog dialog = CreateMaintenancePreviewDialog(
                    "Apply rider recovery preset?",
                    $"Resolved {preview.CyclistIds.Count:N0} rider(s) from {targetSummary}; {preview.FoundCyclistIds.Count:N0} fitness {riderRowLabel} were found and {riderChanges.Length:N0} need changes. {missingSummary} The exact resolved ID set is retained for apply.",
                    riderChanges.Select(change =>
                        $"Rider {change.CyclistId:N0}: {FormatRiderValues(change.OldValues)} -> {FormatRiderValues(change.NewValues)}")
                        .Concat(preview.MissingCyclistIds.Select(id => $"Rider {id:N0}: no fitness row found")));
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (riderChanges.Length > 0)
                {
                    await PrepareMutationWriteAheadAsync(session);
                }

                MaintenanceApplyResult result = await _riderRecoveryService.ApplyAsync(
                    session.WorkingSqlitePath,
                    preview,
                    cancellationToken);
                await CompleteMaintenanceApplyAsync(session, result);
            });
    }

    private async void PreviewJanuaryRepair_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMaintenanceSession(out EditorSessionState session))
        {
            return;
        }

        await RunMaintenanceAsync(
            "January 1 repair",
            async cancellationToken =>
            {
                MaintenanceCapability capability = await _januaryFirstRepairService.CheckCapabilityAsync(
                    session.WorkingSqlitePath,
                    cancellationToken);
                if (!PresentCapabilityFailure(capability))
                {
                    return;
                }

                JanuaryFirstRepairPreview preview = await _januaryFirstRepairService.PreviewAsync(
                    session.WorkingSqlitePath,
                    cancellationToken);
                string repairRowLabel = preview.RowCount == 1 ? "row" : "rows";
                ContentDialog dialog = CreateMaintenancePreviewDialog(
                    "Apply January 1 repair?",
                    $"Database date: {preview.CurrentDate:yyyy-MM-dd}. The repair will delete all {preview.RowCount:N0} {repairRowLabel} from DYN_result_season_stage in the isolated working database.",
                    [$"Rows to delete from DYN_result_season_stage: {preview.RowCount:N0}"]);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (preview.RowCount > 0)
                {
                    await PrepareMutationWriteAheadAsync(session);
                }

                MaintenanceApplyResult result = await _januaryFirstRepairService.ApplyAsync(
                    session.WorkingSqlitePath,
                    preview,
                    cancellationToken);
                await CompleteMaintenanceApplyAsync(session, result);
            });
    }

    private async void PreviewCountryQuotas_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMaintenanceSession(out EditorSessionState session))
        {
            return;
        }

        await RunMaintenanceAsync(
            "Country quota update",
            async cancellationToken =>
            {
                MaintenanceCapability capability = await _countryQuotaMaintenanceService.CheckCapabilityAsync(
                    session.WorkingSqlitePath,
                    cancellationToken);
                if (!PresentCapabilityFailure(capability))
                {
                    return;
                }

                CountryQuotaPreview preview = await _countryQuotaMaintenanceService.PreviewAsync(
                    session.WorkingSqlitePath,
                    cancellationToken);
                var dialog = new CountryQuotaPreviewDialog(preview)
                {
                    XamlRoot = WindowRoot.XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (dialog.ChangeCount > 0)
                {
                    await PrepareMutationWriteAheadAsync(session);
                }

                MaintenanceApplyResult result = await _countryQuotaMaintenanceService.ApplyAsync(
                    session.WorkingSqlitePath,
                    dialog.SourcePreview,
                    cancellationToken);
                await CompleteMaintenanceApplyAsync(session, result);
            });
    }

    private async Task RunMaintenanceAsync(string operationName, Func<CancellationToken, Task> operation)
    {
        if (!TryBeginOperation(operationName, out CancellationToken cancellationToken))
        {
            return;
        }

        ViewModel.State = ShellOperationState.Preview;
        ViewModel.Status = $"Checking {operationName.ToLowerInvariant()} capability…";
        CancelOperationButton.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;
        try
        {
            await operation(cancellationToken);
            RestoreSessionState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_operationMutationPrepared)
            {
                await HandleUnconfirmedMutationCancellationAsync(
                    "Maintenance cancelled",
                    "The app could not confirm whether the maintenance transaction finished. It kept the working session for recovery so that a change that finished late is not overlooked. Review the working copy before making more changes.");
            }
            else
            {
                RestoreSessionState();
                PresentWarning("Maintenance cancelled", "No database mutation was started.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (_operationMutationPrepared)
            {
                await HandlePreparedMutationFailureAsync(
                    "Maintenance operation failed",
                    SafeFailureMessage(
                        exception,
                        "The app could not confirm whether the change finished. You can still recover this session; review the working copy before trying again."));
            }
            else
            {
                RestoreSessionState();
                if (TryGetMaintenanceCapabilityMessage(exception, out string? capabilityMessage))
                {
                    PresentWarning("Tool unavailable for this database", capabilityMessage);
                }
                else
                {
                    PresentError(
                        "Maintenance operation failed",
                        SafeFailureMessage(exception, "Review the open database and try again."));
                }
            }
        }
        finally
        {
            CancelOperationButton.Visibility = Visibility.Collapsed;
            EndOperation();
        }
    }

    private async Task CompleteMaintenanceApplyAsync(
        EditorSessionState expectedSession,
        MaintenanceApplyResult result)
    {
        EditorSessionState currentSession = _session
            ?? throw new InvalidOperationException("The active working session closed during maintenance.");
        if (currentSession.SessionId != expectedSession.SessionId
            || !currentSession.WorkingSqlitePath.Equals(
                expectedSession.WorkingSqlitePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active working session changed during maintenance.");
        }

        if (result.AffectedRows > 0)
        {
            if (!_operationMutationPrepared || !currentSession.IsDirty)
            {
                _session = currentSession with { IsDirty = true };
                try
                {
                    _session = await _workspaceService.MarkDirtyAsync(_session, CancellationToken.None);
                }
                catch (Exception exception) when (IsExpectedOperationFailure(exception))
                {
                    await HandlePostCommitFailureAsync(
                        "Maintenance changed the database before the app could finish preparing its recovery information.");
                    return;
                }
            }

            ViewModel.State = ShellOperationState.Dirty;
            InvalidateTableCaches();

            if (result.HistoryOperation is null || result.UndoGuards is null)
            {
                await HandlePostCommitFailureAsync(
                    "Maintenance changed the database, but the app did not receive the information needed for Undo.");
                return;
            }

            if (_editHistory is null)
            {
                await HandlePostCommitFailureAsync(
                    "Maintenance changed the database, but the session history was unavailable.");
                return;
            }

            try
            {
                _editHistory.Record(result.HistoryOperation, result.UndoGuards);
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                await HandlePostCommitFailureAsync(
                    "Maintenance changed the database, but the app could not save the information needed for Undo.");
                return;
            }

            UpdateHistoryButtons();
            string changedRowLabel = result.AffectedRows == 1 ? "row" : "rows";
            PresentSuccess("Maintenance applied", $"{result.AffectedRows:N0} {changedRowLabel} changed in the isolated working copy.");
        }
        else
        {
            RestoreSessionState();
            PresentSuccess("No changes needed", "The working database already matches the previewed maintenance state.");
        }

        await RefreshSelectedTableAsync();
    }

    private bool TryGetMaintenanceSession(out EditorSessionState session)
    {
        if (_session is not null && !ViewModel.IsBusy)
        {
            session = _session;
            return true;
        }

        session = null!;
        PresentWarning("Open a CDB first", "Maintenance tools operate only on an isolated working database.");
        return false;
    }

    private bool PresentCapabilityFailure(MaintenanceCapability capability)
    {
        if (capability.IsEnabled)
        {
            return true;
        }

        var details = new List<string>(capability.Reasons);
        if (capability.MissingTables.Count > 0)
        {
            details.Add($"Missing tables: {string.Join(", ", capability.MissingTables)}");
        }

        if (capability.MissingColumns.Count > 0)
        {
            details.Add($"Missing columns: {string.Join(", ", capability.MissingColumns)}");
        }

        PresentWarning(
            "Tool unavailable for this database",
            details.Count == 0
                ? "The open database does not support this maintenance tool."
                : string.Join(" ", details));
        return false;
    }

    private void TableTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is not TabViewItem tab)
        {
            return;
        }

        TableTabContext? closingContext = tab.Tag as TableTabContext;
        string? tableName = closingContext?.Schema.Name;
        string[] openTablesBeforeClose = sender.TabItems
            .OfType<TabViewItem>()
            .Select(static item => (item.Tag as TableTabContext)?.Schema.Name)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .ToArray();
        string? selectedBeforeClose =
            ((sender.SelectedItem as TabViewItem)?.Tag as TableTabContext)?.Schema.Name;
        if (_activeTableLoad is not null)
        {
            ShellOperationState returnState = CancelActiveTableLoad()?.ReturnState ??
                (_session?.IsDirty == true ? ShellOperationState.Dirty : ShellOperationState.Ready);
            FinishTableLoadPresentation(returnState);
        }
        CancelVirtualCount(tab);
        sender.TabItems.Remove(tab);
        closingContext?.Coordinator.Dispose();
        _gridBindingSession.ClearIfBoundTo(tab);
        if (tableName is not null)
        {
            _tableTabs.Remove(tableName);
            TableTabState? state = ViewModel.Tabs.FirstOrDefault(item =>
                item.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            if (state is not null)
            {
                ViewModel.Tabs.Remove(state);
            }

            TabCloseSelectionResolution resolution = TabCloseSelectionReconciler.Resolve(
                openTablesBeforeClose,
                tableName,
                selectedBeforeClose,
                ((sender.SelectedItem as TabViewItem)?.Tag as TableTabContext)?.Schema.Name,
                ViewModel.Tables.Select(static table => table.Name).ToArray());
            TabViewItem? selectedTab = resolution.SelectedTable is not null &&
                _tableTabs.TryGetValue(resolution.SelectedTable, out TabViewItem? candidate) &&
                sender.TabItems.Contains(candidate)
                    ? candidate
                    : null;
            if (!ReferenceEquals(sender.SelectedItem, selectedTab))
            {
                sender.SelectedItem = selectedTab;
            }

            SynchronizeTableSelection(selectedTab is null ? null : resolution.SidebarTable);
        }
        else
        {
            TabViewItem? selectedTab = sender.SelectedItem as TabViewItem;
            if (selectedTab is not null && !sender.TabItems.Contains(selectedTab))
            {
                selectedTab = null;
            }

            SynchronizeTableSelectionToTab(selectedTab);
        }

        if (sender.TabItems.Count == 0)
        {
            TableGrid.Clear();
            SetTableGridPresented(isPresented: false);
            ShowEmptyState(
                "Choose a table",
                "Select a table or view from the list to load its first 100 rows.",
                allowOpen: false);
            ViewModel.TableSummary = "No table selected";
        }

        ViewModel.Status = "Table tab closed; database edits are still retained.";
    }

    private ContentDialog CreateDialog(
        string title,
        string content,
        string primaryButtonText,
        string secondaryButtonText,
        string closeButtonText) => new()
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Primary,
        };

    private ContentDialog CreateMaintenancePreviewDialog(
        string title,
        string summary,
        IEnumerable<string> changes)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 680,
        });
        panel.Children.Add(new ListView
        {
            ItemsSource = changes.ToArray(),
            MaxHeight = 420,
            MinWidth = 640,
            SelectionMode = ListViewSelectionMode.None,
        });
        return new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
    }

    private static string FormatRiderValues(RiderRecoveryValues values) =>
        $"Fit {values.Fit:G}, Injury {values.Injury:G}, Days {values.InjuryDays:N0}, " +
        $"Fatigue {values.PhysicalFatigue:G}, Freshness {values.Freshness:G}, Preparation {values.Preparation:G}";

    private void SetBusyPresentation(string title, string message, bool allowCancellation = true)
    {
        ViewModel.State = ShellOperationState.Loading;
        ViewModel.Status = message;
        BusyIndicator.Visibility = Visibility.Visible;
        SetTableLoadingSurface(isVisible: false);
        EmptyState.Visibility = Visibility.Collapsed;
        SetTableGridPresented(isPresented: false);
        CancelOperationButton.Visibility = allowCancellation
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = allowCancellation;
        PresentInformation(title, message);
    }

    private void SetReadyState()
    {
        ViewModel.HasDatabase = _session is not null;
        ViewModel.State = _session?.IsDirty == true
            ? ShellOperationState.Dirty
            : ShellOperationState.Ready;
        BusyIndicator.Visibility = Visibility.Collapsed;
        SetTableLoadingSurface(isVisible: false);
        CancelOperationButton.Visibility = Visibility.Collapsed;
        if (TableTabs.SelectedItem is TabViewItem selected)
        {
            EnsureTabBound(selected);
        }
        else if (_catalog?.Tables.Count > 0)
        {
            ViewModel.Status = "Choose a table.";
        }
    }

    private void RestoreSessionState()
    {
        if (_session is null)
        {
            ResetToNoFile();
            return;
        }

        SetReadyState();
    }

    private void ResetToNoFile()
    {
        CancelActiveTableLoad();
        DisposeTableCoordinators();
        _session = null;
        _catalog = null;
        ResetMaintenanceTargetsForSession(sessionId: null);
        _allTables.Clear();
        _tableTabs.Clear();
        DetachEditHistory();
        ViewModel.Tables.Clear();
        ViewModel.Tabs.Clear();
        ViewModel.HasDatabase = false;
        ViewModel.State = ShellOperationState.NoFile;
        ViewModel.DatabaseName = "No database open";
        ViewModel.Status = "Open a CDB file to begin.";
        ViewModel.TableSummary = "No table selected";
        ViewModel.PageSizeLabel = "100 rows/page";
        TableSearchBox.Text = string.Empty;
        TableTabs.TabItems.Clear();
        TableGrid.Clear();
        SetTableGridPresented(isPresented: false);
        BusyIndicator.Visibility = Visibility.Collapsed;
        SetTableLoadingSurface(isVisible: false);
        ApplyTableFilter(string.Empty);
        ShowEmptyState(
            "Open a CDB file",
            "The original stays untouched while you inspect and edit an isolated working copy.",
            allowOpen: true);
    }

    private void ShowEmptyState(string title, string description, bool allowOpen)
    {
        EmptyStateTitle.Text = title;
        EmptyStateDescription.Text = description;
        AutomationProperties.SetName(EmptyState, title);
        EmptyStateOpenButton.Visibility = allowOpen ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateReleaseLabel.Visibility = allowOpen ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void HideEmptyState() => EmptyState.Visibility = Visibility.Collapsed;

    private void SynchronizeTableSelectionToTab(TabViewItem? tab)
    {
        string? tableName = (tab?.Tag as TableTabContext)?.Schema.Name;
        SynchronizeTableSelection(tableName);
    }

    private void SynchronizeTableSelection(string? tableName)
    {
        _suppressTableSelection = true;
        try
        {
            TablesList.SelectedItem = tableName is null
                ? null
                : ViewModel.Tables.FirstOrDefault(table =>
                    table.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressTableSelection = false;
        }
    }

    private void PresentInformation(string title, string message) =>
        PresentInfoBar(title, message, InfoBarSeverity.Informational);

    private void PresentSuccess(string title, string message) =>
        PresentInfoBar(title, message, InfoBarSeverity.Success);

    private void PresentWarning(string title, string message) =>
        PresentInfoBar(title, message, InfoBarSeverity.Warning);

    private void PresentError(string title, string message) =>
        PresentInfoBar(title, message, InfoBarSeverity.Error);

    private void PresentInfoBar(string title, string message, InfoBarSeverity severity)
    {
        OperationInfo.Title = title;
        OperationInfo.Message = message;
        OperationInfo.Severity = severity;
        OperationInfo.IsOpen = true;
    }

    private bool TryBeginOperation(string operationName, out CancellationToken cancellationToken)
    {
        if (_activeTableLoad is not null)
        {
            cancellationToken = default;
            PresentWarning(
                $"{operationName} is unavailable",
                "Wait for the current table load to finish, or cancel it before trying again.");
            return false;
        }

        if (!_operationGate.TryEnter(_lifetimeCancellation.Token, out var lease))
        {
            cancellationToken = default;
            PresentWarning(
                $"{operationName} is unavailable",
                "Wait for the current database operation to finish, or cancel it before trying again.");
            return false;
        }

        _operationLease = lease;
        _operationMutationPrepared = false;
        cancellationToken = lease!.Token;
        ViewModel.IsOperationExclusive = true;
        SetConflictingCommandsEnabled(isEnabled: false);
        SetTableQueryControlsEnabled(isEnabled: false);
        UpdateCreateRiderButtonState();
        return true;
    }

    private void EndOperation()
    {
        CancelOperationButton.Visibility = Visibility.Collapsed;
        _operationLease?.Dispose();
        _operationLease = null;
        _operationMutationPrepared = false;
        if (_disposed)
        {
            return;
        }

        ViewModel.IsOperationExclusive = false;
        SetConflictingCommandsEnabled(isEnabled: true);
        SetTableQueryControlsEnabled(isEnabled: true);
        UpdateCreateRiderButtonState();
    }

    private void SetConflictingCommandsEnabled(bool isEnabled)
    {
        OpenButton.IsEnabled = isEnabled;
        SaveButton.IsEnabled = isEnabled && _session is not null;
        SaveAsButton.IsEnabled = isEnabled && _session is not null;
        if (!isEnabled)
        {
            UndoButton.IsEnabled = false;
            RedoButton.IsEnabled = false;
            SetRowActionsEnabled(canInsert: false, canMutateSelection: false);
            return;
        }

        UpdateHistoryButtons();
        if (TableTabs.SelectedItem is TabViewItem tab && tab.Tag is TableTabContext context)
        {
            bool canInsert = context.Schema.EditCapability == TableEditCapability.Editable;
            bool canMutateSelection = context.Schema.EditCapability == TableEditCapability.Editable &&
                _selectedRow?.Identity is not null;
            SetRowActionsEnabled(canInsert, canMutateSelection);
        }
    }

    private void SetRowActionsEnabled(bool canInsert, bool canMutateSelection)
    {
        SetInsertRowActionsEnabled(canInsert);
        SetSelectedRowActionsEnabled(canMutateSelection);
    }

    private void SetInsertRowActionsEnabled(bool isEnabled)
    {
        InsertRowButton.IsEnabled = isEnabled;
        OverflowInsertRowButton.IsEnabled = isEnabled;
    }

    private void SetSelectedRowActionsEnabled(bool isEnabled)
    {
        EditRowButton.IsEnabled = isEnabled;
        OverflowEditRowButton.IsEnabled = isEnabled;
        DeleteRowButton.IsEnabled = isEnabled;
        OverflowDeleteRowButton.IsEnabled = isEnabled;
    }

    private void SetTableQueryControlsEnabled(bool isEnabled)
    {
        TablesList.IsEnabled = isEnabled;
        TableTabs.IsEnabled = isEnabled;
        CurrentTableSearchBox.IsEnabled = isEnabled;
        FiltersButton.IsEnabled = isEnabled;
        SortButton.IsEnabled = isEnabled;
        PageSizeBox.IsEnabled = isEnabled;
        if (isEnabled && TableTabs.SelectedItem is TabViewItem tab &&
            tab.Tag is TableTabContext context)
        {
            UpdateTabChrome(tab, context);
        }
        else if (!isEnabled)
        {
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
        }
    }

    private TableLoadRequest BeginQuery(string tableName, bool showLoadingSurface)
    {
        _activeTableLoad?.Cancellation.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var request = new TableLoadRequest(
            _tableLoadGate.Begin(),
            cancellation,
            TableTabs.SelectedItem as TabViewItem,
            _session?.IsDirty == true ? ShellOperationState.Dirty : ShellOperationState.Ready);
        _activeTableLoad = request;
        if (showLoadingSurface)
        {
            ShowTableLoading(request, tableName);
        }

        return request;
    }

    private void ShowTableLoading(TableLoadRequest request, string tableName)
    {
        if (!ReferenceEquals(_activeTableLoad, request) || !request.Lease.IsCurrent)
        {
            return;
        }

        CaptureCurrentTabState(request.PreviousTab);
        ViewModel.State = ShellOperationState.Loading;
        ViewModel.Status = $"Loading {tableName}…";
        TableLoadingTitle.Text = $"Loading {tableName}…";
        SetTableLoadingSurface(isVisible: true);
        SetTableGridPresented(isPresented: false);
        EmptyState.Visibility = Visibility.Collapsed;
        BusyIndicator.Visibility = Visibility.Collapsed;
        CancelOperationButton.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;
        SetConflictingCommandsEnabled(isEnabled: false);
    }

    private void RestorePreviousTable(TableLoadRequest request, string status)
    {
        if (!ReferenceEquals(_activeTableLoad, request) || !request.Lease.IsCurrent)
        {
            return;
        }

        TabViewItem? previousTab = request.PreviousTab is { } candidate &&
            TableTabs.TabItems.Contains(candidate)
                ? candidate
                : TableTabs.TabItems.OfType<TabViewItem>().FirstOrDefault();
        if (previousTab?.Tag is TableTabContext previousContext)
        {
            _suppressTabSelection = true;
            try
            {
                TableTabs.SelectedItem = previousTab;
            }
            finally
            {
                _suppressTabSelection = false;
            }

            _suppressTableSelection = true;
            try
            {
                TablesList.SelectedItem = ViewModel.Tables.FirstOrDefault(item =>
                    item.Name.Equals(previousContext.Schema.Name, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressTableSelection = false;
            }

            EnsureTabBound(previousTab);
            TableGrid.Focus(FocusState.Programmatic);
        }
        else
        {
            _suppressTableSelection = true;
            TablesList.SelectedItem = null;
            _suppressTableSelection = false;
            SetTableGridPresented(isPresented: false);
            ShowEmptyState(
                "No table selected",
                "Choose a table from the list to continue.",
                allowOpen: false);
        }

        ViewModel.Status = status;
    }

    private void CaptureCurrentTabState(TabViewItem? tab)
    {
        if (_catalog is null ||
            tab?.Tag is not TableTabContext context ||
            !_gridBindingSession.IsBoundTo(tab))
        {
            return;
        }

        TableViewState viewState = TableGrid.CaptureViewState(
            _catalog.SchemaSignature,
            context.Schema.Name,
            context.Sorts,
            _preferences.Density);
        tab.Tag = context with
        {
            ViewState = viewState,
            Selection = TableGrid.CaptureSelection(),
            Viewport = TableGrid.CaptureViewport(),
        };
        _gridBindingSession.UpdateBoundViewState(tab, context.Page.Rows, viewState);
    }

    private async Task PersistActiveTableStateAsync(CancellationToken cancellationToken)
    {
        if (TableTabs.SelectedItem is not TabViewItem tab)
        {
            return;
        }

        Guid? sessionId = _session?.SessionId;
        CaptureCurrentTabState(tab);
        await PersistCapturedTableStateAsync(
            tab.Tag as TableTabContext,
            sessionId,
            cancellationToken);
    }

    private async Task PersistCapturedTableStateAsync(
        TableTabContext? context,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        if (context?.ViewState is not { } viewState)
        {
            return;
        }

        try
        {
            await _settingsStore.SaveTableViewStateAsync(viewState, cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown owns the settings-write cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (_session?.SessionId == sessionId)
            {
                PresentWarning(
                    "Table layout was not saved",
                    SafeFailureMessage(exception, "Database rows were not affected."));
            }
        }
    }

    private void EndQuery(TableLoadRequest request)
    {
        if (!ReferenceEquals(_activeTableLoad, request) || !request.Lease.IsCurrent)
        {
            request.Cancellation.Dispose();
            return;
        }

        _activeTableLoad = null;
        request.Cancellation.Dispose();
        FinishTableLoadPresentation(request.ReturnState);
    }

    private void FinishTableLoadPresentation(ShellOperationState returnState)
    {
        SetTableLoadingSurface(isVisible: false);
        ViewModel.State = returnState;
        if (_operationLease is null)
        {
            CancelOperationButton.Visibility = Visibility.Collapsed;
            SetConflictingCommandsEnabled(isEnabled: true);
        }

        if (TableTabs.SelectedItem is TabViewItem selected &&
            selected.Tag is TableTabContext context &&
            _gridBindingSession.IsBoundTo(selected))
        {
            SetTableGridPresented(isPresented: true);
            UpdateCountAndPagingChrome(selected, context);
        }
    }

    private void SetTableLoadingSurface(bool isVisible)
    {
        TableLoadingState.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetTableGridPresented(bool isPresented)
    {
        // WinUI.TableView 1.4.1 spins its dispatcher while a loaded, collapsed
        // control waits for ItemsPanelRoot. Keep the control in layout and use a
        // non-interactive, automation-hidden presentation during overlays instead.
        TableGrid.Visibility = Visibility.Visible;
        TableGrid.Opacity = isPresented ? 1 : 0;
        TableGrid.IsEnabled = isPresented;
        TableGrid.IsHitTestVisible = isPresented;
        AutomationProperties.SetAccessibilityView(
            TableGrid,
            isPresented ? AccessibilityView.Content : AccessibilityView.Raw);
    }

    private static void EnsureTableLoadCurrent(
        TableLoadRequest? request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request?.Lease.ThrowIfSuperseded(cancellationToken);
    }

    private void DisposeCancellationSources()
    {
        CancelActiveTableLoad();
        foreach (CancellationTokenSource cancellation in _countCancellations.Values)
        {
            cancellation.Cancel();
        }
        _countCancellations.Clear();
        _operationLease?.Cancel();
        _operationLease?.Dispose();
        _operationLease = null;
        _operationGate.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private TableLoadRequest? CancelActiveTableLoad()
    {
        TableLoadRequest? request = _activeTableLoad;
        _tableLoadGate.Invalidate();
        request?.Cancellation.Cancel();
        _activeTableLoad = null;
        return request;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeTableCoordinators();
        DisposeCancellationSources();
        GC.SuppressFinalize(this);
    }

    private void DisposeTableCoordinators()
    {
        foreach (TabViewItem tab in _tableTabs.Values)
        {
            CancelVirtualCount(tab);
        }

        foreach (VirtualTableQueryCoordinator coordinator in _tableTabs.Values
                     .Select(static tab => tab.Tag)
                     .OfType<TableTabContext>()
                     .Select(static context => context.Coordinator)
                     .Distinct())
        {
            coordinator.Dispose();
        }

        _gridBindingSession.Reset();
    }

    private async Task TryCloseFailedOpenAsync(EditorSessionState session)
    {
        try
        {
            await _workspaceService.CloseAsync(
                session,
                discardDirtySession: session.IsDirty,
                CancellationToken.None);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            // The workspace service will expose any dirty working session at the next startup.
        }
    }

    private static bool IsCdbPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path).Equals(".cdb", StringComparison.OrdinalIgnoreCase);

    private static string FormatCount(long count) => $"{count:N0} rows";

    private async Task RefreshSelectedTableAsync()
    {
        if (TableTabs.SelectedItem is not TabViewItem tab ||
            tab.Tag is not TableTabContext context)
        {
            return;
        }

        await OpenTableAsync(
            context.Schema.Name,
            context.SearchText,
            _lifetimeCancellation.Token,
            context.Filter,
            context.Page.Request.Limit,
            context.Page.Request.Offset,
            context.Sorts,
            context.FilterDefinition);
    }

    private static bool IsExpectedOperationFailure(Exception exception) => exception is
        PcmCdbEditor.Application.CdbConversionException or
        SqliteException or
        DBConcurrencyException or
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        System.Data.DataException or
        System.Data.Common.DbException or
        InvalidDataException or
        InvalidOperationException or
        ArgumentException;

    private static string SafeFailureMessage(Exception exception, string fallback) => exception switch
    {
        PcmCdbEditor.Application.CdbConversionException conversion =>
            $"{conversion.Failure.Message} {fallback}",
        UnauthorizedAccessException => $"Access was denied. {fallback}",
        IOException => $"A file operation failed. {fallback}",
        SqliteException => $"The working database rejected the operation. {fallback}",
        DBConcurrencyException => $"The row changed since it was loaded. Reload it before trying again. {fallback}",
        InvalidDataException => $"The working database did not pass validation. {fallback}",
        ArgumentException => $"The selected path or query was invalid. {fallback}",
        InvalidOperationException invalidOperation
            when invalidOperation.Message.Equals(
                "This table has no columns that support text search.",
                StringComparison.Ordinal) => $"{invalidOperation.Message} {fallback}",
        _ => fallback,
    };

    private static bool TryGetMaintenanceCapabilityMessage(
        Exception exception,
        out string message)
    {
        if (exception is InvalidOperationException januaryGate &&
            januaryGate.Message.StartsWith(
                "The season-stage repair is available only on January 1;",
                StringComparison.Ordinal))
        {
            message = "The January 1 season-stage repair is available only when the in-game date is January 1.";
            return true;
        }

        if (exception is InvalidOperationException novemberGate &&
            novemberGate.Message.StartsWith(
                "Country quotas can be maintained only during November;",
                StringComparison.Ordinal))
        {
            message = "World and European country quotas are available only when the in-game date is in November.";
            return true;
        }

        if (exception is InvalidDataException invalidDate &&
            invalidDate.Message.Equals(
                "GAM_config.gene_i_date must contain exactly one valid date in yyyyMMdd format.",
                StringComparison.Ordinal))
        {
            message = "This maintenance tool requires exactly one valid game date in GAM_config.gene_i_date using yyyyMMdd format.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    private sealed record FilterRuleDraft(
        int Number,
        string ColumnName,
        FilterOperator Operator,
        string Value);

    private sealed record TableFilterDefinition
    {
        public TableFilterDefinition(
            IEnumerable<FilterRuleDraft> quickRules,
            IEnumerable<FilterRuleDraft> advancedRules,
            string advancedExpression)
        {
            QuickRules = quickRules.ToArray();
            AdvancedRules = advancedRules.ToArray();
            AdvancedExpression = advancedExpression ?? string.Empty;
        }

        public static TableFilterDefinition Empty { get; } = new([], [], string.Empty);

        public FilterRuleDraft[] QuickRules { get; }

        public FilterRuleDraft[] AdvancedRules { get; }

        public string AdvancedExpression { get; }

        public int RuleCount => QuickRules.Length + AdvancedRules.Length;
    }

    private sealed record TableFilterDialogOutcome(
        TableFilterDefinition Definition,
        FilterExpression? Filter);

    private sealed record FilterOperatorChoice(FilterOperator Operator, string Label);

    private sealed class FilterRuleEditor(
        int number,
        Grid root,
        TextBlock label,
        ComboBox columnPicker,
        ComboBox operatorPicker,
        TextBox valueBox,
        Button removeButton)
    {
        public int Number { get; } = number;

        public Grid Root { get; } = root;

        public TextBlock Label { get; } = label;

        public ComboBox ColumnPicker { get; } = columnPicker;

        public ComboBox OperatorPicker { get; } = operatorPicker;

        public TextBox ValueBox { get; } = valueBox;

        public Button RemoveButton { get; } = removeButton;

        public FilterRuleDraft ToDraft(int number) => new(
            number,
            ColumnPicker.SelectedItem as string ?? string.Empty,
            OperatorPicker.SelectedItem is FilterOperatorChoice choice
                ? choice.Operator
                : FilterOperator.Equals,
            ValueBox.Text);
    }

    private sealed class SortRuleEditor(
        Grid root,
        TextBlock priorityLabel,
        ComboBox columnPicker,
        ComboBox directionPicker,
        Button moveUpButton,
        Button moveDownButton,
        Button removeButton)
    {
        public Grid Root { get; } = root;

        public TextBlock PriorityLabel { get; } = priorityLabel;

        public ComboBox ColumnPicker { get; } = columnPicker;

        public ComboBox DirectionPicker { get; } = directionPicker;

        public Button MoveUpButton { get; } = moveUpButton;

        public Button MoveDownButton { get; } = moveDownButton;

        public Button RemoveButton { get; } = removeButton;
    }

    private sealed record TableLoadRequest(
        LatestRequestGate.RequestLease Lease,
        CancellationTokenSource Cancellation,
        TabViewItem? PreviousTab,
        ShellOperationState ReturnState)
    {
        public CancellationToken Token { get; } = Cancellation.Token;
    }

    private sealed record TableTabContext(
        TableSchema Schema,
        TablePage Page,
        string SearchText,
        FilterExpression? Filter,
        TableFilterDefinition FilterDefinition,
        IReadOnlyList<SortDescriptor> Sorts,
        TableViewState? ViewState,
        VirtualTableQueryCoordinator Coordinator,
        ForeignKeyDisplayMode ForeignKeyDisplayMode,
        TableRowCountState CountState,
        bool IsInvalidated,
        GridSelection Selection,
        GridViewport Viewport);
}
