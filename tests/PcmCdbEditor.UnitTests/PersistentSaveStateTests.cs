using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.App.ViewModels;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class PersistentSaveStateTests
{
    [TestMethod]
    public void OperationsCannotHideUnsavedStateOrChangeItsDestination()
    {
        var state = new ShellState
        {
            HasDatabase = true,
            HasUnsavedChanges = true,
            SaveDestination = @"C:\verification\output.cdb",
        };
        foreach (ShellOperationState operation in Enum.GetValues<ShellOperationState>())
        {
            state.State = operation;
            state.Status = "A transient operation result";
            Assert.AreEqual("Unsaved changes", state.SaveStatus);
            Assert.AreEqual(@"C:\verification\output.cdb", state.SaveDestination);
        }
        state.HasUnsavedChanges = false;
        Assert.AreEqual("Saved", state.SaveStatus);
    }

    [TestMethod]
    public void UncertainHistoryAlwaysShowsUnsavedEvenAtAnApparentlyCleanCursor()
    {
        var state = new ShellState { HasDatabase = true, HasHistoryUncertainty = true };
        Assert.AreEqual("Unsaved changes · incomplete history", state.SaveStatus);
        state.State = ShellOperationState.Ready;
        Assert.AreEqual("Unsaved changes · incomplete history", state.SaveStatus);
        state.HasHistoryUncertainty = false;
        Assert.AreEqual("Saved", state.SaveStatus);
        state.HasDatabase = false;
        Assert.AreEqual("No database open", state.SaveStatus);
    }
}
