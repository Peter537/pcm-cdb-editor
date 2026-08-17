using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;

namespace PcmCdbEditor.App;

public sealed partial class CountryQuotaPreviewDialog : ContentDialog
{
    private const double WideReviewLayoutMinimumWidth = 640;

    private readonly CountryQuotaReviewProjection _projection;
    private readonly Dictionary<CountryQuotaReviewScope, CountryQuotaReviewSort> _sorts;
    private bool _updatingControls;
    private bool? _isWideLayout;
    private int _focusRequestGeneration;
    private CountryQuotaReviewScope _scope = CountryQuotaReviewScope.Changes;

    internal CountryQuotaPreviewDialog(CountryQuotaPreview preview)
    {
        _projection = CountryQuotaReviewProjection.Create(preview);
        _sorts = Enum.GetValues<CountryQuotaReviewScope>()
            .ToDictionary(static scope => scope, CountryQuotaReviewProjection.GetDefaultSort);

        InitializeComponent();
        ConfigureSummary();
        ConfigureSelectors();
        ConfigureActions();
        ApplyView();
    }

    internal CountryQuotaPreview SourcePreview => _projection.Source;

    internal int ChangeCount => _projection.ChangeCount;

    private CountryQuotaReviewSort CurrentSort => _sorts[_scope];

    private void ConfigureSummary()
    {
        SummaryText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "Database date {0:yyyy-MM-dd}. {1:N0} of {2:N0} country rows will change. " +
            "The calculation contains {3:N0} World qualifiers and {4:N0} European qualifiers.",
            _projection.CurrentDate,
            _projection.ChangeCount,
            _projection.TotalCountryCount,
            _projection.WorldQualifierCount,
            _projection.EuropeanQualifierCount);
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
    }

    private void ConfigureSelectors()
    {
        ScopeSelector.ItemsSource = new[]
        {
            new ScopeOption(
                CountryQuotaReviewScope.Changes,
                $"Changes ({_projection.ChangeCount:N0})"),
            new ScopeOption(
                CountryQuotaReviewScope.WorldQualifiers,
                $"World qualifiers ({_projection.WorldQualifierCount:N0})"),
            new ScopeOption(
                CountryQuotaReviewScope.EuropeanQualifiers,
                $"European qualifiers ({_projection.EuropeanQualifierCount:N0})")
        };
        SortSelector.ItemsSource = new[]
        {
            new SortOption(CountryQuotaReviewSortField.CountryCode, "Country code"),
            new SortOption(CountryQuotaReviewSortField.UciPoints, "UCI points"),
            new SortOption(CountryQuotaReviewSortField.WorldRank, "World rank"),
            new SortOption(CountryQuotaReviewSortField.EuropeanRank, "European rank")
        };

        _updatingControls = true;
        ScopeSelector.SelectedIndex = 0;
        SelectCurrentSortOption();
        _updatingControls = false;
    }

    private void ConfigureActions()
    {
        if (_projection.ChangeCount == 0)
        {
            PrimaryButtonText = string.Empty;
            IsPrimaryButtonEnabled = false;
            CloseButtonText = "Close";
            ApplyScopeText.Text =
                "Country quotas are already up to date. You can still review the calculated qualifier lists.";
            return;
        }

        PrimaryButtonText = $"Apply all {_projection.ChangeCount:N0} changes";
        IsPrimaryButtonEnabled = true;
        CloseButtonText = "Cancel";
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ConfigureActionAutomation();
        ApplyResponsiveLayout(ReviewLayout.ActualWidth);

        int focusRequestGeneration = ++_focusRequestGeneration;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (focusRequestGeneration == _focusRequestGeneration && IsLoaded)
                {
                    ScopeSelector.Focus(FocusState.Programmatic);
                }
            });
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _focusRequestGeneration++;
    }

    private void ReviewLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void QuotaResults_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, QuotaResults))
        {
            return;
        }

        if (args.InRecycleQueue || args.Item is not CountryQuotaReviewDisplayRow row)
        {
            AutomationProperties.SetName(args.ItemContainer, string.Empty);
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, row.AccessibleSummary);
    }

    private void ApplyResponsiveLayout(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        bool useWideLayout = availableWidth >= WideReviewLayoutMinimumWidth;
        if (_isWideLayout == useWideLayout)
        {
            return;
        }

        _isWideLayout = useWideLayout;
        QuotaHeader.Visibility = useWideLayout ? Visibility.Visible : Visibility.Collapsed;
        string templateKey = useWideLayout ? "WideQuotaRowTemplate" : "CompactQuotaRowTemplate";
        QuotaResults.ItemTemplate = (DataTemplate)Resources[templateKey];
    }

    private void CalculationExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        ReviewBody.Visibility = Visibility.Collapsed;
    }

    private void CalculationExpander_Collapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        ReviewBody.Visibility = Visibility.Visible;
    }

    private void ConfigureActionAutomation()
    {
        if (GetTemplateChild("PrimaryButton") is Button primaryButton)
        {
            AutomationProperties.SetAutomationId(
                primaryButton,
                "ApplyAllCountryQuotaChangesCommand");
            AutomationProperties.SetName(primaryButton, PrimaryButtonText);
            AutomationProperties.SetHelpText(
                primaryButton,
                "Apply every calculated country quota change, including rows outside the current view.");
        }

        if (GetTemplateChild("CloseButton") is Button closeButton)
        {
            AutomationProperties.SetAutomationId(
                closeButton,
                "CancelCountryQuotaReviewCommand");
            AutomationProperties.SetName(closeButton, CloseButtonText);
            AutomationProperties.SetHelpText(
                closeButton,
                "Close the review without changing country quotas.");
        }
    }

    private void ScopeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || ScopeSelector.SelectedItem is not ScopeOption option)
        {
            return;
        }

        _scope = option.Scope;
        _updatingControls = true;
        SelectCurrentSortOption();
        _updatingControls = false;
        ApplyView();
    }

    private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || SortSelector.SelectedItem is not SortOption option)
        {
            return;
        }

        CountryQuotaReviewSort current = CurrentSort;
        _sorts[_scope] = current with { Field = option.Field };
        ApplyView();
    }

    private void ReverseSortButton_Click(object sender, RoutedEventArgs e)
    {
        CountryQuotaReviewSort current = CurrentSort;
        _sorts[_scope] = current with
        {
            Direction = current.Direction == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending
        };
        ApplyView();
    }

    private void SelectCurrentSortOption()
    {
        CountryQuotaReviewSortField field = CurrentSort.Field;
        SortSelector.SelectedItem = SortSelector.Items
            .OfType<SortOption>()
            .Single(option => option.Field == field);
    }

    private void ApplyView()
    {
        CountryQuotaReviewDisplayRow[] rows = _projection
            .GetRows(_scope, CurrentSort)
            .Select(static row => new CountryQuotaReviewDisplayRow(row))
            .ToArray();
        QuotaResults.ItemsSource = rows;
        QuotaResults.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        string scopeName = _scope switch
        {
            CountryQuotaReviewScope.Changes => "changes",
            CountryQuotaReviewScope.WorldQualifiers => "World qualifiers",
            CountryQuotaReviewScope.EuropeanQualifiers => "European qualifiers",
            _ => throw new ArgumentOutOfRangeException()
        };
        string sortDescription = DescribeSort(CurrentSort);
        ResultsStatus.Text = string.Format(
            CultureInfo.CurrentCulture,
            "Showing {0:N0} {1}, sorted by {2}.",
            rows.Length,
            scopeName,
            sortDescription);
        AutomationProperties.SetName(
            QuotaResults,
            $"Country quota review results. {ResultsStatus.Text}");
        AutomationProperties.SetItemStatus(QuotaResults, ResultsStatus.Text);
        AutomationProperties.SetItemStatus(SortSelector, sortDescription);
        AutomationProperties.SetHelpText(
            SortSelector,
            $"Currently sorted by {sortDescription}. Choose another field to change the visible order.");

        ReverseSortButton.Content = CurrentSort.Direction == SortDirection.Ascending ? "↑" : "↓";
        AutomationProperties.SetName(
            ReverseSortButton,
            $"Reverse country quota sort order. Currently {sortDescription}.");

        if (rows.Length == 0)
        {
            EmptyStateTitle.Text = _scope == CountryQuotaReviewScope.Changes
                ? "Country quotas are already up to date"
                : "No countries in this qualifier list";
            EmptyStateMessage.Text = _scope == CountryQuotaReviewScope.Changes
                ? "Choose a qualifier view to inspect the calculated championship places."
                : "The current calculation produced no qualifiers for this championship.";
            AutomationProperties.SetName(
                EmptyState,
                $"{EmptyStateTitle.Text}. {EmptyStateMessage.Text}");
        }
    }

    private static string DescribeSort(CountryQuotaReviewSort sort)
    {
        return sort.Field switch
        {
            CountryQuotaReviewSortField.CountryCode => sort.Direction == SortDirection.Ascending
                ? "country code, A to Z"
                : "country code, Z to A",
            CountryQuotaReviewSortField.UciPoints => sort.Direction == SortDirection.Ascending
                ? "UCI points, lowest first"
                : "UCI points, highest first",
            CountryQuotaReviewSortField.WorldRank => sort.Direction == SortDirection.Ascending
                ? "World rank, best first"
                : "World rank, worst first",
            CountryQuotaReviewSortField.EuropeanRank => sort.Direction == SortDirection.Ascending
                ? "European rank, best first"
                : "European rank, worst first",
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
    }

    private sealed record ScopeOption(CountryQuotaReviewScope Scope, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SortOption(CountryQuotaReviewSortField Field, string Label)
    {
        public override string ToString() => Label;
    }
}

internal sealed class CountryQuotaReviewDisplayRow
{
    public CountryQuotaReviewDisplayRow(CountryQuotaReviewRow row)
    {
        CanonicalCode = row.CanonicalCode;
        StoredCodeLabel = row.StoredCodeLabel ?? string.Empty;
        StoredCodeVisibility = row.HasStoredCodeAlias ? Visibility.Visible : Visibility.Collapsed;
        UciPointsText = row.UciPoints.ToString("N2", CultureInfo.CurrentCulture);
        WorldRankText = FormatRank(row.WorldRank);
        EuropeanRankText = FormatRank(row.EuropeanRank);
        WorldRoadText = $"Road: {row.WorldRoadQuota.DisplayText}";
        WorldTimeTrialText = $"Time trial: {row.WorldTimeTrialQuota.DisplayText}";
        EuropeanRoadText = $"Road: {row.EuropeanRoadQuota.DisplayText}";
        EuropeanTimeTrialText = $"Time trial: {row.EuropeanTimeTrialQuota.DisplayText}";
        AccessibleSummary = row.AccessibleSummary;
    }

    public string CanonicalCode { get; }

    public string StoredCodeLabel { get; }

    public Visibility StoredCodeVisibility { get; }

    public string UciPointsText { get; }

    public string WorldRankText { get; }

    public string EuropeanRankText { get; }

    public string WorldRoadText { get; }

    public string WorldTimeTrialText { get; }

    public string EuropeanRoadText { get; }

    public string EuropeanTimeTrialText { get; }

    public string AccessibleSummary { get; }

    private static string FormatRank(int? rank) => rank.HasValue
        ? $"Rank {rank.Value:N0}"
        : "Not ranked";
}
