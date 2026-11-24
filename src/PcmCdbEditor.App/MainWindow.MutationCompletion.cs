using PcmCdbEditor.App.ViewModels;
using PcmCdbEditor.Infrastructure.History;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private enum MutationOutcome
    {
        NotStarted,
        CompletionUncertain,
        NoChange,
        CommittedWithIncompleteHistory,
        CommittedWithDurableHistory
    }

    private MutationOutcome _mutationOutcome;

    private bool HasCommittedMutation => _mutationOutcome is
        MutationOutcome.CommittedWithIncompleteHistory or MutationOutcome.CommittedWithDurableHistory;

    private EditHistory RequireEditHistory() => _editHistory
        ?? throw new InvalidOperationException("The active session has no edit history.");

    private async Task CompleteCommittedMutationAsync(Action persistHistory)
    {
        if (_operationLease is null || !HasCommittedMutation || _session is not { } prepared)
        {
            throw new InvalidOperationException("Completing a committed mutation requires its active working session.");
        }

        ViewModel.State = ShellOperationState.Dirty;
        InvalidateTableCaches();
        _session = await _mutationWriteAhead.CompleteCommittedAsync(prepared, persistHistory);
        _mutationOutcome = MutationOutcome.CommittedWithDurableHistory;
        UpdateHistoryButtons();
    }

    private async Task<bool> RefreshAfterMutationAsync(string completedChange)
    {
        try
        {
            await RefreshSelectedTableAsync();
            return true;
        }
        catch (OperationCanceledException) when (
            _operationLease?.Token.IsCancellationRequested == true || _lifetimeCancellation.IsCancellationRequested)
        {
            PresentCommittedRefreshCancellation(completedChange);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentTableQueryWarning("Could not refresh the table", SafeFailureMessage(exception,
                $"{completedChange} The displayed rows may be stale. Use Refresh to reload them."));
        }
        return false;
    }
}
