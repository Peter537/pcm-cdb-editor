using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class NavigationSelectionSourceTests
{
    [TestMethod]
    public void SettingsCompletionRestoresTheLastContentNavigationSelection()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        int settingsBranch = source.IndexOf(
            "if (args.IsSettingsSelected)",
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, settingsBranch);

        int finallyBlock = source.IndexOf("finally", settingsBranch, StringComparison.Ordinal);
        Assert.IsGreaterThan(settingsBranch, finallyBlock);

        int restoreCall = source.IndexOf(
            "RestoreContentNavigationSelection();",
            finallyBlock,
            StringComparison.Ordinal);
        Assert.IsGreaterThan(finallyBlock, restoreCall);

        int settingsBranchReturn = source.IndexOf("return;", restoreCall, StringComparison.Ordinal);
        Assert.IsGreaterThan(restoreCall, settingsBranchReturn);
        StringAssert.Contains(
            source,
            "Navigation.SelectedItem = _lastContentNavigationItem ?? TablesNavigationItem;",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void TabBindingRestoresCommittedViewportAndSortIndicatorFromContext()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        int methodStart = source.IndexOf(
            "private void EnsureTabBound(TabViewItem tab)",
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, methodStart);
        int methodEnd = source.IndexOf(
            "private void UpdateTabChrome(",
            methodStart,
            StringComparison.Ordinal);
        Assert.IsGreaterThan(methodStart, methodEnd);

        string methodBody = source[methodStart..methodEnd];
        StringAssert.Contains(
            methodBody,
            "TableGrid.RestoreViewport(context.Viewport);",
            StringComparison.Ordinal);
        StringAssert.Contains(
            methodBody,
            "context.Sorts",
            StringComparison.Ordinal);
        StringAssert.Contains(
            methodBody,
            "TableGrid.SetPrimarySortIndicator(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            methodBody,
            "sortIndicator?.HeaderColumnName",
            StringComparison.Ordinal);
        StringAssert.Contains(
            methodBody,
            "sortIndicator?.Direction",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void TableLoadingUsesOnlyExplicitOrOpenTabSorts()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        int openStart = source.IndexOf(
            "private async Task OpenTableAsync(",
            StringComparison.Ordinal);
        int bindStart = source.IndexOf(
            "private void EnsureTabBound(",
            openStart,
            StringComparison.Ordinal);
        Assert.IsTrue(openStart >= 0 && bindStart > openStart);

        string openBody = source[openStart..bindStart];
        StringAssert.Contains(
            openBody,
            "sorts ?? existingContext?.Sorts",
            StringComparison.Ordinal);
        Assert.IsFalse(
            openBody.Contains("viewState?.Sorts", StringComparison.Ordinal),
            "A newly opened table must not restore sorting from persisted view state.");
        StringAssert.Contains(
            openBody,
            "query.Sorts",
            StringComparison.Ordinal);
        StringAssert.Contains(
            openBody,
            "new TableTabContext(",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void OpenTabQueryReloadsRetainInMemorySortsWithoutPersistingThem()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        int reloadStart = source.IndexOf(
            "private async Task<TableQueryOutcome> LoadTableQueryAsync(",
            StringComparison.Ordinal);
        int parseFilterStart = source.IndexOf(
            "private static bool TryParseFilterValue(",
            reloadStart,
            StringComparison.Ordinal);
        Assert.IsTrue(reloadStart >= 0 && parseFilterStart > reloadStart);

        string reloadBody = source[reloadStart..parseFilterStart];
        StringAssert.Contains(
            reloadBody,
            "sorts ?? context.Sorts",
            StringComparison.Ordinal);

        int captureStart = source.IndexOf(
            "private void CaptureCurrentTabState(",
            StringComparison.Ordinal);
        int persistStart = source.IndexOf(
            "private async Task PersistActiveTableStateAsync(",
            captureStart,
            StringComparison.Ordinal);
        Assert.IsTrue(captureStart >= 0 && persistStart > captureStart);

        string captureBody = source[captureStart..persistStart];
        StringAssert.Contains(
            captureBody,
            "TableGrid.CaptureViewState(",
            StringComparison.Ordinal);
        Assert.IsFalse(
            captureBody.Contains("context.Sorts", StringComparison.Ordinal),
            "Capturing a persistent column layout must not include the open tab's sort descriptors.");
    }

    private static string GetMainWindowCodePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "PcmCdbEditor.App",
                "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "MainWindow.xaml.cs could not be resolved from the test output directory.");
    }
}
