using Microsoft.VisualStudio.TestTools.UnitTesting;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class WorkspaceCloseWorkflowTests
{
    [TestMethod]
    public async Task UnsuccessfulSaveKeepsTheWorkspaceOpen()
    {
        WorkspaceCloseCompletion result = await WorkspaceCloseWorkflow.CompleteAsync(
            _ => Task.FromResult(false),
            _ => throw new AssertFailedException("An unsuccessful save must not close the workspace."),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(WorkspaceCloseOutcome.SaveDidNotComplete, result.Outcome);
    }

    [TestMethod]
    public async Task CancelAfterSaveCommitsKeepsTheWindowOpenAndReportsTheSuccessfulSave()
    {
        using var gate = new ExclusiveOperationGate();
        Assert.IsTrue(gate.TryEnter(CancellationToken.None, out var active));
        using ExclusiveOperationGate.ExclusiveOperationLease lease = active!;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closeCalled = false;

        Task<WorkspaceCloseCompletion> closing = WorkspaceCloseWorkflow.CompleteAsync(
            _ =>
            {
                committed.SetResult();
                return finishSave.Task;
            },
            _ =>
            {
                closeCalled = true;
                return Task.FromResult(new WorkspaceCloseResult(null));
            },
            lease.Token);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lease.Cancel();
        }
        finally
        {
            finishSave.SetResult(true);
        }

        WorkspaceCloseCompletion result = await closing.ConfigureAwait(false);
        Assert.AreEqual(WorkspaceCloseOutcome.SavedButCloseCancelled, result.Outcome);
        Assert.IsFalse(closeCalled);
    }

    [TestMethod]
    public async Task CancellationWhileDrainingAfterASaveReportsTheSuccessfulSave()
    {
        using var cancellation = new CancellationTokenSource();
        WorkspaceCloseCompletion result = await WorkspaceCloseWorkflow.CompleteAsync(
            _ => Task.FromResult(true),
            token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<WorkspaceCloseResult>(token);
            },
            cancellation.Token).ConfigureAwait(false);

        Assert.AreEqual(WorkspaceCloseOutcome.SavedButCloseCancelled, result.Outcome);
    }
}
