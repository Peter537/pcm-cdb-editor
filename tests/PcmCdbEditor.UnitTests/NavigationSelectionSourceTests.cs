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
