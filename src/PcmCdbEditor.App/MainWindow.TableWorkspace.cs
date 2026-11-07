using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.App.ViewModels;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private bool _rowDetailsOpen;
    private bool _tableQueryBanner;

    private bool TryGetPresentedRow(out TableTabContext context, out TypedRow row)
    {
        if (TableTabs.SelectedItem is TabViewItem tab &&
            _gridBindingSession.IsBoundTo(tab) &&
            tab.Tag is TableTabContext bound &&
            TableGrid.GetPresentedRow(out int index) is { } presentation &&
            (uint)index < (uint)bound.Display.Rows.Count &&
            ReferenceEquals(bound.Display.Rows[index], presentation.Row))
        {
            context = bound;
            row = presentation.Row;
            return true;
        }

        context = null!;
        row = null!;
        return false;
    }

    private void UpdatePresentedRow()
    {
        bool hasRow = TryGetPresentedRow(out TableTabContext context, out TypedRow row);
        if (!ReferenceEquals(_selectedRow, row))
        {
            InspectorValues.ItemsSource = hasRow ? GetInspectorValues(context, row) : null;
        }
        _selectedRow = hasRow ? row : null;
        bool editable = hasRow && row.Identity is not null &&
            context.Schema.EditCapability == TableEditCapability.Editable;
        InspectorHeading.Text = !hasRow ? "No current row"
            : editable ? DescribeRow(context, row)
            : $"{DescribeRow(context, row)} · Read-only";
        InspectorSelection.Text = hasRow ? DescribeCheckedRows() : string.Empty;
        SetSelectedRowActionsEnabled(editable && _operationLease is null && _activeTableLoad is null);
        RowDetailsButton.IsEnabled = hasRow && CanOpenRowDetails();
        UpdateRelationshipNavigationCommands();
    }

    private static InspectorValueItem[] GetInspectorValues(TableTabContext context, TypedRow row) =>
        context.Schema.Columns
            .Where(static column => !column.IsHidden)
            .Select(column =>
            {
                if (!row.Values.TryGetValue(column.Name, out SqliteValue value))
                {
                    return new InspectorValueItem(column.Name, "Unavailable");
                }

                string display = value.Kind == SqliteValueKind.Null
                    ? "NULL"
                    : $"{value.Kind} · {DescribeStoredValue(value)}";
                if (row.RelatedValues.GetValueOrDefault(column.Name)?.Label is { Length: > 0 } label)
                {
                    display += $"\nRelated name · {label}";
                }

                return new InspectorValueItem(column.Name, display);
            })
            .ToArray();

    private static string DescribeStoredValue(SqliteValue value) => value.Kind == SqliteValueKind.Text
        ? $"\"{value.TextValue}\""
        : FormatSqliteValue(value);

    private static string DescribeRow(TableTabContext context, TypedRow row) =>
        row.Identity is { } identity
            ? $"{context.Schema.Name} · " + string.Join(", ", identity.Components.Select(component =>
                $"{component.ColumnName} = {DescribeStoredValue(component.Value)}"))
            : $"{context.Schema.Name} · No stable row key";

    private string DescribeCheckedRows() => $"Rows checked: {TableGrid.CheckedRowCount:N0}";

    private string DescribeRowAction(TableTabContext context, TypedRow row) =>
        $"{DescribeRow(context, row)}\n{DescribeCheckedRows()}. This action affects one current row.";

    private bool CanOpenRowDetails() => TablesView.Visibility == Visibility.Visible &&
        _operationLease is null && _activeTableLoad is null &&
        !_rowEditorOpen && !_relationshipCellEditorOpen && !_rowDetailsOpen && !TableGrid.IsEditing;

    private async void RowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenRowDetails() || !TryGetPresentedRow(out TableTabContext context, out TypedRow row))
        {
            return;
        }

        var values = new ListView
        {
            ItemsSource = GetInspectorValues(context, row),
            ItemTemplate = InspectorValues.ItemTemplate,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 440,
        };
        AutomationProperties.SetName(values, "Current row values and storage classes");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = DescribeRow(context, row) + "\n" + DescribeCheckedRows(),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(values);
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Row details",
            Content = content,
            CloseButtonText = "Close",
        };
        _rowDetailsOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _rowDetailsOpen = false;
            TableGrid.FocusCurrentRow();
        }
    }

    private async void HistoryShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        bool isUndo = sender.Key == Windows.System.VirtualKey.Z;
        if (!(isUndo ? UndoButton : RedoButton).IsEnabled || _rowEditorOpen || _rowDetailsOpen ||
            _relationshipCellEditorOpen || TableGrid.IsEditing ||
            FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is TextBox or RichEditBox)
        {
            return;
        }

        args.Handled = true;
        await ReplayEditAsync(isUndo);
    }

    private void UpdateQuerySummary(TableTabContext context, string sortSummary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.SearchText))
        {
            parts.Add($"Search \"{context.SearchText}\"");
        }

        string filterSummary = context.Filter is null ? "No active filters." : DescribeFilter(context.Filter);
        if (context.Filter is not null)
        {
            parts.Add("Filter · " + filterSummary);
        }
        if (context.Sorts.Count > 0)
        {
            parts.Add("Sort · " + sortSummary);
        }
        if (context.Display.RowDisplay.IsAllRows)
        {
            parts.Add("All rows loaded");
        }

        ActiveTableQueryText.Text = parts.Count == 0
            ? "No active search, filters, or sort"
            : string.Join("  |  ", parts);
        ToolTipService.SetToolTip(ActiveTableQueryText, ActiveTableQueryText.Text);
        AutomationProperties.SetName(FiltersButton, (string)FiltersButton.Content);
        AutomationProperties.SetName(SortButton, (string)SortButton.Content);
        AutomationProperties.SetHelpText(FiltersButton, filterSummary);
        ToolTipService.SetToolTip(FiltersButton, filterSummary);
        ResetTableQueryButton.IsEnabled = _operationLease is null && _activeTableLoad is null &&
            (!string.IsNullOrWhiteSpace(context.SearchText) || context.Filter is not null ||
             context.Sorts.Count > 0 || context.Display.Offset > 0);
    }

    private static string DescribeFilter(FilterExpression filter)
    {
        if (filter is FilterGroup group)
        {
            string join = group.Operator == FilterGroupOperator.And ? " AND " : " OR ";
            return "(" + string.Join(join, group.Children.Select(DescribeFilter)) + ")";
        }

        var condition = (FilterCondition)filter;
        string column = condition.Target == QueryValueTarget.RelatedLabel
            ? $"{condition.ColumnName} name"
            : condition.ColumnName;
        string value = DescribeStoredValue(condition.Value);
        string comparison = condition.Operator switch
        {
            FilterOperator.Contains => $"contains {value}",
            FilterOperator.StartsWith => $"starts with {value}",
            FilterOperator.EndsWith => $"ends with {value}",
            FilterOperator.Equals => $"= {value}",
            FilterOperator.NotEquals => $"≠ {value}",
            FilterOperator.GreaterThan => $"> {value}",
            FilterOperator.GreaterThanOrEqual => $"≥ {value}",
            FilterOperator.LessThan => $"< {value}",
            FilterOperator.LessThanOrEqual => $"≤ {value}",
            FilterOperator.IsNull => "is NULL",
            FilterOperator.IsNotNull => "is not NULL",
            _ => throw new InvalidOperationException("Unknown filter operator."),
        };
        return $"{column} {comparison}";
    }

    private static int CountAppliedFilters(FilterExpression? filter) => filter switch
    {
        FilterCondition => 1,
        FilterGroup group => group.Children.Sum(CountAppliedFilters),
        _ => 0,
    };

    private void ResetTableWorkspaceChrome()
    {
        _suppressCurrentTableSearch = true;
        CurrentTableSearchBox.Text = string.Empty;
        _suppressCurrentTableSearch = false;
        _suppressPageSizeSelection = true;
        PageSizeBox.SelectedIndex = -1;
        _suppressPageSizeSelection = false;
        FiltersButton.Content = "Filters";
        SortButton.Content = "Sort";
        foreach (Button button in new[] { FiltersButton, SortButton })
        {
            AutomationProperties.SetName(button, (string)button.Content);
            AutomationProperties.SetHelpText(button, "Choose a table to configure its query.");
            ToolTipService.SetToolTip(button, null);
        }
        ActiveTableQueryText.Text = "No table selected";
        ToolTipService.SetToolTip(ActiveTableQueryText, null);
        ViewModel.PageSizeLabel = string.Empty;
        ClearTableQueryBanner();
        SetTableQueryControlsEnabled(_operationLease is null);
        SetInsertRowActionsEnabled(false);
        UpdatePresentedRow();
    }

    private void PresentTableRows(TableTabContext context)
    {
        bool hasRows = context.Display.Rows.Count > 0;
        SetTableGridPresented(hasRows);
        if (hasRows)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        if (context.Filter is not null || !string.IsNullOrWhiteSpace(context.SearchText))
        {
            ShowEmptyState("No matching rows", "Change the search or filters, or use Reset query.", allowOpen: false);
        }
        else
        {
            ShowEmptyState("This table is empty", "There are no rows in the current table.", allowOpen: false);
        }
    }

    private bool TryGetIdleTable(out TableTabContext context)
    {
        if (_operationLease is null && _activeTableLoad is null && !TableGrid.IsEditing &&
            TableTabs.SelectedItem is TabViewItem { Tag: TableTabContext selected })
        {
            context = selected;
            return true;
        }

        context = null!;
        return false;
    }

    private async void RefreshTable_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetIdleTable(out TableTabContext context))
        {
            await LoadTableQueryAsync(context, context.SearchText, context.Filter,
                GetRequestedRowDisplay(context), GetRequestedOffset(context), refresh: true);
        }
    }

    private async void ResetTableQuery_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetIdleTable(out TableTabContext context))
        {
            await LoadTableQueryAsync(context, string.Empty, null, GetRequestedRowDisplay(context),
                sorts: [], filterDefinition: TableFilterDefinition.Empty);
        }
    }

    private async void ResetColumnLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetIdleTable(out TableTabContext context) ||
            TableTabs.SelectedItem is not TabViewItem tab || _catalog is null ||
            !TryBeginOperation("Reset column layout", out CancellationToken cancellationToken))
        {
            return;
        }

        try
        {
            CaptureCurrentTabState(tab);
            context = (TableTabContext)tab.Tag;
            var layout = new TableViewState(_catalog.SchemaSignature, context.Schema.Name, [], _preferences.Density, 0);
            await _settingsStore.SaveTableViewStateAsync(layout, cancellationToken);
            GridViewport viewport = context.Viewport with { HorizontalOffset = 0 };
            tab.Tag = context with { ViewState = layout, Viewport = viewport };
            if (_relationshipReturn is { } source &&
                source.TableName.Equals(context.Schema.Name, StringComparison.OrdinalIgnoreCase))
            {
                _relationshipReturn = source with
                {
                    ViewState = layout,
                    Viewport = source.Viewport with { HorizontalOffset = 0 },
                };
            }
            TableGrid.InvalidateColumnLayout();
            EnsureTabBound(tab);
            TableGrid.RestoreSelection(context.Selection);
            TableGrid.RestoreViewport(viewport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentWarning("Column layout was not reset",
                SafeFailureMessage(exception, "The current layout remains available."));
        }
        finally
        {
            EndOperation();
        }
    }

    private void PresentTableQueryError(string title, string message)
    {
        PresentError(title, message);
        _tableQueryBanner = true;
    }

    private void PresentTableQueryWarning(string title, string message)
    {
        PresentWarning(title, message);
        _tableQueryBanner = true;
    }

    private void ClearTableQueryBanner()
    {
        if (_tableQueryBanner)
        {
            OperationInfo.IsOpen = false;
            _tableQueryBanner = false;
        }
    }

    private async Task<EditorPreferences> SavePreferencesAsync(
        Func<EditorPreferences, EditorPreferences> update,
        CancellationToken cancellationToken)
    {
        await _preferenceSaveGate.WaitAsync(cancellationToken);
        try
        {
            EditorPreferences previous = _preferences;
            EditorPreferences updated = update(previous);
            await _settingsStore.SavePreferencesAsync(updated, cancellationToken);
            _preferences = updated;
            return previous;
        }
        finally
        {
            _preferenceSaveGate.Release();
        }
    }
}
