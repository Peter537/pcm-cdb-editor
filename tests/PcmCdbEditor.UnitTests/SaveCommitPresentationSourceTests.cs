using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SaveCommitPresentationSourceTests
{
    [TestMethod]
    public void CommittedMetadataFailureUsesSavedDestinationPresentationForSaveAndSaveAs()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        int saveStart = source.IndexOf(
            "private async Task<bool> SaveCurrentAsync",
            StringComparison.Ordinal);
        int saveAsStart = source.IndexOf(
            "private async void SaveAs_Click",
            saveStart,
            StringComparison.Ordinal);
        int committedStateHelper = source.IndexOf(
            "private async Task AdoptCommittedSaveAfterMetadataFailureAsync",
            saveAsStart,
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, saveStart);
        Assert.IsGreaterThan(saveStart, saveAsStart);
        Assert.IsGreaterThan(saveAsStart, committedStateHelper);

        string saveSection = source[saveStart..saveAsStart];
        string saveAsSection = source[saveAsStart..committedStateHelper];
        StringAssert.Contains(
            saveSection,
            "catch (WorkspaceSaveCommitException exception)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            saveSection,
            "rememberDestination: false",
            StringComparison.Ordinal);
        StringAssert.Contains(
            saveAsSection,
            "catch (WorkspaceSaveCommitException exception)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            saveAsSection,
            "rememberDestination: true",
            StringComparison.Ordinal);

        StringAssert.Contains(
            source,
            "The destination was saved, but the app could not update the saved session information.",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "The destination was saved, but the app could not update the saved session information or mark the current Undo history as saved.",
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
