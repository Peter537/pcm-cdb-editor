using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PcmCdbEditor.App.ViewModels;

public enum ShellOperationState
{
    NoFile,
    Loading,
    Ready,
    Dirty,
    Saving,
    Preview,
    Recovery,
    Failed,
}

public sealed class ShellState : INotifyPropertyChanged
{
    private ShellOperationState _state = ShellOperationState.NoFile;
    private string _databaseName = "No database open";
    private string _status = "Open a CDB file to begin.";
    private string _tableSummary = "No table selected";
    private string _pageSizeLabel = "100 rows/page";
    private bool _isInspectorOpen = true;
    private bool _hasDatabase;
    private bool _isOperationExclusive;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TableTabState> Tabs { get; } = [];

    public ObservableCollection<TableListItem> Tables { get; } = [];

    public ShellOperationState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsDatabaseOpen));
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string DatabaseName
    {
        get => _databaseName;
        set => Set(ref _databaseName, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string TableSummary
    {
        get => _tableSummary;
        set => Set(ref _tableSummary, value);
    }

    public string PageSizeLabel
    {
        get => _pageSizeLabel;
        set => Set(ref _pageSizeLabel, value);
    }

    public bool HasDatabase
    {
        get => _hasDatabase;
        set
        {
            if (Set(ref _hasDatabase, value))
            {
                OnPropertyChanged(nameof(IsDatabaseOpen));
            }
        }
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set => Set(ref _isInspectorOpen, value);
    }

    public bool IsOperationExclusive
    {
        get => _isOperationExclusive;
        set
        {
            if (Set(ref _isOperationExclusive, value))
            {
                OnPropertyChanged(nameof(IsDatabaseOpen));
            }
        }
    }

    public bool IsBusy => State is ShellOperationState.Loading
        or ShellOperationState.Saving
        or ShellOperationState.Preview;

    public bool IsDatabaseOpen => HasDatabase && !IsBusy && !IsOperationExclusive;

    public bool IsDirty => State == ShellOperationState.Dirty;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TableTabState(string Name, string CountLabel, bool IsReadOnly);

public sealed record TableListItem(string Name, string KindLabel, bool IsReadOnly);

public sealed record InspectorValueItem(string ColumnName, string DisplayValue);
