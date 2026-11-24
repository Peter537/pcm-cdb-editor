using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.App.ViewModels;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Workspace;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsync();

    private async void SaveAs_Click(object sender, RoutedEventArgs e) =>
        await SaveWorkspaceAsync(chooseDestination: true, ownedOperationToken: null);

    private Task<bool> SaveCurrentAsync(CancellationToken? ownedOperationToken = null) =>
        SaveWorkspaceAsync(chooseDestination: false, ownedOperationToken);

    private async Task<bool> SaveWorkspaceAsync(bool chooseDestination, CancellationToken? ownedOperationToken)
    {
        if (_session is not { } session || ViewModel.IsBusy)
        {
            PresentWarning("Save is unavailable", "No ready working session is open.");
            return false;
        }

        if (TableGrid.IsEditing)
        {
            PresentWarning("Finish the cell edit", "Press Enter to commit the value or Escape to cancel it, then save.");
            return false;
        }

        bool ownsOperation = !ownedOperationToken.HasValue;
        CancellationToken cancellationToken;
        if (ownsOperation)
        {
            if (!TryBeginOperation(chooseDestination ? "Save as" : "Save", out cancellationToken))
            {
                return false;
            }
        }
        else
        {
            if (_operationLease is null)
            {
                throw new InvalidOperationException("A nested save requires the caller's exclusive operation lease.");
            }

            cancellationToken = ownedOperationToken!.Value;
        }

        string destination = session.SaveTargetCdbPath;
        SaveDestinationSnapshot? confirmation = null;
        bool saveAs = false;
        bool retireHistory = session.MutationPending || session.HistoryUncertain != false ||
            _editHistory?.State is { HistoryUncertain: true } or { HasPendingReplay: true };
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chooseDestination)
                {
                    string? selected = await PickSaveDestinationAsync(destination);
                    if (selected is null)
                    {
                        RestoreSessionState();
                        PresentWarning("Save cancelled", "The working copy is still open. No destination was replaced.");
                        return false;
                    }

                    destination = Path.GetFullPath(selected);
                    saveAs = !destination.Equals(session.SaveTargetCdbPath, StringComparison.OrdinalIgnoreCase);
                    confirmation = saveAs
                        ? await _workspaceService.InspectSaveDestinationAsync(destination, cancellationToken)
                        : null;
                    chooseDestination = false;
                }

                ViewModel.State = ShellOperationState.Saving;
                ViewModel.Status = saveAs ? "Saving a new CDB…" : "Saving database…";
                BusyIndicator.Visibility = Visibility.Visible;
                CancelOperationButton.Visibility = Visibility.Visible;
                CancelOperationButton.IsEnabled = true;
                PresentInformation(saveAs ? "Saving database as" : "Saving database",
                    "Converting to a staged CDB before the destination is replaced.");
                WorkspaceSaveResult result;
                try
                {
                    result = saveAs
                        ? await _workspaceService.SaveAsAsync(session, destination, cancellationToken, confirmation)
                        : await _workspaceService.SaveAsync(session, cancellationToken, confirmation);
                }
                catch (SaveDestinationConflictException conflict)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ViewModel.Status = "Review the save destination.";
                    ContentDialogResult decision = await ConfirmSaveDestinationConflictAsync(conflict);
                    if (decision == ContentDialogResult.None)
                    {
                        RestoreSessionState();
                        PresentWarning("Save cancelled", "The working copy is still open. The destination was left untouched.");
                        return false;
                    }

                    chooseDestination = decision == ContentDialogResult.Primary;
                    confirmation = chooseDestination ? null : conflict.ObservedDestination;
                    continue;
                }

                _session = result.Session;
                bool historyBaselineSaved = await TryMarkHistorySavedBaselineAsync(retireHistory);
                if (saveAs)
                {
                    ViewModel.DatabaseName = Path.GetFileName(result.Session.SaveTargetCdbPath);
                    await RememberRecentFileAsync(result.Session.SaveTargetCdbPath, CancellationToken.None);
                }

                SetReadyState();
                string title = saveAs ? "Database saved as" : "Database saved";
                if (result.DestinationChangedAtCommit)
                {
                    string message = "The working copy was saved. The displaced version was preserved. Open Save details for its backup path.";
                    if (!historyBaselineSaved)
                    {
                        message += " Reopen the saved CDB before editing because the Undo baseline could not be updated.";
                    }
                    PresentWarning(
                        "Destination changed during replacement",
                        message);
                }
                else if (historyBaselineSaved)
                {
                    string message = result.BackupPath is null
                        ? "Saved to the destination. Open Save details for its full path."
                        : "The previous CDB was backed up. Open Save details for both paths.";
                    if (retireHistory)
                    {
                        message += " Undo and Redo now start from this save because the earlier history was incomplete.";
                    }
                    PresentSuccess(title, message);
                }
                else
                {
                    PresentWarning(
                        $"{title}. Reopen before editing",
                        "The destination was saved, but the app could not mark the Undo history as saved. Reopen the CDB before making more edits.");
                }

                return historyBaselineSaved;
            }
        }
        catch (WorkspaceSaveHandoffException exception)
        {
            _session = exception.Session;
            RestoreSessionState();
            string displaced = exception.Backup is { } backup
                ? $"Recorded backup path {backup}."
                : "The original was restored at its destination.";
            PresentWarning("Save handoff incomplete",
                $"Destination: {exception.Destination}\n{displaced}\nStaged CDB: {exception.Stage}");
            return false;
        }
        catch (WorkspaceSaveCommitException exception)
        {
            await AdoptCommittedSaveAfterMetadataFailureAsync(exception.CommittedSave, saveAs, retireHistory);
            return _session?.IsDirty == false;
        }
        catch (Exception exception) when (IsWorkspaceCancellation(exception, cancellationToken))
        {
            RestoreSessionState();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                PresentWarning("Save cancelled", "The destination was not replaced.");
            }

            return false;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception) || exception is COMException)
        {
            RestoreSessionState();
            PresentError(
                saveAs ? "Could not save the database as" : "Could not save the database",
                SafeFailureMessage(exception, "The destination was not replaced."));
            return false;
        }
        finally
        {
            BusyIndicator.Visibility = Visibility.Collapsed;
            if (ownsOperation)
            {
                EndOperation();
            }
        }
    }

    private async Task<string?> PickSaveDestinationAsync(string suggestedPath)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedPath),
            DefaultFileExtension = ".cdb",
        };
        picker.FileTypeChoices.Add("PCM database", [".cdb"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? selected = await picker.PickSaveFileAsync();
        return selected?.Path;
    }

    private async Task<ContentDialogResult> ConfirmSaveDestinationConflictAsync(SaveDestinationConflictException conflict)
    {
        string explanation = conflict.Reason == SaveDestinationConflictReason.BaselineUnknown
            ? "This recovered session has no verified record of the original destination contents."
            : conflict.ObservedDestination.Length is null
                ? "The destination is now missing."
                : "The destination changed outside the editor.";
        string overwriteLabel = conflict.ObservedDestination.Length is null ? "Write here" : "Overwrite";
        string overwriteDetails = conflict.ObservedDestination.Length > 0
            ? "Overwrite replaces the current file and keeps the displaced copy as a backup."
            : "Writing here saves the working copy at this path.";
        ContentDialog dialog = CreateDialog(
            "Review the save destination",
            string.Empty,
            "Save as",
            overwriteLabel,
            "Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Content = new TextBlock
        {
            Text = $"{explanation}\n\n{conflict.ObservedDestination.Path}\n\n" +
                $"Choose Save as for a different path. {overwriteDetails}",
            TextWrapping = TextWrapping.Wrap
        };
        return await dialog.ShowAsync();
    }

    private async void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.LastBackupPath is not { } backup ||
            Path.GetDirectoryName(backup) is not { } folderPath)
        {
            return;
        }

        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
            {
                PresentWarning("Could not open the backup folder", "Copy its path from Save details.");
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception) || exception is COMException)
        {
            PresentWarning("Could not open the backup folder", "Copy its path from Save details.");
        }
    }
}
