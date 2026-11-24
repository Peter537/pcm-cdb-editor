using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private AutoSuggestBox? _riderLookupFeedbackOwner;
    private RiderCreationPreview? _riderReceipt;
    private Guid? _riderReceiptOperationId;
    private bool _riderReceiptApplied;
    private bool _riderValidationMessage;

    private void UpdateCurrentAbilityPresentation()
    {
        bool useOverride = RiderCurrentAbilityOverrideToggle.IsChecked == true;
        RiderCurrentAbilityOverrideBox.IsEnabled = useOverride;
        ResetRiderCurrentAbilityButton.IsEnabled = useOverride;
        string calculation = _riderAbilityEditors.Count != 0 &&
            _riderAbilityEditors.All(editor => IsWholeAbilityValue(editor.Current.Value))
            ? $"Calculated from Current abilities · {RiderCurrentAbility.Calculate(_riderAbilityEditors.Select(editor => (int)editor.Current.Value)):N2}"
            : "Enter every Current value to calculate current ability.";
        RiderCurrentAbilitySummary.Text = useOverride
            ? calculation + " The override will be stored."
            : calculation;
    }

    private void RiderCurrentAbilityMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_riderCreationDraft is null)
        {
            return;
        }

        if (RiderCurrentAbilityOverrideToggle.IsChecked == true &&
            double.IsNaN(RiderCurrentAbilityOverrideBox.Value) &&
            _riderAbilityEditors.All(editor => IsWholeAbilityValue(editor.Current.Value)))
        {
            RiderCurrentAbilityOverrideBox.Value = RiderCurrentAbility.Calculate(
                _riderAbilityEditors.Select(editor => (int)editor.Current.Value));
        }
        UpdateCurrentAbilityPresentation();
        InvalidateRiderCreationPreview();
    }

    private void RiderCurrentAbilityOverride_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_riderCreationDraft is not null)
        {
            InvalidateRiderCreationPreview();
        }
    }

    private void ResetCurrentAbilityOverride()
    {
        RiderCurrentAbilityOverrideToggle.IsChecked = false;
        RiderCurrentAbilityOverrideBox.Value = double.NaN;
        UpdateCurrentAbilityPresentation();
    }

    private void ResetRiderCurrentAbility_Click(object sender, RoutedEventArgs e)
    {
        ResetCurrentAbilityOverride();
        RiderCurrentAbilityOverrideToggle.Focus(FocusState.Programmatic);
    }

    private void CopyRiderCurrentToLimits_Click(object sender, RoutedEventArgs e)
    {
        RiderAbilityEditor? invalid = _riderAbilityEditors.FirstOrDefault(editor =>
            !IsWholeAbilityValue(editor.Current.Value));
        if (invalid is not null)
        {
            PresentRiderValidation("Check Current abilities", $"Enter a whole number from 50 to 85 for {invalid.Definition.Label}.");
            invalid.Current.Focus(FocusState.Programmatic);
            return;
        }

        foreach (RiderAbilityEditor editor in _riderAbilityEditors)
        {
            editor.Limit.Value = editor.Current.Value;
        }
        ClearRiderValidationMessage();
    }

    private void ClearRiderLimits_Click(object sender, RoutedEventArgs e)
    {
        foreach (RiderAbilityEditor editor in _riderAbilityEditors)
        {
            editor.Limit.Value = double.NaN;
        }
        ClearRiderValidationMessage();
    }

    private void ClearRiderValidationMessage()
    {
        if (_riderValidationMessage)
        {
            OperationInfo.IsOpen = false;
            _riderValidationMessage = false;
        }
    }

    private void PresentRiderValidation(string title, string message)
    {
        PresentWarning(title, message);
        _riderValidationMessage = true;
    }

    private void RiderDraftText_Changed(object sender, TextChangedEventArgs e) => InvalidateRiderCreationPreview();
    private void RiderDraftNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => InvalidateRiderCreationPreview();
    private void RiderDraftDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e) => InvalidateRiderCreationPreview();
    private void RiderDraftRole_Changed(object sender, SelectionChangedEventArgs e) => InvalidateRiderCreationPreview();
    private void RiderBulkValue_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => ClearRiderValidationMessage();

    private Task SearchRiderLookupAsync(AutoSuggestBox sender, RiderLookupTarget? target, int limit) =>
        RunSessionReaderAsync(token => SearchRiderLookupCoreAsync(sender, target, limit, token),
            _lifetimeCancellation.Token);

    private async Task SearchRiderLookupCoreAsync(
        AutoSuggestBox sender, RiderLookupTarget? target, int limit, CancellationToken sessionToken)
    {
        if (target is null || _session is not { } session || _riderCreationSessionId != session.SessionId)
        {
            return;
        }

        CancelRiderLookup(sender);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        CancellationToken token = cancellation.Token;
        _riderLookupCancellations[sender] = cancellation;
        string search = sender.Text;
        sender.ItemsSource = null;
        _riderLookupFeedbackOwner = sender;
        PresentRiderLookupFeedback(sender, $"Searching {target.Label}…");
        try
        {
            await Task.Delay(SearchDelayMilliseconds, token);
            IReadOnlyList<RiderLookupOption> options = await _riderCreationService.SearchLookupAsync(
                session.WorkingSqlitePath, target, search, limit, token);
            if (OwnsSearch())
            {
                sender.ItemsSource = options;
                PresentRiderLookupFeedback(sender, options.Count == 0
                    ? $"No matches for {target.Label}. Try another search."
                    : options.Count == limit ? $"Showing up to {limit} matches. Refine the search to narrow the list." : string.Empty);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            if (OwnsSearch())
            {
                PresentRiderLookupFeedback(sender, $"Could not search {target.Label}. Try again.");
            }
        }
        finally
        {
            if (_riderLookupCancellations.TryGetValue(sender, out var current) && ReferenceEquals(current, cancellation))
            {
                _riderLookupCancellations.Remove(sender);
            }
            cancellation.Dispose();
        }

        bool OwnsSearch() => !token.IsCancellationRequested && _session?.SessionId == session.SessionId &&
            _riderLookupCancellations.TryGetValue(sender, out var current) && ReferenceEquals(current, cancellation);
    }

    private void PresentRiderLookupFeedback(AutoSuggestBox sender, string message)
    {
        if (ReferenceEquals(_riderLookupFeedbackOwner, sender))
        {
            RiderLookupFeedback.Text = message;
            RiderLookupFeedback.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ClearRiderLookupFeedback()
    {
        _riderLookupFeedbackOwner = null;
        RiderLookupFeedback.Text = string.Empty;
        RiderLookupFeedback.Visibility = Visibility.Collapsed;
    }

    private void CancelRiderLookup(AutoSuggestBox sender)
    {
        if (_riderLookupCancellations.Remove(sender, out var previous))
        {
            previous.Cancel();
        }
        if (ReferenceEquals(_riderLookupFeedbackOwner, sender))
        {
            ClearRiderLookupFeedback();
        }
    }

    private bool OwnsRiderReview(EditorSessionState session, long draftGeneration, long viewGeneration) =>
        _session?.SessionId == session.SessionId &&
        _riderCreationSessionId == session.SessionId &&
        _riderDraftGeneration == draftGeneration &&
        _riderViewGeneration == viewGeneration &&
        RiderCreationView.Visibility == Visibility.Visible;

    private void SetRiderReceipt(RiderCreationPreview? preview, Guid? operationId = null)
    {
        _riderReceipt = preview;
        _riderReceiptOperationId = operationId;
        _riderReceiptApplied = preview is not null;
        bool hasReceipt = preview is not null;
        RiderCreationReceipt.Visibility = hasReceipt ? Visibility.Visible : Visibility.Collapsed;
        RiderStepNavigation.Visibility = hasReceipt ? Visibility.Collapsed : Visibility.Visible;
        RiderCreationStepContentControl.Visibility = hasReceipt ? Visibility.Collapsed : Visibility.Visible;
        RiderCreationFooter.Visibility = hasReceipt ? Visibility.Collapsed : Visibility.Visible;
        UpdateRiderReceiptPresentation();
        if (hasReceipt)
        {
            _riderDraftBaseline = CaptureRiderDraftValues();
        }
    }

    private void UpdateRiderReceiptPresentation()
    {
        RiderReceiptHeading.Text = _riderReceiptApplied ? "Rider and contract created" : "Rider creation undone";
        RiderReceiptText.Text = _riderReceipt is not { } preview ? string.Empty :
            $"{preview.Input.FirstName} {preview.Input.LastName} · Rider {preview.NewCyclistId:N0} · Contract {preview.NewContractId:N0}\n" +
            (_riderReceiptApplied
                ? "Use View rider or View contract to inspect the working rows. Save CDB writes the working copy to its current destination."
                : "Undo removed the two rows from the working copy. Use Redo while the creation remains in history.");
        ViewCreatedRiderButton.IsEnabled = _riderReceiptApplied;
        ViewCreatedContractButton.IsEnabled = _riderReceiptApplied;
        SaveCreatedRiderButton.IsEnabled = _riderReceiptApplied;
    }

    private void UpdateRiderReceiptAfterReplay(EditOperation operation, bool isUndo)
    {
        if (_riderReceiptOperationId == operation.OperationId)
        {
            _riderReceiptApplied = !isUndo;
            UpdateRiderReceiptPresentation();
        }
    }

    private async void CreateAnotherRider_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && _riderReceipt is not null && _operationLease is null && !ViewModel.IsBusy)
        {
            await InitializeRiderCreationAsync(session, _lifetimeCancellation.Token, startAnother: true);
        }
    }

    private async void SaveCreatedRider_Click(object sender, RoutedEventArgs e)
    {
        if (_riderReceiptApplied && _operationLease is null && _session?.SessionId == _riderCreationSessionId)
        {
            await SaveCurrentAsync();
        }
    }

    private async void ViewCreatedRiderRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!_riderReceiptApplied || _riderReceipt is not { } receipt ||
            _session?.SessionId != _riderCreationSessionId || _operationLease is not null || ViewModel.IsBusy)
        {
            return;
        }

        bool viewRider = (sender as FrameworkElement)?.Tag as string == "rider";
        string table = viewRider ? "DYN_cyclist" : "DYN_contract_cyclist";
        string keyColumn = viewRider ? receipt.RiderIdentityColumn : receipt.ContractIdentityColumn;
        long id = viewRider ? receipt.NewCyclistId : receipt.NewContractId;
        var filter = new FilterCondition(keyColumn, FilterOperator.Equals, SqliteValue.Integer(id));
        var definition = new TableFilterDefinition([], [], string.Empty, filter);
        Navigation.SelectedItem = TablesNavigationItem;
        await RunReferenceTableLoadAsync(table, "Could not open the created row", async request =>
        {
            await OpenTableAsync(table, string.Empty, request.Token, filter,
                rowDisplay: TableRowDisplayChoice.FromPageSize(_preferences.PageSize), sorts: [],
                filterDefinition: definition, loadRequest: request, presentation: new TableOpenPresentation(display =>
                {
                    if (display.Rows.Count != 1 || display.Rows[0].Identity is not { } identity)
                    {
                        throw new InvalidDataException("The created row is no longer uniquely available.");
                    }
                    return (new GridSelection(identity, keyColumn, [identity])
                        { CurrentCellRow = identity }, new GridViewport(identity, 0));
                }));
            request.Lease.ThrowIfSuperseded(request.Token);
            SynchronizeTableSelectionToTab(TableTabs.SelectedItem as TabViewItem);
            TableGrid.FocusCurrentRow();
        });
    }
}
