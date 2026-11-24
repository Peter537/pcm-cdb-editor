using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.App.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private bool _relationshipCellEditorOpen;

    private IEnumerable<string> GetRelationshipPickerColumns(TableSchema table)
    {
        if (_catalog is not { } catalog || table.EditCapability != TableEditCapability.Editable)
        {
            return [];
        }

        return table.Columns
            .Where(column => CanChooseRelationship(catalog, table, column))
            .Select(static column => column.Name);
    }

    private static bool CanChooseRelationship(DatabaseSchemaCatalog catalog, TableSchema table, ColumnSchema column) =>
        !column.IsPrimaryKey && !column.IsGenerated &&
        RelationshipMetadata.TryGetLookupRelation(catalog, table, column.Name, out _, out _);

    private async void TableGrid_RelationshipEditRequested(object? sender, RelationshipCellEditRequest request)
    {
        if (_relationshipCellEditorOpen || _operationLease is not null || _activeTableLoad is not null ||
            _session is not { } session || _catalog is not { } catalog ||
            TableTabs.SelectedItem is not TabViewItem { Tag: TableTabContext context } ||
            !SqliteIdentifierComparer.Instance.Equals(context.Schema.Name, request.TableName) ||
            context.Schema.Columns.FirstOrDefault(column =>
                SqliteIdentifierComparer.Instance.Equals(column.Name, request.ColumnName)) is not { } column ||
            !CanChooseRelationship(catalog, context.Schema, column) ||
            !context.Display.Rows.Any(row => request.Identity.Equals(row.Identity) && row.Revision == request.Revision))
        {
            return;
        }

        _relationshipCellEditorOpen = true;
        try
        {
            using var picker = CreateRelationshipPicker(session.WorkingSqlitePath, catalog, context.Schema, column);
            var current = new TextBlock
            {
                Text = $"Stored key: {DescribeRelationshipKey(request.OldValue)}",
                TextWrapping = TextWrapping.Wrap,
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(current);
            panel.Children.Add(picker.Content);
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = $"Choose a value for {column.Name}",
                Content = panel,
                PrimaryButtonText = "Apply selected key",
                SecondaryButtonText = "Enter raw key",
                CloseButtonText = "Cancel",
                IsPrimaryButtonEnabled = false,
            };
            bool applying = false;
            picker.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = !applying && picker.SelectedOption is not null;
            dialog.Opened += (_, _) => picker.Start();
            dialog.Closing += (_, args) => args.Cancel = applying;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (picker.SelectedOption is not { } option || applying)
                {
                    return;
                }

                var deferral = args.GetDeferral();
                applying = true;
                picker.SetInputEnabled(false);
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                try
                {
                    RowEditResult result = await ApplyRelationshipCellAsync(session, catalog, request, option.Key, option);
                    args.Cancel = result.Retry;
                    if (result.Retry)
                    {
                        picker.SetInputEnabled(true);
                        picker.ShowError(result.Error!);
                    }
                }
                finally
                {
                    applying = false;
                    picker.SetInputEnabled(true);
                    dialog.IsPrimaryButtonEnabled = picker.SelectedOption is not null;
                    dialog.IsSecondaryButtonEnabled = true;
                    deferral.Complete();
                }
            };
            ContentDialogResult outcome = await dialog.ShowAsync();
            if (outcome == ContentDialogResult.Secondary)
            {
                picker.Dispose();
                var values = new Dictionary<string, SqliteValue>(SqliteIdentifierComparer.Instance)
                {
                    [column.Name] = request.OldValue,
                };
                var editors = CreateRowEditors([column], values, allowDefault: false);
                await ShowRowEditorAsync($"Enter raw key for {column.Name}", editors, "Update", changedOnly: true,
                    changes => changes.TryGetValue(column.Name, out SqliteValue value)
                        ? ApplyRelationshipCellAsync(session, catalog, request, value, choice: null)
                        : Task.FromResult(new RowEditResult(false)));
            }
        }
        finally
        {
            _relationshipCellEditorOpen = false;
        }
    }

    private Task<RowEditResult> ApplyRelationshipCellAsync(
        EditorSessionState session,
        DatabaseSchemaCatalog catalog,
        RelationshipCellEditRequest request,
        SqliteValue value,
        RelationshipLookupOption? choice)
    {
        if (_session?.SessionId != session.SessionId || !ReferenceEquals(_catalog, catalog) ||
            TableTabs.SelectedItem is not TabViewItem { Tag: TableTabContext context } ||
            !SqliteIdentifierComparer.Instance.Equals(context.Schema.Name, request.TableName))
        {
            return Task.FromResult(new RowEditResult(true, "The active table changed. Reopen this cell to edit it."));
        }

        if (choice is null && value.Equals(request.OldValue))
        {
            return Task.FromResult(new RowEditResult(false));
        }

        var operation = new RowUpdateOperation(Guid.NewGuid(), request.TableName, DateTimeOffset.UtcNow,
            request.Identity, [KeyValuePair.Create(request.ColumnName, request.OldValue)],
            [KeyValuePair.Create(request.ColumnName, value)], request.Revision,
            choice is null ? null : [KeyValuePair.Create(request.ColumnName, choice)]);
        return ExecuteEditAsync(operation, cancellationToken =>
            _tableDataStore.UpdateRowAsync(session.WorkingSqlitePath, catalog, operation, cancellationToken));
    }

    private Button? CreateRelationshipChoiceButton(
        string workingPath,
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        RowValueEditor editor,
        Dictionary<string, RelationshipLookupOption> choices)
    {
        if (!CanChooseRelationship(catalog, table, editor.Column))
        {
            return null;
        }

        string columnName = editor.Column.Name;
        var button = new Button { Content = "Choose referenced value" };
        AutomationProperties.SetName(button, $"Choose referenced value for {columnName}");
        var flyout = new Flyout();
        RelationshipPickerView? picker = null;
        string? chosenDisplayText = null;
        flyout.Opening += (_, _) =>
        {
            RelationshipPickerView activePicker = CreateRelationshipPicker(workingPath, catalog, table, editor.Column);
            picker = activePicker;
            var panel = new StackPanel { Spacing = 8, Width = 400 };
            panel.Children.Add(activePicker.Content);
            var use = new Button { Content = "Use selected key", IsEnabled = false };
            activePicker.SelectionChanged += (_, _) => use.IsEnabled = activePicker.SelectedOption is not null;
            use.Click += (_, _) =>
            {
                if (activePicker.SelectedOption is { } option)
                {
                    editor.NullBox.IsChecked = false;
                    editor.StorageClassPicker.SelectedItem = option.Key.Kind;
                    editor.TextBox.Text = FormatEditorValue(option.Key);
                    chosenDisplayText = editor.TextBox.Text;
                    choices[columnName] = option;
                    flyout.Hide();
                }
            };
            panel.Children.Add(use);
            flyout.Content = panel;
        };
        flyout.Opened += (_, _) => picker?.Start();
        flyout.Closed += (_, _) =>
        {
            picker?.Dispose();
            picker = null;
            flyout.Content = null;
        };
        button.Flyout = flyout;

        editor.TextBox.TextChanged += (_, _) =>
        {
            if (!string.Equals(editor.TextBox.Text, chosenDisplayText, StringComparison.Ordinal))
            {
                choices.Remove(columnName);
            }
        };
        editor.StorageClassPicker.SelectionChanged += (_, _) =>
        {
            if (choices.TryGetValue(columnName, out RelationshipLookupOption? choice) &&
                !Equals(editor.StorageClassPicker.SelectedItem, choice.Key.Kind))
            {
                choices.Remove(columnName);
            }
        };
        editor.NullBox.Checked += (_, _) => choices.Remove(columnName);
        return button;
    }

    private RelationshipPickerView CreateRelationshipPicker(
        string workingPath,
        DatabaseSchemaCatalog catalog,
        TableSchema table,
        ColumnSchema column) => new(column.Name, (search, cancellationToken) =>
            TrackSessionReaderAsync(_relationshipLookup.SearchAsync(workingPath, catalog, table.Name, column.Name, search,
                limit: 60, cancellationToken)), _lifetimeCancellation.Token);

    private static string DescribeRelationshipKey(SqliteValue value) =>
        value.Kind == SqliteValueKind.Text
            ? $"Text \"{FormatEditorValue(value)}\""
            : value.Kind == SqliteValueKind.Null ? "NULL" : $"{value.Kind} {FormatSqliteValue(value)}";

    private sealed class RelationshipPickerView : IDisposable
    {
        private readonly TextBox _search = new() { PlaceholderText = "Search names, keys, or context" };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly ListView _options = new() { MaxHeight = 270, SelectionMode = ListViewSelectionMode.Single };
        private readonly Func<string, CancellationToken, Task<RelationshipLookupPage>> _lookup;
        private readonly CancellationToken _lifetime;
        private CancellationTokenSource? _searchCancellation;
        private bool _disposed;

        public RelationshipPickerView(
            string sourceColumn,
            Func<string, CancellationToken, Task<RelationshipLookupPage>> lookup,
            CancellationToken lifetime)
        {
            _lookup = lookup;
            _lifetime = lifetime;
            Content = new StackPanel { Spacing = 8 };
            Content.Children.Add(_search);
            Content.Children.Add(_error);
            Content.Children.Add(_status);
            Content.Children.Add(_options);
            AutomationProperties.SetName(_search, $"Search referenced values for {sourceColumn}");
            AutomationProperties.SetName(_options, $"Referenced values for {sourceColumn}");
            AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            _search.TextChanged += async (_, _) =>
            {
                _error.Visibility = Visibility.Collapsed;
                await ReloadAsync(delay: true);
            };
            _options.SelectionChanged += (_, _) =>
            {
                if (SelectedOption is not null)
                {
                    _error.Visibility = Visibility.Collapsed;
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
        }

        public StackPanel Content { get; }
        public RelationshipLookupOption? SelectedOption => (_options.SelectedItem as ListViewItem)?.Tag as RelationshipLookupOption;
        public event EventHandler? SelectionChanged;

        public async void Start()
        {
            _search.Focus(FocusState.Programmatic);
            await ReloadAsync(delay: false);
        }

        public void ShowError(string message)
        {
            _error.Text = message;
            _error.Visibility = Visibility.Visible;
            _search.Focus(FocusState.Programmatic);
            _ = ReloadAsync(delay: false);
        }

        public void SetInputEnabled(bool enabled)
        {
            _search.IsEnabled = enabled;
            _options.IsEnabled = enabled;
        }

        private async Task ReloadAsync(bool delay)
        {
            if (_disposed || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _searchCancellation?.Cancel();
            using var current = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _searchCancellation = current;
            _options.Items.Clear();
            _status.Text = "Loading referenced values…";
            string query = _search.Text;
            try
            {
                if (delay)
                {
                    await Task.Delay(SearchDelayMilliseconds, current.Token);
                }
                RelationshipLookupPage result = await _lookup(query, current.Token);
                if (current.IsCancellationRequested || _disposed)
                {
                    return;
                }

                foreach (RelationshipLookupOption option in result.Options)
                {
                    string key = DescribeRelationshipKey(option.Key);
                    string label = option.Label is null ? key : $"{option.Label} · {key}";
                    var item = new ListViewItem
                    {
                        Tag = option,
                        Content = new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(option.Context) ? label : $"{label}\n{option.Context}",
                            TextWrapping = TextWrapping.Wrap,
                        },
                    };
                    AutomationProperties.SetName(item, label);
                    AutomationProperties.SetHelpText(item, option.Context);
                    _options.Items.Add(item);
                }
                _status.Text = result.UnavailableReason ?? (result.Options.Count == 0
                    ? "No matching referenced values."
                    : result.HasMore ? $"Showing the first {result.Options.Count} matches. Refine the search for more."
                    : $"{result.Options.Count} matching referenced values.");
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (IsExpectedOperationFailure(exception))
            {
                if (!current.IsCancellationRequested && !_disposed)
                {
                    _status.Text = SafeFailureMessage(exception, "Could not load referenced values. Try another search or enter the raw key.");
                }
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, current))
                {
                    _searchCancellation = null;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _searchCancellation?.Cancel();
        }
    }
}
