using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace PcmCdbEditor.App.Controls;

/// <summary>
/// Keeps every WinUI.TableView type inside the application adapter boundary.
/// </summary>
public sealed partial class TableGridAdapterControl : UserControl, ITableGridAdapter
{
    private readonly BulkRowSource<GridRowPresentation> _rowSource = new();
    private readonly InlineEditCommitStager _editStager = new();
    private HashSet<string> _relationshipPickerColumns = new(StringComparer.OrdinalIgnoreCase);
    private RelationshipCellEditRequest? _pendingRelationshipEdit;
    private ColumnFingerprintHeader? _columnFingerprintHeader;
    private GridColumnFingerprint[] _columnFingerprint = [];
    private long _bindGeneration;
    private long _pendingSortIndicatorBindGeneration;
    private long _sortIndicatorGeneration;
    private long _pendingSortIndicatorGeneration;
    private string? _pendingSortIndicatorColumnName;
    private SortDirection? _pendingSortIndicatorDirection;
    private bool _hasPendingSortIndicator;
    private bool _isBinding;
    private bool _isEditingAllowed = true;
    private bool _presentationPending;

    public TableGridAdapterControl()
    {
        InitializeComponent();
        GridControl.LayoutUpdated += GridControl_LayoutUpdated;
        GridControl.PreviewKeyDown += GridControl_PreviewKeyDown;
        GridControl.CurrentCellChanged += (_, _) => QueuePresentedRowChanged();
    }

    public IReadOnlyList<GridRowPresentation> Rows => _rowSource.Items;

    public bool IsEditing => GetCurrentCell()?.Content is TextBox;

    public event EventHandler<GridSelection>? SelectionChanged;
    public event EventHandler? PresentedRowChanged;

    public int CheckedRowCount => GridControl.SelectedItems.Count;

    public GridRowPresentation? GetPresentedRow(out int index)
    {
        if (TryGetCurrentCellAddress(out var slot, out GridRowPresentation row, out _))
        {
            index = slot.Row;
            return row;
        }

        index = GridControl.SelectedIndex;
        return !_isBinding && (uint)index < (uint)_rowSource.Items.Length &&
            ReferenceEquals(GridControl.SelectedItem, _rowSource.Items[index])
            ? _rowSource.Items[index]
            : null;
    }

    public void FocusCurrentRow()
    {
        if (GetCurrentCell()?.Focus(FocusState.Programmatic) != true)
        {
            GridControl.Focus(FocusState.Programmatic);
        }
    }

    public void InvalidateColumnLayout() => _columnFingerprintHeader = null;

    private void QueuePresentedRowChanged()
    {
        if (_presentationPending)
        {
            return;
        }

        _presentationPending = DispatcherQueue.TryEnqueue(() =>
        {
            _presentationPending = false;
            PresentedRowChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public event EventHandler<string>? ColumnHeaderActivated;

    public event EventHandler<EditOperation>? EditCommitted;
    public event EventHandler<RelationshipCellEditRequest>? RelationshipEditRequested;

    /// <summary>
    /// Reports an edit that the adapter cannot safely represent without exposing
    /// package-specific event arguments outside this control.
    /// </summary>
    public event EventHandler<string>? EditRejected;

    public void SetEditingAllowed(bool isAllowed)
    {
        _isEditingAllowed = isAllowed;
        if (!isAllowed)
        {
            _pendingRelationshipEdit = null;
        }
    }

    public void SetRelationshipPickerColumns(IEnumerable<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(columnNames);
        _relationshipPickerColumns = columnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pendingRelationshipEdit = null;
    }

    public void Bind(
        TableSchema schema,
        IReadOnlyList<TypedRow> rows,
        TableViewState? state,
        ForeignKeyDisplayMode displayMode)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        _editStager.Clear();
        _pendingRelationshipEdit = null;

        ColumnSchema[] visibleColumns = schema.Columns
            .Where(static column => !column.IsHidden)
            .OrderBy(static column => column.Ordinal)
            .ToArray();
        Dictionary<string, ColumnDisplayState> savedColumns = CreateSavedColumnMap(state);
        var nextHeader = new ColumnFingerprintHeader(
            schema.Name,
            schema.EditCapability,
            Math.Clamp(state?.FrozenColumnCount ?? 0, 0, visibleColumns.Length));
        GridColumnFingerprint[] nextFingerprint = CreateColumnFingerprint(
            visibleColumns,
            savedColumns);
        bool columnsChanged = _columnFingerprintHeader != nextHeader ||
            !_columnFingerprint.SequenceEqual(nextFingerprint);
        GridSelection selection = columnsChanged ? new GridSelection(null, null, []) : CaptureSelection();
        GridViewport viewport = columnsChanged ? new GridViewport(null, 0) : CaptureViewport();
        if (columnsChanged)
        {
            NeutralizeHorizontalOffsetForColumnTopologyChange();
        }

        _rowSource.Replace(
            rows,
            row => new GridRowPresentation(schema.Name, row, visibleColumns, displayMode));
        _bindGeneration++;

        _isBinding = true;
        try
        {
            if (columnsChanged)
            {
                // TableView must not realize old rows against a changing column collection.
                GridControl.ItemsSource = null;
                RebuildColumns(schema, visibleColumns, state, savedColumns);
                _columnFingerprintHeader = nextHeader;
                _columnFingerprint = nextFingerprint;
            }

            // One complete replacement avoids a notification and layout pass per row.
            GridControl.ItemsSource = _rowSource.Items;
            if (!columnsChanged)
            {
                RestoreSelection(selection);
                RestoreViewport(viewport);
            }
        }
        finally
        {
            _isBinding = false;
        }

        AnnounceSelection(CaptureSelection());
    }

    public void Clear()
    {
        _editStager.Clear();
        _pendingRelationshipEdit = null;
        _relationshipPickerColumns.Clear();
        _isBinding = true;
        try
        {
            NeutralizeHorizontalOffsetForColumnTopologyChange();
            GridControl.ItemsSource = null;
            GridControl.Columns.Clear();
            _rowSource.Clear();
            _columnFingerprintHeader = null;
            _columnFingerprint = [];
            _bindGeneration++;
        }
        finally
        {
            _isBinding = false;
        }

        AnnounceSelection(new GridSelection(null, null, []));
    }

    private void NeutralizeHorizontalOffsetForColumnTopologyChange()
    {
        if (GridControl.HorizontalOffset == 0)
        {
            return;
        }

        GridControl.SetValue(
            WinUI.TableView.TableView.HorizontalOffsetProperty,
            0d);
    }

    private void RebuildColumns(
        TableSchema schema,
        ColumnSchema[] visibleColumns,
        TableViewState? state,
        Dictionary<string, ColumnDisplayState> savedColumns)
    {
        GridControl.Columns.Clear();
        GridControl.FrozenColumnCount = Math.Clamp(
            state?.FrozenColumnCount ?? 0,
            0,
            visibleColumns.Length);
        for (var index = 0; index < visibleColumns.Length; index++)
        {
            ColumnSchema column = visibleColumns[index];
            savedColumns.TryGetValue(column.Name, out ColumnDisplayState? saved);
            bool isReadOnly = schema.EditCapability != TableEditCapability.Editable ||
                column.IsGenerated ||
                column.IsPrimaryKey;
            var tableColumn = new DirectTextTableViewColumn(index, isReadOnly)
            {
                Header = column.Name,
                IsReadOnly = isReadOnly,
                CanFilter = false,
                CanSort = true,
                CanReorder = true,
                CanResize = true,
                Visibility = saved is null || saved.IsVisible ? Visibility.Visible : Visibility.Collapsed,
                Width = new GridLength(saved?.Width is > 24 ? saved.Width : 160),
                Tag = column.Name,
                Order = saved?.DisplayIndex ?? index,
            };
            GridControl.Columns.Add(tableColumn);
        }
    }

    private static GridColumnFingerprint[] CreateColumnFingerprint(
        ColumnSchema[] visibleColumns,
        Dictionary<string, ColumnDisplayState> savedColumns)
    {
        return visibleColumns.Select(column =>
        {
            savedColumns.TryGetValue(column.Name, out ColumnDisplayState? saved);
            double width = saved?.Width is > 24 ? saved.Width : 160;
            return new GridColumnFingerprint(
                column.Name,
                column.Ordinal,
                column.IsGenerated,
                column.IsPrimaryKey,
                saved?.IsVisible != false,
                saved?.DisplayIndex ?? column.Ordinal,
                BitConverter.DoubleToInt64Bits(width));
        }).ToArray();
    }

    private static Dictionary<string, ColumnDisplayState> CreateSavedColumnMap(
        TableViewState? state)
    {
        var savedColumns = new Dictionary<string, ColumnDisplayState>(StringComparer.OrdinalIgnoreCase);
        if (state is null)
        {
            return savedColumns;
        }

        foreach (ColumnDisplayState column in state.Columns)
        {
            savedColumns.TryAdd(column.ColumnName, column);
        }

        return savedColumns;
    }

    public void SetDensity(GridDensity density)
    {
        GridControl.RowHeight = density == GridDensity.Compact ? 30 : 40;
        GridControl.HeaderRowHeight = density == GridDensity.Compact ? 34 : 44;
    }

    public void SetHeaderSortingEnabled(bool isEnabled) =>
        GridControl.CanSortColumns = isEnabled;

    public void SetPrimarySortIndicator(string? columnName, SortDirection? direction)
    {
        _pendingSortIndicatorGeneration = ++_sortIndicatorGeneration;
        _pendingSortIndicatorBindGeneration = _bindGeneration;
        _pendingSortIndicatorColumnName = columnName;
        _pendingSortIndicatorDirection = direction;
        _hasPendingSortIndicator = true;
        ApplyPrimarySortIndicator(columnName, direction);
    }

    private void GridControl_LayoutUpdated(object? sender, object e)
    {
        if (!_hasPendingSortIndicator)
        {
            return;
        }

        // A bind or tab switch can replace native header presenters during layout.
        // Reapply the latest committed direction once those presenters exist.
        _hasPendingSortIndicator = false;
        if (_pendingSortIndicatorGeneration == _sortIndicatorGeneration &&
            _pendingSortIndicatorBindGeneration == _bindGeneration)
        {
            ApplyPrimarySortIndicator(
                _pendingSortIndicatorColumnName,
                _pendingSortIndicatorDirection);
        }
    }

    private void ApplyPrimarySortIndicator(string? columnName, SortDirection? direction)
    {
        foreach (WinUI.TableView.TableViewColumn column in GridControl.Columns)
        {
            column.SortDirection = null;
        }

        if (string.IsNullOrWhiteSpace(columnName) || direction is null)
        {
            return;
        }

        WinUI.TableView.TableViewColumn? primaryColumn = GridControl.Columns.FirstOrDefault(column =>
            column.Tag is string name &&
            name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (primaryColumn is null)
        {
            return;
        }

        primaryColumn.SortDirection = direction switch
        {
            SortDirection.Ascending => WinUI.TableView.SortDirection.Ascending,
            SortDirection.Descending => WinUI.TableView.SortDirection.Descending,
            _ => null,
        };
    }

    public TableViewState CaptureViewState(
        string schemaSignature,
        string tableName,
        GridDensity density)
    {
        var columns = GridControl.Columns
            .Select((column, index) => new ColumnDisplayState(
                column.Tag as string ?? column.Header?.ToString() ?? $"column-{index}",
                column.Width.IsAbsolute ? column.Width.Value : 160,
                column.Order ?? index,
                column.Visibility == Visibility.Visible,
                IsFrozen: index < GridControl.FrozenColumnCount))
            .ToArray();
        return new TableViewState(
            schemaSignature,
            tableName,
            columns,
            density,
            GridControl.FrozenColumnCount);
    }

    public GridSelection CaptureSelection()
    {
        var selectedRow = GridControl.SelectedItem as GridRowPresentation;
        RowIdentity? identity = selectedRow?.Identity;
        RowIdentity? currentCellRow = null;
        string? currentColumn = null;
        (int, TypedRow)? readOnlyCell = null;
        if (TryGetCurrentCellAddress(out var slot, out GridRowPresentation cellRow, out string cellColumn))
        {
            currentCellRow = cellRow.Identity;
            currentColumn = cellColumn;
            if (currentCellRow is null)
            {
                readOnlyCell = (slot.Row, cellRow.Row);
            }
        }
        GridRowPresentation[] checkedRows = GridControl.SelectedItems
            .OfType<GridRowPresentation>()
            .ToArray();
        RowIdentity[] selected = checkedRows
            .Select(static row => row.Identity)
            .OfType<RowIdentity>()
            .ToArray();
        return new GridSelection(identity, currentColumn, selected)
        {
            CurrentCellRow = currentCellRow,
            ReadOnlySelectedRow = selectedRow is { Identity: null }
                ? (GridControl.SelectedIndex, selectedRow.Row)
                : null,
            ReadOnlyCheckedRows = checkedRows
                .Where(static row => row.Identity is null)
                .Select(static row => row.Row)
                .ToArray(),
            ReadOnlyCell = readOnlyCell,
        };
    }

    public string? GetCurrentColumnForRow(TypedRow row) =>
        TryGetCurrentCellAddress(out _, out GridRowPresentation presentation, out string columnName) &&
        ReferenceEquals(row, presentation.Row)
            ? columnName
            : null;

    private bool TryGetCurrentCellAddress(
        out WinUI.TableView.TableViewCellSlot slot,
        out GridRowPresentation row,
        out string columnName)
    {
        IList<WinUI.TableView.TableViewColumn> visibleColumns = GridControl.Columns.VisibleColumns;
        if (!_isBinding && GridControl.CurrentCellSlot is { } current &&
            (uint)current.Row < (uint)_rowSource.Items.Length &&
            (uint)current.Row < (uint)GridControl.Items.Count &&
            ReferenceEquals(GridControl.Items[current.Row], _rowSource.Items[current.Row]) &&
            (uint)current.Column < (uint)visibleColumns.Count &&
            visibleColumns[current.Column].Tag is string physicalColumn)
        {
            slot = current;
            row = _rowSource.Items[current.Row];
            columnName = physicalColumn;
            return true;
        }

        slot = default;
        row = null!;
        columnName = string.Empty;
        return false;
    }

    private WinUI.TableView.TableViewCell? GetCurrentCell()
    {
        if (!TryGetCurrentCellAddress(out var slot, out GridRowPresentation row, out _) ||
            GridControl.ContainerFromIndex(slot.Row) is not WinUI.TableView.TableViewRow nativeRow ||
            !ReferenceEquals(nativeRow.Content, row) ||
            (uint)slot.Column >= (uint)nativeRow.Cells.Count)
        {
            return null;
        }

        WinUI.TableView.TableViewCell cell = nativeRow.Cells[slot.Column];
        return cell.IsCurrent ? cell : null;
    }

    public GridViewport CaptureViewport()
    {
        if (_rowSource.Items.Length == 0)
        {
            return new GridViewport(null, 0);
        }

        double rowHeight = Math.Max(1, GridControl.RowHeight);
        int firstIndex = Math.Clamp(
            (int)Math.Floor(GridControl.VerticalOffset / rowHeight),
            0,
            _rowSource.Items.Length - 1);
        GridRowPresentation first = _rowSource.Items[firstIndex];
        return new GridViewport(first.Identity,
            checked((int)Math.Round(GridControl.HorizontalOffset, MidpointRounding.ToEven)))
        {
            ReadOnlyFirstRow = first.Identity is null ? (firstIndex, first.Row) : null,
        };
    }

    public void RestoreSelection(GridSelection selection)
    {
        bool wasBinding = _isBinding;
        _isBinding = true;
        try
        {
            ApplySelection(selection);
        }
        finally
        {
            _isBinding = wasBinding;
        }
        if (!wasBinding)
        {
            AnnounceSelection(CaptureSelection());
        }
    }

    private void ApplySelection(GridSelection selection)
    {
        IList<WinUI.TableView.TableViewColumn> visibleColumns = GridControl.Columns.VisibleColumns;
        GridSelectionResolution<GridRowPresentation> resolved =
            GridContentBindingSession.ResolveSelection(
                selection,
                _rowSource.Items,
                static row => row.Identity,
                visibleColumns
                    .Select(static column => column.Tag)
                    .OfType<string>());

        int readOnlySelectedIndex = ResolveReadOnlyRow(selection.ReadOnlySelectedRow);
        GridRowPresentation? primarySelection = readOnlySelectedIndex >= 0
            ? _rowSource.Items[readOnlySelectedIndex]
            : resolved.CurrentRow;
        GridControl.SelectedItems.Clear();
        if (primarySelection is not null)
        {
            GridControl.SelectedItems.Add(primarySelection);
        }
        foreach (GridRowPresentation selected in resolved.SelectedRows)
        {
            if (!ReferenceEquals(selected, primarySelection))
            {
                GridControl.SelectedItems.Add(selected);
            }
        }
        if (selection.ReadOnlyCheckedRows.Count > 0)
        {
            var checkedRows = new HashSet<TypedRow>(selection.ReadOnlyCheckedRows, ReferenceEqualityComparer.Instance);
            foreach (GridRowPresentation row in _rowSource.Items)
            {
                if (!ReferenceEquals(row, primarySelection) && checkedRows.Contains(row.Row))
                {
                    GridControl.SelectedItems.Add(row);
                }
            }
        }

        int rowIndex = selection.ReadOnlyCell is not null
            ? ResolveReadOnlyRow(selection.ReadOnlyCell)
            : Array.IndexOf(_rowSource.Items, resolved.CurrentCellRow);
        if (rowIndex >= 0 && selection.CurrentColumn is not null)
        {
            int columnIndex = visibleColumns
                .Select((column, index) => new { column, index })
                .Where(item => item.column.Tag is string name &&
                    name.Equals(selection.CurrentColumn, StringComparison.OrdinalIgnoreCase))
                .Select(static item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (columnIndex >= 0)
            {
                GridControl.CurrentCellSlot = new WinUI.TableView.TableViewCellSlot(
                    rowIndex,
                    columnIndex);
                return;
            }
        }

        GridControl.CurrentCellSlot = null;
    }

    private int ResolveReadOnlyRow((int Index, TypedRow Row)? snapshot) =>
        snapshot is { } locator && (uint)locator.Index < (uint)_rowSource.Items.Length &&
        ReferenceEquals(_rowSource.Items[locator.Index].Row, locator.Row)
            ? locator.Index
            : -1;

    public void RestoreViewport(GridViewport viewport)
    {
        if (viewport.FirstVisibleRow is null && viewport.ReadOnlyFirstRow is null && viewport.HorizontalOffset == 0)
        {
            return;
        }

        long generation = _bindGeneration;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => RestoreViewport(viewport, generation));
    }

    private async void RestoreViewport(GridViewport viewport, long generation)
    {
        if (generation != _bindGeneration)
        {
            return;
        }

        if (GridControl.SelectedIndex >= 0)
        {
            await GridControl.ScrollRowIntoView(GridControl.SelectedIndex);
        }

        if (generation != _bindGeneration)
        {
            return;
        }

        if (GridControl.CurrentCellSlot is { } currentCellSlot)
        {
            await GridControl.ScrollCellIntoView(currentCellSlot);
        }

        if (generation != _bindGeneration)
        {
            return;
        }

        if (viewport.FirstVisibleRow is not null &&
            _rowSource.Items.FirstOrDefault(row =>
                row.Identity is not null && row.Identity.Equals(viewport.FirstVisibleRow)) is { } first)
        {
            GridControl.ScrollIntoView(first);
        }
        else if (ResolveReadOnlyRow(viewport.ReadOnlyFirstRow) is var readOnlyIndex && readOnlyIndex >= 0)
        {
            GridControl.ScrollIntoView(_rowSource.Items[readOnlyIndex]);
        }

        GridControl.SetValue(
            WinUI.TableView.TableView.HorizontalOffsetProperty,
            (double)viewport.HorizontalOffset);
    }

    private void GridControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBinding)
        {
            return;
        }

        AnnounceSelection(CaptureSelection());
    }

    private void GridControl_Sorting(
        object sender,
        WinUI.TableView.TableViewSortingEventArgs args)
    {
        // Sorting is database-side. Never let TableView reorder only the loaded page.
        args.Handled = true;
        if (args.Column.Tag is string columnName && !string.IsNullOrWhiteSpace(columnName))
        {
            long generation = _bindGeneration;
            // TableView raises Sorting before its header tap has finished. Defer
            // downstream grid state changes until that native callback returns.
            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    if (generation == _bindGeneration)
                    {
                        AnnounceColumnHeaderActivation(columnName);
                    }
                });
        }
    }

    private void GridControl_BeginningEdit(
        object sender,
        WinUI.TableView.TableViewBeginningEditEventArgs args)
    {
        _editStager.Clear();
        if (args.Cancel || !_isEditingAllowed || _isBinding || _pendingRelationshipEdit is not null)
        {
            args.Cancel = true;
            return;
        }

        if (args.DataItem is not GridRowPresentation row ||
            args.Column.Tag is not string columnName ||
            row.Identity is null ||
            !row.TryGetCell(columnName, out GridCellPresentation cell))
        {
            args.Cancel = true;
            RejectEdit("This cell has no verified row identity and cannot be edited safely.");
            return;
        }

        if (cell.Value.Kind == SqliteValueKind.Blob)
        {
            args.Cancel = true;
            RejectEdit("BLOB values are read-only metadata and cannot be edited as text.");
            return;
        }

        if (CanRequestRelationshipEdit(args.Cell, row, cell))
        {
            args.Cancel = true;
            // TableView also raises BeginningEdit for the next cell after an Enter or Tab commit.
            if (args.EditingArgs is DoubleTappedRoutedEventArgs or
                KeyRoutedEventArgs { Key: VirtualKey.F2, KeyStatus.WasKeyDown: false })
            {
                QueueRelationshipEdit(row, cell);
            }
            return;
        }

        if (cell.IsDisplayProjection)
        {
            args.Cancel = true;
            RejectEdit("Use Edit row to change this foreign-key value in its raw SQLite form.");
            return;
        }

        if (cell.Value.Kind == SqliteValueKind.Null)
        {
            args.Cancel = true;
            RejectEdit("Use Edit row to choose a storage class when replacing a NULL value.");
        }
    }

    private void GridControl_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || _relationshipPickerColumns.Count == 0 ||
            IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu) ||
            GetCurrentCell() is not { Content: TextBlock } nativeCell ||
            nativeCell.Row?.Content is not GridRowPresentation row ||
            nativeCell.Column?.Tag is not string columnName ||
            !row.TryGetCell(columnName, out GridCellPresentation cell) ||
            !CanRequestRelationshipEdit(nativeCell, row, cell))
        {
            return;
        }

        // Intercept Enter on a display cell before TableView navigates to the next row.
        for (var element = args.OriginalSource as DependencyObject;
             element is not null && !ReferenceEquals(element, GridControl);
             element = VisualTreeHelper.GetParent(element))
        {
            if (!ReferenceEquals(element, nativeCell))
            {
                continue;
            }

            args.Handled = true;
            if (!args.KeyStatus.WasKeyDown)
            {
                QueueRelationshipEdit(row, cell);
            }
            return;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private bool CanRequestRelationshipEdit(
        WinUI.TableView.TableViewCell nativeCell,
        GridRowPresentation row,
        GridCellPresentation cell)
    {
        int rowIndex = nativeCell.Slot.Row;
        return RelationshipEditRequested is not null && _isEditingAllowed && !_isBinding && IsEnabled &&
            !nativeCell.IsReadOnly && row.Identity is not null && cell.Value.Kind != SqliteValueKind.Blob &&
            _relationshipPickerColumns.Contains(cell.ColumnName) &&
            (uint)rowIndex < (uint)_rowSource.Items.Length && ReferenceEquals(_rowSource.Items[rowIndex], row);
    }

    private void QueueRelationshipEdit(GridRowPresentation row, GridCellPresentation cell)
    {
        if (_pendingRelationshipEdit is not null || row.Identity is not { } identity)
        {
            return;
        }

        var request = new RelationshipCellEditRequest(row.TableName, identity, row.Revision, cell.ColumnName, cell.Value);
        long generation = _bindGeneration;
        _pendingRelationshipEdit = request;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!ReferenceEquals(_pendingRelationshipEdit, request))
                {
                    return;
                }

                _pendingRelationshipEdit = null;
                if (generation == _bindGeneration && _isEditingAllowed && !_isBinding && IsEnabled)
                {
                    RelationshipEditRequested?.Invoke(this, request);
                }
            }))
        {
            _pendingRelationshipEdit = null;
        }
    }

    private void GridControl_CellEditEnding(
        object sender,
        WinUI.TableView.TableViewCellEditEndingEventArgs args)
    {
        _editStager.Clear();
        if (args.EditAction != WinUI.TableView.TableViewEditAction.Commit ||
            args.DataItem is not GridRowPresentation row ||
            row.Identity is null ||
            args.Column.Tag is not string columnName ||
            args.EditingElement is not TextBox editor ||
            !row.TryGetCell(columnName, out GridCellPresentation cell))
        {
            return;
        }

        if (FormatInlineValue(cell.Value).Equals(editor.Text, StringComparison.Ordinal))
        {
            return;
        }

        if (!TryParseInlineValue(cell.Value.Kind, editor.Text, out SqliteValue value, out string? error))
        {
            args.Cancel = true;
            RejectEdit($"{columnName}: {error}");
            return;
        }

        if (value.Equals(cell.Value))
        {
            return;
        }

        _editStager.Stage(new RowUpdateOperation(
            Guid.NewGuid(),
            row.TableName,
            DateTimeOffset.UtcNow,
            row.Identity,
            [KeyValuePair.Create(columnName, cell.Value)],
            [KeyValuePair.Create(columnName, value)],
            row.Revision),
            _bindGeneration,
            row,
            columnName);
    }

    private void GridControl_CellEditEnded(
        object sender,
        WinUI.TableView.TableViewCellEditEndedEventArgs args)
    {
        EditOperation? operation = _editStager.Complete(
            args.EditAction == WinUI.TableView.TableViewEditAction.Commit,
            _bindGeneration,
            args.DataItem,
            args.Column.Tag as string);
        if (operation is not null)
        {
            AnnounceEdit(operation);
        }
    }

    private static bool TryParseInlineValue(
        SqliteValueKind storageClass,
        string text,
        out SqliteValue value,
        out string? error)
    {
        switch (storageClass)
        {
            case SqliteValueKind.Integer when long.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long integer):
                value = SqliteValue.Integer(integer);
                error = null;
                return true;
            case SqliteValueKind.Integer:
                value = SqliteValue.Null;
                error = "Enter a whole number using invariant digits.";
                return false;
            case SqliteValueKind.Real when double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double real) &&
                double.IsFinite(real):
                value = SqliteValue.Real(real);
                error = null;
                return true;
            case SqliteValueKind.Real:
                value = SqliteValue.Null;
                error = "Enter a finite number using a period as the decimal separator.";
                return false;
            case SqliteValueKind.Text:
                value = SqliteValue.Text(text);
                error = null;
                return true;
            default:
                value = SqliteValue.Null;
                error = "Use Edit row to choose NULL or another SQLite storage class.";
                return false;
        }
    }

    private static string FormatInlineValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Integer => value.IntegerValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => value.RealValue.ToString(
            "R",
            System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        _ => string.Empty,
    };

    // Kept explicit so future TableView edit events can enter the application port without
    // leaking package-specific event arguments into Application.
    internal void AnnounceSelection(GridSelection selection)
    {
        SelectionChanged?.Invoke(this, selection);
        QueuePresentedRowChanged();
    }

    internal void AnnounceColumnHeaderActivation(string columnName) =>
        ColumnHeaderActivated?.Invoke(this, columnName);

    internal void AnnounceEdit(EditOperation operation) => EditCommitted?.Invoke(this, operation);

    private void RejectEdit(string message) => EditRejected?.Invoke(this, message);

    /// <summary>
    /// Returns direct text elements so TableView 1.4.1 takes its bounded template-column
    /// measurement path instead of repeatedly measuring a wide grid at infinite width.
    /// </summary>
    private sealed class DirectTextTableViewColumn : WinUI.TableView.TableViewTemplateColumn
    {
        private readonly int _cellIndex;

        public DirectTextTableViewColumn(int cellIndex, bool isReadOnly)
        {
            _cellIndex = cellIndex;

            // TableView 1.4.1 treats a template column with no editing template as
            // read-only before it calls GenerateEditingElement. The generated element
            // remains the direct TextBox below; this template is only the editability marker.
            if (!isReadOnly)
            {
                EditingTemplate = new DataTemplate();
            }
        }

        public override FrameworkElement GenerateElement(
            WinUI.TableView.TableViewCell cell,
            object? dataItem)
        {
            var textBlock = new TextBlock
            {
                Margin = new Thickness(12, 0, 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ApplyPresentation(cell, textBlock, dataItem);
            return textBlock;
        }

        public override FrameworkElement GenerateEditingElement(
            WinUI.TableView.TableViewCell cell,
            object? dataItem)
        {
            return new TextBox
            {
                IsSpellCheckEnabled = false,
                Text = GetPresentation(dataItem) is { } presentation
                    ? FormatInlineValue(presentation.Value)
                    : string.Empty,
            };
        }

        public override void RefreshElement(
            WinUI.TableView.TableViewCell cell,
            object? dataItem)
        {
            if (cell.Content is TextBlock textBlock)
            {
                ApplyPresentation(cell, textBlock, dataItem);
            }
            else
            {
                cell.Content = GenerateElement(cell, dataItem);
            }
        }

        protected override object? PrepareCellForEdit(
            WinUI.TableView.TableViewCell cell,
            RoutedEventArgs routedEvent)
        {
            if (cell.Content is TextBox textBox)
            {
                textBox.SelectAll();
                return textBox.Text;
            }

            return base.PrepareCellForEdit(cell, routedEvent);
        }

        public override object? GetCellContent(object? dataItem) => GetText(dataItem);

        public override object? GetClipboardContent(object? dataItem) => GetText(dataItem);

        private void ApplyPresentation(
            WinUI.TableView.TableViewCell cell,
            TextBlock textBlock,
            object? dataItem)
        {
            GridCellPresentation? presentation = GetPresentation(dataItem);
            if (presentation is { CombinedLabel: { } label })
            {
                textBlock.Inlines.Clear();
                textBlock.Inlines.Add(new Run { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                textBlock.Inlines.Add(new Run { Text = presentation.Display[label.Length..] });
                textBlock.Tag = true;
            }
            else
            {
                // Clear reused runs without allocating an inline collection for plain cells.
                if (textBlock.Tag is true)
                {
                    textBlock.Inlines.Clear();
                    textBlock.Tag = null;
                }
                textBlock.Text = GetText(dataItem);
            }

            if (presentation is not null && (presentation.IsDisplayProjection || presentation.RelationshipHelp is not null))
            {
                string display = presentation.Display;
                string? help = presentation.RelationshipHelp;
                AutomationProperties.SetName(cell, display);
                AutomationProperties.SetHelpText(cell, help ?? string.Empty);
                ToolTipService.SetToolTip(cell, help is null ? display : $"{display}\n{help}");
            }
            else
            {
                cell.ClearValue(AutomationProperties.NameProperty);
                cell.ClearValue(AutomationProperties.HelpTextProperty);
                cell.ClearValue(ToolTipService.ToolTipProperty);
            }
        }

        private string GetText(object? dataItem) => GetPresentation(dataItem)?.Display ?? string.Empty;

        private GridCellPresentation? GetPresentation(object? dataItem) =>
            dataItem is GridRowPresentation row &&
                (uint)_cellIndex < (uint)row.Cells.Count
                ? row.Cells[_cellIndex]
                : null;
    }

    private sealed record ColumnFingerprintHeader(
        string TableName,
        TableEditCapability EditCapability,
        int FrozenColumnCount);

    private readonly record struct GridColumnFingerprint(
        string Name,
        int Ordinal,
        bool IsGenerated,
        bool IsPrimaryKey,
        bool IsVisible,
        int DisplayIndex,
        long WidthBits);
}

public sealed record RelationshipCellEditRequest(
    string TableName,
    RowIdentity Identity,
    RowRevision Revision,
    string ColumnName,
    SqliteValue OldValue);

public sealed class GridRowPresentation
{
    public GridRowPresentation(
        string tableName,
        TypedRow row,
        IReadOnlyList<ColumnSchema> columns,
        ForeignKeyDisplayMode displayMode)
    {
        TableName = tableName;
        Row = row;
        Identity = row.Identity;
        Revision = row.Revision;
        Cells = columns.Select(column => new GridCellPresentation(
            column.Name,
            row.Values.TryGetValue(column.Name, out SqliteValue value)
                ? value
                : SqliteValue.Null,
            row.RelatedValues.GetValueOrDefault(column.Name),
            displayMode)).ToArray();
    }

    public string TableName { get; }

    public TypedRow Row { get; }

    public RowIdentity? Identity { get; }

    public RowRevision Revision { get; }

    public IReadOnlyList<GridCellPresentation> Cells { get; }

    public bool TryGetCell(string columnName, out GridCellPresentation cell)
    {
        cell = Cells.FirstOrDefault(candidate =>
            candidate.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))!;
        return cell is not null;
    }
}

public sealed record GridCellPresentation
{
    public GridCellPresentation(
        string columnName,
        SqliteValue value,
        RelatedValue? relatedValue,
        ForeignKeyDisplayMode displayMode)
    {
        ColumnName = columnName;
        Value = value;
        string? label = relatedValue is { Resolution: RelationshipResolution.Resolved, Label: { } text } &&
            !string.IsNullOrWhiteSpace(text) ? text : null;
        IsDisplayProjection = label is not null &&
            displayMode is ForeignKeyDisplayMode.RawAndName or ForeignKeyDisplayMode.ResolvedName;
        CombinedLabel = displayMode == ForeignKeyDisplayMode.RawAndName ? label : null;
        Display = displayMode switch
        {
            ForeignKeyDisplayMode.RawAndName when label is not null => $"{label} · {Format(value)}",
            ForeignKeyDisplayMode.ResolvedName when label is not null => label,
            _ => Format(value),
        };
        RelationshipHelp = relatedValue?.Resolution switch
        {
            RelationshipResolution.TargetUnavailable => "The referenced table or key column is unavailable.",
            RelationshipResolution.MissingTarget => "The referenced record could not be found for this stored key.",
            RelationshipResolution.AmbiguousKey => "More than one record matches this stored key.",
            RelationshipResolution.LabelUnavailable => "No readable name is available for this relationship.",
            RelationshipResolution.Resolved when label is null => "No readable name is available for this relationship.",
            RelationshipResolution.UnknownRelationship => "No known relationship for this column.",
            RelationshipResolution.AmbiguousRelationship => "This column has more than one possible relationship.",
            RelationshipResolution.CompositeRelationship => "This relationship uses multiple columns. Showing the stored value.",
            _ => null,
        };
    }

    public string ColumnName { get; }
    public SqliteValue Value { get; }
    public bool IsDisplayProjection { get; }
    public string Display { get; }
    public string? CombinedLabel { get; }
    public string? RelationshipHelp { get; }

    private static string Format(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null => "NULL",
        SqliteValueKind.Integer => value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Real => value.RealValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        SqliteValueKind.Text => value.TextValue ?? string.Empty,
        SqliteValueKind.Blob => $"BLOB · {GetBlobLength(value.BlobBase64):N0} bytes",
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
}
