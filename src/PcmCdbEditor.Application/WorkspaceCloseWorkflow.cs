namespace PcmCdbEditor.Application;

internal enum WorkspaceCloseOutcome
{
    Closed,
    SaveDidNotComplete,
    Cancelled,
    SavedButCloseCancelled
}

internal sealed record WorkspaceCloseCompletion(WorkspaceCloseOutcome Outcome, string? RemainingDirectory = null);

internal static class WorkspaceCloseWorkflow
{
    public static async Task<WorkspaceCloseCompletion> CompleteAsync(
        Func<CancellationToken, Task<bool>>? save,
        Func<CancellationToken, Task<WorkspaceCloseResult>> close,
        CancellationToken cancellationToken)
    {
        bool saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (save is not null)
            {
                // Both callbacks belong to the window and must resume on its dispatcher.
                if (!await save(cancellationToken).ConfigureAwait(true))
                {
                    return new(WorkspaceCloseOutcome.SaveDidNotComplete);
                }
                saved = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceCloseResult result = await close(cancellationToken).ConfigureAwait(true);
            return new(WorkspaceCloseOutcome.Closed, result.RemainingDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(saved ? WorkspaceCloseOutcome.SavedButCloseCancelled : WorkspaceCloseOutcome.Cancelled);
        }
    }
}
