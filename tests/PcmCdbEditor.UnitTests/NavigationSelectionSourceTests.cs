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
