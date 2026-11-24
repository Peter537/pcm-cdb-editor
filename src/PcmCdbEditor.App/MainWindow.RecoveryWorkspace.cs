using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private bool _recoveryLoading;

    private bool CanUseRecoveryCommands =>
        !_disposed && !ViewModel.IsBusy && _operationLease is null && _activeTableLoad is null && !_recoveryLoading;

    private RecoverableSession? SelectedRecoverySession =>
        RecoverySessionsList.SelectedItem is ListViewItem { Tag: RecoverableSession selected } &&
        selected.Session.SessionId != _session?.SessionId
            ? selected
            : null;

    private async void CheckRecovery_Click(object sender, RoutedEventArgs e) =>
        await RefreshRecoverySessionsAsync(_lifetimeCancellation.Token);

    private void RecoverySelection_Changed(object sender, SelectionChangedEventArgs e) =>
        UpdateRecoveryCommands();

    private void UpdateRecoveryCommands()
    {
        if (CleanCompletedSessionsButton is null)
        {
            return;
        }

        bool ready = CanUseRecoveryCommands;
        bool selected = ready && SelectedRecoverySession is not null;
        CheckRecoveryButton.IsEnabled = ready;
        ResumeRecoveryButton.IsEnabled = selected;
        DiscardRecoveryButton.IsEnabled = selected;
        CleanCompletedSessionsButton.IsEnabled = ready;
    }

    private async Task<bool> RefreshRecoverySessionsAsync(CancellationToken cancellationToken)
    {
        if (!CanUseRecoveryCommands)
        {
            return false;
        }

        Guid? selectedId = SelectedRecoverySession?.Session.SessionId;
        _recoveryLoading = true;
        RecoveryStatusText.Text = "Checking working sessions…";
        UpdateRecoveryCommands();
        try
        {
            IReadOnlyList<RecoverableSession> recoverable =
                await _workspaceService.GetRecoverableSessionsAsync(cancellationToken);
            ListViewItem[] items = recoverable
                .Where(item => item.Session.SessionId != _session?.SessionId)
                .Select(CreateRecoveryItem)
                .ToArray();
            RecoverySessionsList.ItemsSource = items;
            RecoverySessionsList.SelectedItem = items.FirstOrDefault(item =>
                item.Tag is RecoverableSession session && session.Session.SessionId == selectedId);
            string status = items.Length switch
            {
                0 => "No working sessions are available to resume.",
                1 => "One recoverable session is available.",
                _ => $"{items.Length:N0} recoverable sessions are available.",
            };
            RecoveryStatusText.Text = status + " Sessions in use or with unreadable files are left intact.";
            return items.Length != 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            RecoverySessionsList.ItemsSource = null;
            RecoveryStatusText.Text = SafeFailureMessage(exception, "Could not check recovery sessions. Refresh to try again.");
            return false;
        }
        finally
        {
            _recoveryLoading = false;
            if (!_disposed)
            {
                UpdateRecoveryCommands();
            }
        }
    }

    private static ListViewItem CreateRecoveryItem(RecoverableSession recoverable)
    {
        string label = DescribeRecoverySession(recoverable.Session);
        if (recoverable.RequiresJournalRecovery)
        {
            label += "\nSQLite journal recovery is required before editing.";
        }
        var item = new ListViewItem
        {
            Tag = recoverable,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(item, label);
        return item;
    }

    private static string DescribeRecoverySession(EditorSessionState session)
    {
        string updated = session.UpdatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture);
        string details = $"{session.SourceCdbPath}\nLast updated {updated} · Session {session.SessionId:N}";
        if (!session.SourceCdbPath.Equals(session.SaveTargetCdbPath, StringComparison.OrdinalIgnoreCase))
        {
            details += $"\nSave destination {session.SaveTargetCdbPath}";
        }

        return details;
    }

    private async void ResumeRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseRecoveryCommands || SelectedRecoverySession is not { Session: var selected } ||
            !TryBeginOperation("Resume session", out CancellationToken cancellationToken))
        {
            return;
        }

        EditorSessionState? recovered = null;
        try
        {
            bool? discard = await ConfirmSessionReplacementAsync(cancellationToken);
            if (discard is null)
            {
                return;
            }

            SetBusyPresentation(
                "Resuming working session",
                "Validating the isolated CDB and SQLite working files.");
            recovered = await _workspaceService.RecoverAsync(
                selected.SessionId, cancellationToken, expectedUpdatedAtUtc: selected.UpdatedAtUtc);
            string? remaining = await ReplaceSessionAsync(recovered, discard.Value, cancellationToken);
            if (ReferenceEquals(Navigation.SelectedItem, RecoveryNavigationItem))
            {
                Navigation.SelectedItem = TablesNavigationItem;
            }

            if (_editHistory?.State.RecoveredInterruptedReplay == true)
            {
                PresentWarning(
                    "Unsaved work resumed after an interrupted undo or redo",
                    "Before replaying the operation, the app will verify the stored rows against the recorded edit.");
            }
            else
            {
                PresentSuccess("Unsaved work resumed", "The recovered working copy is open for editing.");
            }
            if (_session?.PendingSaveAttempt is not null || _session?.DeferredSaveAttempts is { Count: > 0 })
            {
                PresentWarning("Unsaved work resumed with pending save cleanup",
                    "The working database is open. An earlier save left staging files that could not be removed yet.");
            }
            PresentRemainingSessionCleanup(remaining);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                RestoreAfterFailedOpen();
                PresentWarning("Resume cancelled", "The selected session can still be recovered. Any previously open session is still available.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            RestoreAfterFailedOpen();
            PresentError("Could not resume the session",
                SafeFailureMessage(exception, "Refresh to check whether the selected working session is still available."));
        }
        finally
        {
            if (recovered is not null && _session?.SessionId != recovered.SessionId)
            {
                _workspaceService.ReleaseSession(recovered.SessionId);
            }
            EndOperation();
            await RefreshRecoverySessionsAsync(_lifetimeCancellation.Token);
        }
    }

    private async void DiscardRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseRecoveryCommands || SelectedRecoverySession is not { Session: var selected } ||
            !TryBeginOperation("Discard session", out CancellationToken cancellationToken))
        {
            return;
        }

        try
        {
            if (await ConfirmRecoveryActionAsync(
                    "Discard selected working session?",
                    $"{DescribeRecoverySession(selected)}\n\nDiscard permanently removes this working copy and its unsaved changes.",
                    "Discard session",
                    "Keep session"))
            {
                WorkspaceCloseResult close = await _workspaceService.DiscardRecoverableSessionAsync(
                    selected.SessionId, selected.UpdatedAtUtc, cancellationToken);
                if (close.RemainingDirectory is { } remaining)
                {
                    PresentWarning("Session discarded with files left for cleanup", remaining);
                }
                else
                {
                    PresentSuccess("Working session discarded", "The selected working copy was removed.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown or the operation lease owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError("Could not discard the session",
                SafeFailureMessage(exception, "Refresh to check the working session's current status."));
        }
        finally
        {
            EndOperation();
            await RefreshRecoverySessionsAsync(_lifetimeCancellation.Token);
        }
    }

    private async void CleanCompletedSessions_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseRecoveryCommands || !TryBeginOperation("Clean completed sessions", out CancellationToken cancellationToken))
        {
            return;
        }

        try
        {
            if (await ConfirmRecoveryActionAsync(
                    "Clean completed sessions?",
                    "Remove leftover working files from completed, clean sessions. Recoverable sessions and CDB backups are kept.",
                    "Clean completed",
                    "Cancel"))
            {
                await _workspaceService.CleanCompletedSessionsAsync(cancellationToken);
                PresentSuccess("Completed-session check finished", "Recoverable sessions and CDB backups were kept.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown or the operation lease owns cancellation.
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError("Could not finish cleanup",
                SafeFailureMessage(exception, "Refresh to check which working sessions are available."));
        }
        finally
        {
            EndOperation();
            await RefreshRecoverySessionsAsync(_lifetimeCancellation.Token);
        }
    }

    private async Task<bool> ConfirmRecoveryActionAsync(string title, string message, string action, string cancel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void RecentDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || _operationLease is not null || _activeTableLoad is not null ||
            !TryBeginOperation("Recent databases", out CancellationToken cancellationToken))
        {
            return;
        }

        string? selectedPath = null;
        try
        {
            bool hasRecent = _preferences.RecentFiles.Count != 0;
            var list = new ListView
            {
                ItemsSource = _preferences.RecentFiles,
                ItemTemplate = (DataTemplate)WindowRoot.Resources["WrappedListTextTemplate"],
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 360,
            };
            AutomationProperties.SetName(list, "Recent database paths");
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "Recent databases",
                Content = hasRecent ? list : new TextBlock { Text = "No recent databases." },
                PrimaryButtonText = hasRecent ? "Open selected" : string.Empty,
                IsPrimaryButtonEnabled = false,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is string;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellationToken.IsCancellationRequested)
            {
                selectedPath = list.SelectedItem as string;
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            PresentError("Could not open recent databases",
                SafeFailureMessage(exception, "Use Open to choose a CDB."));
        }
        finally
        {
            EndOperation();
        }

        if (selectedPath is not null)
        {
            await OpenPathAsync(selectedPath);
        }
    }
}
