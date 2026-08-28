using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class TableGridAdapterSourceTests
{
    [TestMethod]
    public void WideGridColumnsUseDirectTemplateElementsWithoutLosingEditOrClipboardContracts()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());

        Assert.IsFalse(
            source.Contains("new WinUI.TableView.TableViewTextColumn", StringComparison.Ordinal),
            "TableViewTextColumn re-enters the 1.4.1 infinite-width measurement path.");
        StringAssert.Contains(
            source,
            "DirectTextTableViewColumn : WinUI.TableView.TableViewTemplateColumn",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override FrameworkElement GenerateElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override FrameworkElement GenerateEditingElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "Text = GetText(dataItem)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override void RefreshElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(source, "EditingTemplate = new DataTemplate()", StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override object? GetClipboardContent(object? dataItem) => GetText(dataItem)",
            StringComparison.Ordinal);
        Assert.IsFalse(
            source.Contains("SetBinding(", StringComparison.Ordinal),
            "Wide-grid display and edit elements must not allocate per-cell binding expressions.");
        Assert.IsFalse(
            source.Contains("new ContentControl", StringComparison.Ordinal),
            "Cell elements must remain direct children for bounded TableView measurement.");
    }

    [TestMethod]
    public void InlineEditMutationIsPublishedOnlyAfterTableViewFinishesTheCommit()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        string xaml = File.ReadAllText(GetAdapterXamlPath());

        StringAssert.Contains(
            xaml,
            "CellEditEnding=\"GridControl_CellEditEnding\"",
            StringComparison.Ordinal);
        StringAssert.Contains(
            xaml,
            "CellEditEnded=\"GridControl_CellEditEnded\"",
            StringComparison.Ordinal);

        int endingStart = source.IndexOf(
            "private void GridControl_CellEditEnding(",
            StringComparison.Ordinal);
        int endedStart = source.IndexOf(
            "private void GridControl_CellEditEnded(",
            StringComparison.Ordinal);
        Assert.IsTrue(endingStart >= 0, "The cancelable validation event must remain wired.");
        Assert.IsTrue(endedStart > endingStart, "The post-commit event must publish staged edits.");

        string endingBody = source[endingStart..endedStart];
        Assert.IsFalse(
            endingBody.Contains("AnnounceEdit(", StringComparison.Ordinal),
            "CellEditEnding runs before TableView commits and must not start a mutation or rebind.");
        StringAssert.Contains(
            source[endedStart..],
            "AnnounceEdit(",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void HeaderSortingIsInterceptedAndPublicationIsDeferredPastTheNativeTap()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        string xaml = File.ReadAllText(GetAdapterXamlPath());

        StringAssert.Contains(xaml, "CanSortColumns=\"True\"", StringComparison.Ordinal);
        StringAssert.Contains(
            xaml,
            "Sorting=\"GridControl_Sorting\"",
            StringComparison.Ordinal);
        StringAssert.Contains(
            xaml,
            "Click or tap a column header to make it the primary sort",
            StringComparison.Ordinal);
        StringAssert.Contains(source, "CanSort = true,", StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public event EventHandler<string>? ColumnHeaderActivated;",
            StringComparison.Ordinal);

        int sortingStart = source.IndexOf(
            "private void GridControl_Sorting(",
            StringComparison.Ordinal);
        int beginningEditStart = source.IndexOf(
            "private void GridControl_BeginningEdit(",
            StringComparison.Ordinal);
        Assert.IsTrue(sortingStart >= 0, "The package sorting event must remain intercepted.");
        Assert.IsTrue(
            beginningEditStart > sortingStart,
            "The header sorting handler could not be isolated from the adapter source.");

        string sortingBody = source[sortingStart..beginningEditStart];
        int handledIndex = sortingBody.IndexOf("args.Handled = true;", StringComparison.Ordinal);
        int tagIndex = sortingBody.IndexOf("args.Column.Tag is string columnName", StringComparison.Ordinal);
        int enqueueIndex = sortingBody.IndexOf("DispatcherQueue.TryEnqueue(", StringComparison.Ordinal);
        int publishIndex = sortingBody.IndexOf("AnnounceColumnHeaderActivation(columnName);", StringComparison.Ordinal);
        Assert.IsTrue(handledIndex >= 0, "TableView must never sort only the loaded page.");
        Assert.IsTrue(
            tagIndex > handledIndex,
            "Package sorting must be handled before the column tag is validated.");
        Assert.IsTrue(
            enqueueIndex > tagIndex,
            "A verified header activation must wait until the native header tap has unwound.");
        Assert.IsTrue(
            publishIndex > enqueueIndex,
            "Only the deferred callback may publish the physical column tag.");
        StringAssert.Contains(
            sortingBody,
            "DispatcherQueuePriority.Low",
            StringComparison.Ordinal);
        Assert.IsFalse(
            source.Contains("SortDescriptions", StringComparison.Ordinal),
            "Database-side sorting must not add local TableView sort descriptions.");
    }

    [TestMethod]
    public void PrimarySortIndicatorClearsEveryArrowBeforeSettingAtMostOne()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());

        StringAssert.Contains(
            source,
            "public void SetHeaderSortingEnabled(bool isEnabled)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "GridControl.CanSortColumns = isEnabled;",
            StringComparison.Ordinal);

        int indicatorStart = source.IndexOf(
            "public void SetPrimarySortIndicator(",
            StringComparison.Ordinal);
        int captureStateStart = source.IndexOf(
            "public TableViewState CaptureViewState(",
            StringComparison.Ordinal);
        Assert.IsTrue(indicatorStart >= 0, "The primary header indicator API must remain available.");
        Assert.IsTrue(
            captureStateStart > indicatorStart,
            "The primary header indicator method could not be isolated from the adapter source.");

        string indicatorBody = source[indicatorStart..captureStateStart];
        int clearIndex = indicatorBody.IndexOf("column.SortDirection = null;", StringComparison.Ordinal);
        int lookupIndex = indicatorBody.IndexOf(
            "WinUI.TableView.TableViewColumn? primaryColumn =",
            StringComparison.Ordinal);
        int setIndex = indicatorBody.IndexOf(
            "primaryColumn.SortDirection = direction switch",
            StringComparison.Ordinal);
        Assert.IsTrue(clearIndex >= 0, "All stale native sort arrows must be cleared.");
        Assert.IsTrue(lookupIndex > clearIndex, "The primary column must be resolved after clearing arrows.");
        Assert.IsTrue(setIndex > lookupIndex, "At most one resolved header may receive a native arrow.");
        StringAssert.Contains(
            source,
            "GridControl.LayoutUpdated += GridControl_LayoutUpdated;",
            "The adapter must wait for regenerated native header presenters.");
        StringAssert.Contains(
            indicatorBody,
            "_pendingSortIndicatorGeneration == _sortIndicatorGeneration",
            StringComparison.Ordinal);
        StringAssert.Contains(
            indicatorBody,
            "_pendingSortIndicatorBindGeneration == _bindGeneration",
            StringComparison.Ordinal);
        StringAssert.Contains(
            indicatorBody,
            "ApplyPrimarySortIndicator(",
            StringComparison.Ordinal);
        Assert.AreEqual(
            1,
            indicatorBody.Split(
                "primaryColumn.SortDirection =",
                StringSplitOptions.None).Length - 1,
            "Only the primary column may receive a native sort direction.");
    }

    [TestMethod]
    public void ViewportRestoreDefersPastTheBindAndUsesTableViewHorizontalOffset()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        int viewportStart = source.IndexOf(
            "public void RestoreViewport(GridViewport viewport)",
            StringComparison.Ordinal);
        int selectionChangedStart = source.IndexOf(
            "private void GridControl_SelectionChanged(",
            viewportStart,
            StringComparison.Ordinal);
        Assert.IsTrue(viewportStart >= 0, "The viewport restore entry point must remain available.");
        Assert.IsTrue(
            selectionChangedStart > viewportStart,
            "The viewport restore implementation could not be isolated from the adapter source.");

        string viewportBody = source[viewportStart..selectionChangedStart];
        int generationCapture = viewportBody.IndexOf(
            "long generation = _bindGeneration;",
            StringComparison.Ordinal);
        int enqueue = viewportBody.IndexOf(
            "DispatcherQueue.TryEnqueue(",
            StringComparison.Ordinal);
        int deferredRestore = viewportBody.IndexOf(
            "() => RestoreViewport(viewport, generation)",
            StringComparison.Ordinal);
        int generationGuard = viewportBody.IndexOf(
            "if (generation != _bindGeneration)",
            StringComparison.Ordinal);
        int currentCellBarrier = viewportBody.IndexOf(
            "await GridControl.ScrollCellIntoView(currentCellSlot);",
            StringComparison.Ordinal);
        int selectedRowBarrier = viewportBody.IndexOf(
            "await GridControl.ScrollRowIntoView(GridControl.SelectedIndex);",
            StringComparison.Ordinal);
        int postRowGenerationGuard = selectedRowBarrier < 0
            ? -1
            : viewportBody.IndexOf(
                "if (generation != _bindGeneration)",
                selectedRowBarrier,
                StringComparison.Ordinal);
        int postScrollGenerationGuard = currentCellBarrier < 0
            ? -1
            : viewportBody.IndexOf(
                "if (generation != _bindGeneration)",
                currentCellBarrier,
                StringComparison.Ordinal);
        int verticalRestore = viewportBody.IndexOf(
            "GridControl.ScrollIntoView(first);",
            StringComparison.Ordinal);
        int horizontalRestore = viewportBody.IndexOf(
            "GridControl.SetValue(",
            StringComparison.Ordinal);
        int horizontalOffsetProperty = viewportBody.IndexOf(
            "WinUI.TableView.TableView.HorizontalOffsetProperty",
            StringComparison.Ordinal);
        int horizontalOffsetValue = horizontalOffsetProperty < 0
            ? -1
            : viewportBody.IndexOf(
                "viewport.HorizontalOffset",
                horizontalOffsetProperty,
                StringComparison.Ordinal);

        Assert.IsTrue(generationCapture >= 0, "Deferred restoration must capture the current bind generation.");
        Assert.IsTrue(enqueue > generationCapture, "Viewport restoration must be deferred until rows are realized.");
        StringAssert.Contains(viewportBody, "DispatcherQueuePriority.Low", StringComparison.Ordinal);
        Assert.IsTrue(deferredRestore > enqueue, "The queued callback must carry the captured bind generation.");
        Assert.IsTrue(generationGuard > deferredRestore, "A superseded bind must not restore a stale viewport.");
        Assert.IsTrue(
            selectedRowBarrier > generationGuard,
            "The adapter must first await TableView's selection-driven row scrolling.");
        Assert.IsTrue(
            postRowGenerationGuard > selectedRowBarrier,
            "A bind superseded while TableView scrolls the selected row must stop before current-cell scrolling.");
        Assert.IsTrue(
            currentCellBarrier > postRowGenerationGuard,
            "TableView's current-cell scrolling must complete after its selected-row scrolling.");
        Assert.IsTrue(
            postScrollGenerationGuard > currentCellBarrier,
            "A bind superseded while TableView scrolls must not restore a stale viewport.");
        Assert.IsTrue(
            verticalRestore > postScrollGenerationGuard,
            "The visible row must be restored after TableView's selection-driven scrolling completes.");
        Assert.IsTrue(
            horizontalRestore > verticalRestore,
            "Horizontal offset restoration must follow the vertical ScrollIntoView operation.");
        Assert.IsTrue(
            horizontalOffsetProperty > horizontalRestore && horizontalOffsetValue > horizontalOffsetProperty,
            "The adapter must restore TableView's horizontal-offset dependency property.");
        Assert.IsFalse(
            source.Contains("FindDescendantScrollViewer", StringComparison.Ordinal),
            "Viewport restoration must not target an arbitrary descendant ScrollViewer.");
        Assert.IsFalse(
            viewportBody.Contains("ChangeView(", StringComparison.Ordinal),
            "TableView horizontal position must not be restored through ScrollViewer.ChangeView.");
    }

    [TestMethod]
    public void ColumnTopologyChangesNeutralizeHorizontalOffsetBeforeMutation()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        int bindStart = source.IndexOf(
            "public void Bind(TableSchema schema, IReadOnlyList<TypedRow> rows, TableViewState? state)",
            StringComparison.Ordinal);
        int clearStart = source.IndexOf(
            "public void Clear()",
            bindStart,
            StringComparison.Ordinal);
        int rebuildStart = source.IndexOf(
            "private void RebuildColumns(",
            clearStart,
            StringComparison.Ordinal);
        Assert.IsTrue(bindStart >= 0 && clearStart > bindStart && rebuildStart > clearStart);

        string bindBody = source[bindStart..clearStart];
        int captureViewport = bindBody.IndexOf("CaptureViewport()", StringComparison.Ordinal);
        int neutralizeForBind = bindBody.IndexOf(
            "NeutralizeHorizontalOffsetForColumnTopologyChange();",
            StringComparison.Ordinal);
        int replaceRows = bindBody.IndexOf("_rowSource.Replace(", StringComparison.Ordinal);
        int detachRows = bindBody.IndexOf("GridControl.ItemsSource = null;", StringComparison.Ordinal);
        int rebuildColumns = bindBody.IndexOf("RebuildColumns(", StringComparison.Ordinal);
        Assert.IsTrue(captureViewport >= 0, "The outgoing viewport must be captured before rebinding.");
        Assert.IsTrue(
            neutralizeForBind > captureViewport,
            "A topology change must preserve the outgoing viewport before neutralizing the shared TableView.");
        Assert.IsTrue(
            replaceRows > neutralizeForBind && detachRows > replaceRows && rebuildColumns > detachRows,
            "The stale horizontal offset must be zero before rows or columns are replaced.");

        string clearBody = source[clearStart..rebuildStart];
        int neutralizeForClear = clearBody.IndexOf(
            "NeutralizeHorizontalOffsetForColumnTopologyChange();",
            StringComparison.Ordinal);
        int clearRows = clearBody.IndexOf("GridControl.ItemsSource = null;", StringComparison.Ordinal);
        int clearColumns = clearBody.IndexOf("GridControl.Columns.Clear();", StringComparison.Ordinal);
        Assert.IsTrue(
            neutralizeForClear >= 0 && clearRows > neutralizeForClear && clearColumns > clearRows,
            "Clearing the grid must neutralize the horizontal offset before removing its topology.");

        StringAssert.Contains(
            clearBody,
            "WinUI.TableView.TableView.HorizontalOffsetProperty",
            StringComparison.Ordinal);
        StringAssert.Contains(clearBody, "0d", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CapturedViewStateContainsOnlyPersistentColumnLayout()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        int captureStart = source.IndexOf(
            "public TableViewState CaptureViewState(",
            StringComparison.Ordinal);
        int selectionStart = source.IndexOf(
            "public GridSelection CaptureSelection()",
            captureStart,
            StringComparison.Ordinal);
        Assert.IsTrue(captureStart >= 0 && selectionStart > captureStart);

        string captureBody = source[captureStart..selectionStart];
        StringAssert.Contains(
            captureBody,
            "new ColumnDisplayState(",
            StringComparison.Ordinal);
        Assert.IsFalse(
            captureBody.Contains("SortDescriptor", StringComparison.Ordinal),
            "Persisted adapter state must not accept sort descriptors.");
        Assert.IsFalse(
            captureBody.Contains("sorts", StringComparison.OrdinalIgnoreCase),
            "Persisted adapter state must not capture session-only sorting.");
    }

    private static string GetAdapterSourcePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "PcmCdbEditor.App",
                "Controls",
                "TableGridAdapterControl.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "TableGridAdapterControl.xaml.cs could not be resolved from the test output directory.");
    }

    private static string GetAdapterXamlPath()
    {
        string sourcePath = GetAdapterSourcePath();
        return sourcePath[..^".cs".Length];
    }
}
