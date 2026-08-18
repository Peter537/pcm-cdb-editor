using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App.Controls;

/// <summary>
/// Keeps every WinUI.TableView type inside the application adapter boundary.
/// </summary>
public sealed partial class TableGridAdapterControl : UserControl, ITableGridAdapter
{
    private readonly BulkRowSource<GridRowPresentation> _rowSource = new();
    private readonly InlineEditCommitStager _editStager = new();
    private ColumnFingerprintHeader? _columnFingerprintHeader;
    private GridColumnFingerprint[] _columnFingerprint = [];
    private long _bindGeneration;
    private bool _isBinding;

    public TableGridAdapterControl()
    {
        InitializeComponent();
    }

    public IReadOnlyList<GridRowPresentation> Rows => _rowSource.Items;

    public event EventHandler<GridSelection>? SelectionChanged;

    public event EventHandler<EditOperation>? EditCommitted;

    /// <summary>
    /// Reports an edit that the adapter cannot safely represent without exposing
    /// package-specific event arguments outside this control.
    /// </summary>
    public event EventHandler<string>? EditRejected;

    public void Bind(TableSchema schema, IReadOnlyList<TypedRow> rows, TableViewState? state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        _editStager.Clear();

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
        _rowSource.Replace(
            rows,
            row => new GridRowPresentation(schema.Name, row, visibleColumns));
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
        _isBinding = true;
        try
        {
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
                CanSort = false,
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

    public TableViewState CaptureViewState(
        string schemaSignature,
        string tableName,
        IReadOnlyList<SortDescriptor> sorts,
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
            sorts,
            density,
            GridControl.FrozenColumnCount);
    }

    public GridSelection CaptureSelection()
    {
        RowIdentity? identity = (GridControl.SelectedItem as GridRowPresentation)?.Identity;
        IList<WinUI.TableView.TableViewColumn> visibleColumns = GridControl.Columns.VisibleColumns;
        string? currentColumn = GridControl.CurrentCellSlot is { } slot &&
            slot.Column >= 0 &&
            slot.Column < visibleColumns.Count
                ? visibleColumns[slot.Column].Tag as string
                : null;
        RowIdentity[] selected = GridControl.SelectedItems
            .OfType<GridRowPresentation>()
            .Select(static row => row.Identity)
            .OfType<RowIdentity>()
            .ToArray();
        return new GridSelection(identity, currentColumn, selected);
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
        return new GridViewport(
            _rowSource.Items[firstIndex].Identity,
            checked((int)Math.Round(GridControl.HorizontalOffset, MidpointRounding.ToEven)));
    }

    public void RestoreSelection(GridSelection selection)
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

        GridControl.SelectedItems.Clear();
        foreach (GridRowPresentation selected in resolved.SelectedRows)
        {
            GridControl.SelectedItems.Add(selected);
        }

        GridControl.SelectedItem = resolved.CurrentRow;
        if (resolved.CurrentRow is not null && resolved.CurrentColumn is not null)
        {
            int rowIndex = Array.IndexOf(_rowSource.Items, resolved.CurrentRow);
            int columnIndex = visibleColumns
                .Select((column, index) => new { column, index })
                .Where(item => item.column.Tag is string name &&
                    name.Equals(resolved.CurrentColumn, StringComparison.OrdinalIgnoreCase))
                .Select(static item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (rowIndex >= 0 && columnIndex >= 0)
            {
                GridControl.CurrentCellSlot = new WinUI.TableView.TableViewCellSlot(
                    rowIndex,
                    columnIndex);
            }
        }
    }

    public void RestoreViewport(GridViewport viewport)
    {
        if (viewport.FirstVisibleRow is null && viewport.HorizontalOffset == 0)
        {
            return;
        }

        long generation = _bindGeneration;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => RestoreViewport(viewport, generation));
    }

    private void RestoreViewport(GridViewport viewport, long generation)
    {
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

        FindDescendantScrollViewer(GridControl)?.ChangeView(
            viewport.HorizontalOffset,
            null,
            null,
            disableAnimation: true);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < children; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            if (FindDescendantScrollViewer(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void GridControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBinding)
        {
            return;
        }

        AnnounceSelection(CaptureSelection());
    }

    private void GridControl_BeginningEdit(
        object sender,
        WinUI.TableView.TableViewBeginningEditEventArgs args)
    {
        _editStager.Clear();
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
    internal void AnnounceSelection(GridSelection selection) => SelectionChanged?.Invoke(this, selection);

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
            return new TextBlock
            {
                Margin = new Thickness(12, 0, 12, 0),
                Text = GetText(dataItem),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        public override FrameworkElement GenerateEditingElement(
            WinUI.TableView.TableViewCell cell,
            object? dataItem)
        {
            return new TextBox
            {
                IsSpellCheckEnabled = false,
                Text = GetText(dataItem),
            };
        }

        public override void RefreshElement(
            WinUI.TableView.TableViewCell cell,
            object? dataItem)
        {
            if (cell.Content is TextBlock textBlock)
            {
                textBlock.Text = GetText(dataItem);
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

        private string GetText(object? dataItem)
        {
            return dataItem is GridRowPresentation row &&
                (uint)_cellIndex < (uint)row.Cells.Count
                    ? row.Cells[_cellIndex].Display
                    : string.Empty;
        }
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

public sealed class GridRowPresentation
{
    public GridRowPresentation(string tableName, TypedRow row, IReadOnlyList<ColumnSchema> columns)
    {
        TableName = tableName;
        Identity = row.Identity;
        Revision = row.Revision;
        Cells = columns.Select(column => new GridCellPresentation(
            column.Name,
            row.Values.TryGetValue(column.Name, out SqliteValue value)
                ? value
                : SqliteValue.Null,
            row.Values.TryGetValue($"{column.Name}__display", out SqliteValue displayValue)
                ? displayValue
                : null)).ToArray();
    }

    public string TableName { get; }

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

public sealed record GridCellPresentation(
    string ColumnName,
    SqliteValue Value,
    SqliteValue? DisplayValue = null)
{
    public bool IsDisplayProjection => DisplayValue.HasValue;

    public string Display { get; } = Format(DisplayValue ?? Value);

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
