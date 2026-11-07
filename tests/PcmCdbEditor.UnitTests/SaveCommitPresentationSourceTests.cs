using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class SaveCommitPresentationSourceTests
{
    [TestMethod]
    public void CommittedMetadataFailureUsesSavedDestinationPresentationForSaveAndSaveAs()
    {
        string mainPath = GetMainWindowCodePath();
        string saveSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(mainPath)!, "MainWindow.SaveWorkspace.cs"));
        StringAssert.Contains(saveSource, "SaveWorkspaceAsync(chooseDestination: false", StringComparison.Ordinal);
        StringAssert.Contains(saveSource, "SaveWorkspaceAsync(chooseDestination: true", StringComparison.Ordinal);
        StringAssert.Contains(saveSource, "catch (WorkspaceSaveCommitException exception)", StringComparison.Ordinal);
        StringAssert.Contains(saveSource, "AdoptCommittedSaveAfterMetadataFailureAsync(exception.CommittedSave, saveAs, retireHistory)",
            StringComparison.Ordinal);

        string mainSource = File.ReadAllText(mainPath);
        int helperStart = mainSource.IndexOf("private async Task AdoptCommittedSaveAfterMetadataFailureAsync", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, helperStart);
        int helperEnd = mainSource.IndexOf("private async void AppWindow_Closing", helperStart, StringComparison.Ordinal);
        Assert.IsGreaterThan(helperStart, helperEnd);
        string committedStateHelper = mainSource[helperStart..helperEnd];
        StringAssert.Contains(committedStateHelper, "_session = committedSave.Session;", StringComparison.Ordinal);
        StringAssert.Contains(committedStateHelper, "rememberDestination", StringComparison.Ordinal);
        StringAssert.Contains(committedStateHelper, "Reopen the saved CDB before making more edits.", StringComparison.Ordinal);
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
