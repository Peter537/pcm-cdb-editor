using System.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private const string ReferenceOpenedTitle = "Referenced row opened";

    private ReferenceSourceBookmark? _relationshipReturn;
    private TableLoadRequest? _relationshipNavigationLoad;

    private bool CanNavigateRelationships => !_disposed && _session is not null && _catalog is not null
        && _operationLease is null && _activeTableLoad is null && TablesView.Visibility == Visibility.Visible;

    private void UpdateRelationshipNavigationCommands()
    {
        bool tablesVisible = TablesView.Visibility == Visibility.Visible;
        OpenReferencedRowButton.Visibility = tablesVisible ? Visibility.Visible : Visibility.Collapsed;
        OpenReferencedRowButton.IsEnabled = _catalog is { } catalog
            && TryGetSelectedReferenceSource(out _, out TableTabContext context, out TypedRow row)
            && GetReferenceChoices(catalog, context.Schema, row).Any();

        bool canReturn = _relationshipReturn is { } source && _session?.SessionId == source.SessionId;
        ReturnToReferenceSourceButton.Visibility = tablesVisible && canReturn ? Visibility.Visible : Visibility.Collapsed;
        ReturnToReferenceSourceButton.IsEnabled = canReturn && CanNavigateRelationships;
    }

    private void RelationshipTablesVisibilityChanged()
    {
        if (TablesView.Visibility != Visibility.Visible)
        {
            _relationshipNavigationLoad?.Cancellation.Cancel();
        }

        UpdateRelationshipNavigationCommands();
    }

    private bool TryGetSelectedReferenceSource(out TabViewItem tab, out TableTabContext context, out TypedRow row)
    {
        if (CanNavigateRelationships && TableTabs.SelectedItem is TabViewItem selectedTab
            && TryGetPresentedRow(out context, out row) && !context.IsInvalidated)
        {
            tab = selectedTab;
            return true;
        }

        tab = null!;
        context = null!;
        row = null!;
        return false;
    }

    private static IEnumerable<ReferenceChoice> GetReferenceChoices(DatabaseSchemaCatalog catalog, TableSchema table, TypedRow row)
    {
        foreach (string column in table.Relationships.Select(static relation => relation.SourceColumn)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (row.Values.TryGetValue(column, out SqliteValue key)
                && (key.Kind is SqliteValueKind.Integer or SqliteValueKind.Text
                    || key.Kind == SqliteValueKind.Real && double.IsFinite(key.RealValue))
                && RelationshipMetadata.TryGetLookupRelation(catalog, table, column, out ForeignKeyRelation relation, out _))
            {
                yield return new ReferenceChoice(column, relation.TargetTable, key);
            }
        }
    }

    private async void OpenReferencedRow_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _catalog is not { } catalog
            || !TryGetSelectedReferenceSource(out TabViewItem sourceTab, out TableTabContext context, out TypedRow row))
        {
            return;
        }

        if (TableGrid.IsEditing)
        {
            PresentWarning("Finish the cell edit", "Commit or cancel the current cell edit before opening a referenced row.");
            return;
        }

        ReferenceChoice[] choices = GetReferenceChoices(catalog, context.Schema, row).ToArray();
        string? currentColumn = TableGrid.GetCurrentColumnForRow(row);
        ReferenceChoice? choice = choices.FirstOrDefault(candidate =>
            candidate.ColumnName.Equals(currentColumn, StringComparison.OrdinalIgnoreCase));
        if (choice is null && choices.Length != 0)
        {
            choice = await ChooseReferenceAsync(choices);
        }

        if (choice is null || _session?.SessionId != session.SessionId
            || !TryGetSelectedReferenceSource(out TabViewItem currentTab, out TableTabContext currentContext, out TypedRow currentRow)
            || !ReferenceEquals(currentTab, sourceTab) || !ReferenceEquals(currentContext.Display.Rows, context.Display.Rows)
            || !ReferenceEquals(currentRow, row))
        {
            return;
        }

        await RunReferenceTableLoadAsync(choice.TargetTable, "Could not open the referenced row", async request =>
        {
            if (sourceTab.Tag is not TableTabContext captured)
            {
                throw new InvalidOperationException("The source table is unavailable.");
            }

            var bookmark = new ReferenceSourceBookmark(
                session.SessionId, captured.Schema.Name, captured.SearchText, captured.Filter, captured.FilterDefinition,
                captured.Sorts, captured.Display.RowDisplay, captured.Display.Offset, captured.ViewState,
                captured.Selection, captured.Viewport);
            RelationshipLookupResult lookup = await _relationshipLookup.ResolveAsync(
                session.WorkingSqlitePath, catalog, context.Schema.Name, choice.ColumnName, choice.Key, request.Token);
            request.Lease.ThrowIfSuperseded(request.Token);
            if (lookup.Option is not { } target)
            {
                RestorePreviousTable(request, "The source table remains available.");
                PresentWarning("Referenced row unavailable", lookup.UnavailableReason
                    ?? "The stored value did not identify exactly one referenced row.");
                return;
            }

            var filter = new FilterCondition(target.TargetColumn, FilterOperator.Equals, target.Key);
            var filterDefinition = new TableFilterDefinition([], [], string.Empty, referenceFilter: filter);
            await OpenTableAsync(
                target.TargetTable, string.Empty, request.Token, filter,
                TableRowDisplayChoice.FromPageSize(_preferences.PageSize), filterDefinition: filterDefinition,
                loadRequest: request, presentation: new TableOpenPresentation(display => SelectReferencedRow(display, target)));
            request.Lease.ThrowIfSuperseded(request.Token);
            _relationshipReturn = bookmark;
            SynchronizeTableSelectionToTab(TableTabs.SelectedItem as TabViewItem);
            TableGrid.Focus(FocusState.Programmatic);
            PresentInformation(ReferenceOpenedTitle,
                $"The {target.TargetTable} tab is filtered to the referenced row. Use Return to source in the command menu to restore {bookmark.TableName}.");
            _tableQueryBanner = true;
        });
    }

    private async Task<ReferenceChoice?> ChooseReferenceAsync(IReadOnlyList<ReferenceChoice> choices)
    {
        var picker = new ComboBox
        {
            Header = "Relationship",
            ItemsSource = choices,
            DisplayMemberPath = nameof(ReferenceChoice.Display),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(picker, "Relationship to open");
        AutomationProperties.SetAutomationId(picker, "ReferencedRowRelationship");
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Open referenced row",
            Content = picker,
            PrimaryButtonText = "Open row",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem is ReferenceChoice;
        dialog.Opened += (_, _) => picker.Focus(FocusState.Programmatic);
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? picker.SelectedItem as ReferenceChoice : null;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError("Could not choose a relationship", SafeFailureMessage(exception, "The source table remains available."));
            return null;
        }
    }

    private static (GridSelection Selection, GridViewport Viewport) SelectReferencedRow(
        TableDisplayWindow display, RelationshipLookupOption target)
    {
        if (display.Rows.Count != 1 || display.Rows[0] is not { Identity: { } identity } row
            || !identity.Equals(target.TargetIdentity)
            || !row.Values.TryGetValue(target.TargetColumn, out SqliteValue key) || key != target.Key)
        {
            throw new DBConcurrencyException("The referenced row changed during navigation.");
        }

        return (new GridSelection(identity, target.TargetColumn, [identity]), new GridViewport(identity, 0));
    }

    private async void ReturnToReferenceSource_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateRelationships || _relationshipReturn is not { } source || _session?.SessionId != source.SessionId)
        {
            return;
        }

        if (TableGrid.IsEditing)
        {
            PresentWarning("Finish the cell edit", "Commit or cancel the current cell edit before returning to the source.");
            return;
        }

        if (TryReturnToLoadedSource(source))
        {
            FinishReferenceReturn(source);
            return;
        }

        await RunReferenceTableLoadAsync(source.TableName, "Could not return to the source table", async request =>
        {
            await OpenTableAsync(source.TableName, source.SearchText, request.Token, source.Filter, source.RowDisplay,
                source.Offset, source.Sorts, source.FilterDefinition, request,
                new TableOpenPresentation(_ => (source.Selection, source.Viewport), source.ViewState));
            request.Lease.ThrowIfSuperseded(request.Token);
            FinishReferenceReturn(source);
        });
    }

    private bool TryReturnToLoadedSource(ReferenceSourceBookmark source)
    {
        if (!_tableTabs.TryGetValue(source.TableName, out TabViewItem? tab)
            || tab.Tag is not TableTabContext { IsInvalidated: false } context
            || context.ForeignKeyDisplayMode != _preferences.ForeignKeyDisplayMode
            || !context.SearchText.Equals(source.SearchText, StringComparison.Ordinal)
            || !ReferenceEquals(context.Filter, source.Filter)
            || !context.Sorts.SequenceEqual(source.Sorts)
            || context.Display.RowDisplay != source.RowDisplay || context.Display.Offset != source.Offset)
        {
            return false;
        }

        CaptureCurrentTabState(TableTabs.SelectedItem as TabViewItem);
        tab.Tag = context with { ViewState = source.ViewState, Selection = source.Selection, Viewport = source.Viewport };
        bool wasBound = _gridBindingSession.IsBoundTo(tab);
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
        if (wasBound)
        {
            TableGrid.RestoreSelection(source.Selection);
            TableGrid.RestoreViewport(source.Viewport);
        }

        return true;
    }

    private void FinishReferenceReturn(ReferenceSourceBookmark source)
    {
        _relationshipReturn = null;
        UpdateRelationshipNavigationCommands();
        SynchronizeTableSelectionToTab(TableTabs.SelectedItem as TabViewItem);
        if (string.Equals(OperationInfo.Title, ReferenceOpenedTitle, StringComparison.Ordinal))
        {
            OperationInfo.IsOpen = false;
        }

        ViewModel.Status = $"Returned to {source.TableName}.";
        TableGrid.Focus(FocusState.Programmatic);
    }

    private async Task RunReferenceTableLoadAsync(string tableName, string errorTitle, Func<TableLoadRequest, Task> load)
    {
        TableLoadRequest request = BeginQuery(tableName, showLoadingSurface: true);
        _relationshipNavigationLoad = request;
        try
        {
            await load(request);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested || !request.Lease.IsCurrent)
        {
            if (TablesView.Visibility == Visibility.Visible)
            {
                RestorePreviousTable(request, "Navigation cancelled.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (request.Lease.IsCurrent && TablesView.Visibility == Visibility.Visible)
            {
                RestorePreviousTable(request, "The previous table remains available.");
                PresentError(errorTitle, SafeFailureMessage(exception, "The working database was not changed."));
            }
        }
        finally
        {
            if (ReferenceEquals(_relationshipNavigationLoad, request))
            {
                _relationshipNavigationLoad = null;
            }

            EndQuery(request);
        }
    }

    private sealed record ReferenceChoice(string ColumnName, string TargetTable, SqliteValue Key)
    {
        public string Display => $"{ColumnName} → {TargetTable} · {FormatSqliteValue(Key)}";
    }

    private sealed record ReferenceSourceBookmark(
        Guid SessionId, string TableName, string SearchText, FilterExpression? Filter,
        TableFilterDefinition FilterDefinition, IReadOnlyList<SortDescriptor> Sorts, TableRowDisplayChoice RowDisplay,
        long Offset, TableViewState? ViewState, GridSelection Selection, GridViewport Viewport);

    private sealed record TableOpenPresentation(
        Func<TableDisplayWindow, (GridSelection Selection, GridViewport Viewport)> Resolve,
        TableViewState? ViewState = null);
}
