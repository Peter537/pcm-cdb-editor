using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class MainWindow
{
    private int _recordedChangeRowIndex;
    private bool _refreshingRecordedChanges;
    private bool _historyReplayActive;
    private EditHistorySnapshot? _displayedHistory;
    private bool _displayedHistoryUncertain;

    private void RefreshSessionChanges()
    {
        if (ChangesView.Visibility != Visibility.Visible)
        {
            return;
        }

        EditHistorySnapshot? snapshot = _editHistory?.GetSnapshot();
        EditorSessionState? session = _session;
        if (session is null || snapshot is null)
        {
            ChangesBaselineText.Text = "Open a CDB to see recorded changes.";
            ChangesReplayText.Text = string.Empty;
            _displayedHistory = null;
            SetRecordedChanges([]);
            return;
        }

        bool pendingReplay = snapshot.State.HasPendingReplay;
        bool replayInProgress = pendingReplay && _historyReplayActive;
        bool isUncertain = session.HistoryUncertain != false || snapshot.State.HistoryUncertain ||
            ((session.MutationPending || pendingReplay) && !replayInProgress);
        ChangesBaselineText.Text = DescribeSavedBaseline(session, snapshot, isUncertain, replayInProgress);
        string statusPrefix = isUncertain ? "Recorded next" : "Next";
        ChangesReplayText.Text = replayInProgress
            ? "Undo or Redo is in progress."
            : pendingReplay ? "The pending operation could not be recorded. Save to establish a new baseline."
            : $"{statusPrefix} Undo · {DescribeHistoryAction(snapshot.NextUndo)}\n{statusPrefix} Redo · {DescribeHistoryAction(snapshot.NextRedo)}" +
                (snapshot.Applied.Count + snapshot.Undone.Count == 0 ? "\nNo operations are available to inspect." : string.Empty);
        if (!ReferenceEquals(snapshot, _displayedHistory) || isUncertain != _displayedHistoryUncertain)
        {
            _displayedHistory = snapshot;
            _displayedHistoryUncertain = isUncertain;
            SetRecordedChanges(snapshot.Applied.Select(operation => new RecordedChange(operation, IsApplied: true, isUncertain))
                .Concat(snapshot.Undone.Select(operation => new RecordedChange(operation, IsApplied: false, isUncertain)))
                .ToArray());
        }
    }

    private static string DescribeSavedBaseline(
        EditorSessionState session, EditHistorySnapshot snapshot, bool isUncertain, bool replayInProgress)
    {
        if (isUncertain)
        {
            return "Undo history is incomplete. The working copy has unsaved or unverified changes. The recorded order may differ from the working database.";
        }

        if (replayInProgress)
        {
            return "The saved baseline will be shown after the current replay finishes.";
        }

        if (!session.IsDirty)
        {
            return "The working copy matches the saved baseline.";
        }

        if (snapshot.SavedAppliedCount is not { } savedCount)
        {
            return "Unsaved changes. The saved baseline is outside the retained sequence.";
        }

        int distance = snapshot.Applied.Count - savedCount;
        return distance switch
        {
            > 0 => $"Unsaved changes · {distance:N0} {(distance == 1 ? "operation" : "operations")} ahead of the saved baseline.",
            < 0 => $"Unsaved changes · {-distance:N0} {(distance == -1 ? "operation" : "operations")} behind the saved baseline.",
            _ => "The working copy has unsaved changes."
        };
    }

    private static string DescribeHistoryAction(EditOperation? operation) =>
        operation is null ? "None" : EditHistoryInspection.Describe(operation);

    private void SetRecordedChanges(RecordedChange[] records)
    {
        var selected = RecordedChangesList.SelectedItem as RecordedChange;
        RecordedChange? retained = records.FirstOrDefault(entry => entry.Operation.OperationId == selected?.Operation.OperationId);
        _refreshingRecordedChanges = true;
        try
        {
            RecordedChangesList.ItemsSource = records;
            RecordedChangesList.SelectedItem = retained ?? records.FirstOrDefault();
        }
        finally
        {
            _refreshingRecordedChanges = false;
        }

        if (retained is null)
        {
            _recordedChangeRowIndex = 0;
        }

        RecordedChangesList.Visibility = records.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PresentRecordedChangeRow();
    }

    private void RecordedChanges_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingRecordedChanges)
        {
            _recordedChangeRowIndex = 0;
            PresentRecordedChangeRow();
        }
    }

    private void PreviousRecordedChangeRow_Click(object sender, RoutedEventArgs e)
    {
        _recordedChangeRowIndex--;
        PresentRecordedChangeRow();
    }

    private void NextRecordedChangeRow_Click(object sender, RoutedEventArgs e)
    {
        _recordedChangeRowIndex++;
        PresentRecordedChangeRow();
    }

    private void PresentRecordedChangeRow()
    {
        if (RecordedChangesList.SelectedItem is not RecordedChange selected)
        {
            RecordedChangeDetails.Visibility = Visibility.Collapsed;
            RecordedChangeValuesText.Text = string.Empty;
            return;
        }

        int count = EditHistoryInspection.GetRowCount(selected.Operation);
        RecordedChangeDetails.Visibility = Visibility.Visible;
        RecordedChangeRowNavigation.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0)
        {
            RecordedChangeRowText.Text = "No row details are available for this operation.";
            RecordedChangeValuesText.Text = string.Empty;
            return;
        }

        _recordedChangeRowIndex = Math.Clamp(_recordedChangeRowIndex, 0, count - 1);
        EditHistoryRow row = EditHistoryInspection.GetRow(selected.Operation, _recordedChangeRowIndex);
        string identity = row.Identity is null
            ? "Row identity unavailable"
            : string.Join(", ", row.Identity.Components.Select(component =>
                $"{component.ColumnName} = {DescribeIdentityValue(component.Value)}"));
        RecordedChangeRowText.Text = $"Row {_recordedChangeRowIndex + 1:N0} of {count:N0} · {row.TableName} · {identity}";
        PreviousRecordedChangeRowButton.IsEnabled = _recordedChangeRowIndex > 0;
        NextRecordedChangeRowButton.IsEnabled = _recordedChangeRowIndex + 1 < count;
        RecordedChangeValuesText.Text = DescribeRecordedFields(row);
    }

    private static string DescribeRecordedFields(EditHistoryRow row)
    {
        var text = new StringBuilder();
        foreach (string column in row.ChangedColumns)
        {
            SqliteValue? before = row.BeforeValues?.TryGetValue(column, out SqliteValue oldValue) == true ? oldValue : null;
            SqliteValue? after = row.AfterValues?.TryGetValue(column, out SqliteValue newValue) == true ? newValue : null;
            text.AppendLine(column);
            text.Append("Before · ").AppendLine(DescribeSide(row.BeforeValues, before));
            text.Append("After · ").AppendLine(DescribeSide(row.AfterValues, after));
            text.AppendLine();
        }

        return text.Length == 0 ? "No changed field values are available." : text.ToString().TrimEnd();

        static string DescribeSide(IReadOnlyDictionary<string, SqliteValue>? values, SqliteValue? value) =>
            value is { } present ? DescribeHistoryValue(present)
            : values is null ? "Row absent" : "Value unavailable";
    }

    private static string DescribeHistoryValue(SqliteValue value) => value.Kind switch
    {
        SqliteValueKind.Null or SqliteValueKind.Blob => FormatSqliteValue(value),
        SqliteValueKind.Text => $"Text · “{FormatSqliteValue(value)}”",
        _ => $"{value.Kind} · {FormatSqliteValue(value)}"
    };

    private static string DescribeIdentityValue(SqliteValue value) => value.Kind == SqliteValueKind.Blob
        ? $"{FormatSqliteValue(value)} · Key fingerprint {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.BlobBase64 ?? string.Empty)))[..12]}"
        : DescribeHistoryValue(value);

    private sealed record RecordedChange(EditOperation Operation, bool IsApplied, bool IsUncertain)
    {
        public string Label =>
            $"{Status} · {EditHistoryInspection.Describe(Operation)} · {Operation.CreatedAtUtc.ToLocalTime():g}";

        private string Status => IsUncertain
            ? IsApplied ? "Recorded as applied" : "Recorded as undone"
            : IsApplied ? "Applied" : "Undone";
    }
}
