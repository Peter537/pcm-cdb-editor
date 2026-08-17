using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class CountryQuotaPreviewDialogSourceTests
{
    private static readonly string[] RequiredAutomationIds =
    [
        "CountryQuotaReviewDialog",
        "CountryQuotaReviewSummary",
        "CountryQuotaCalculationDetails",
        "CountryQuotaReviewScope",
        "CountryQuotaReviewSort",
        "ReverseCountryQuotaReviewSort",
        "CountryQuotaReviewStatus",
        "CountryQuotaReviewHeaders",
        "CountryQuotaReviewResults",
        "CountryQuotaReviewEmptyState",
    ];

    [TestMethod]
    public void DialogExposesResponsiveStructuredReviewAndStableAccessibilityContracts()
    {
        XDocument document = XDocument.Load(GetAppSourcePath("CountryQuotaPreviewDialog.xaml"));
        string[] automationIds = document.Root!
            .DescendantsAndSelf()
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

        CollectionAssert.AreEquivalent(RequiredAutomationIds, automationIds);
        Assert.AreEqual(
            automationIds.Length,
            automationIds.Distinct(StringComparer.Ordinal).Count(),
            "Country quota dialog automation IDs must remain unique.");

        string xaml = document.ToString(SaveOptions.DisableFormatting);
        string dialogMarkupAndCode = xaml +
            File.ReadAllText(GetAppSourcePath("CountryQuotaPreviewDialog.xaml.cs"));
        foreach (string requiredCopy in new[]
        {
            "Country code",
            "UCI points",
            "World Championship",
            "European Championship",
            "Road",
            "Time trial",
            "How rankings and quotas are calculated",
        })
        {
            StringAssert.Contains(dialogMarkupAndCode, requiredCopy, StringComparison.Ordinal);
        }

        StringAssert.Contains(xaml, "AdaptiveTrigger MinWindowWidth=\"640\"", StringComparison.Ordinal);
        StringAssert.Contains(xaml, "AdaptiveTrigger MinWindowWidth=\"1008\"", StringComparison.Ordinal);
        StringAssert.Contains(xaml, "SizeChanged=\"ReviewLayout_SizeChanged\"", StringComparison.Ordinal);
        Assert.IsFalse(xaml.Contains(
            "Setter Target=\"QuotaResults.ItemTemplate\"",
            StringComparison.Ordinal),
            "Visual-state resource setters do not reliably replace an instantiated WinUI item template.");
        StringAssert.Contains(xaml, "AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal);
        StringAssert.Contains(
            xaml,
            "ContainerContentChanging=\"QuotaResults_ContainerContentChanging\"",
            StringComparison.Ordinal);
        Assert.IsFalse(xaml.Contains(
            "Setter Property=\"AutomationProperties.Name\" Value=\"{Binding AccessibleSummary}\"",
            StringComparison.Ordinal),
            "A style binding does not reliably name a virtualized WinUI ListViewItem automation peer.");
        StringAssert.Contains(
            xaml,
            "Setter Property=\"AutomationProperties.AccessibilityView\" Value=\"Raw\"",
            StringComparison.Ordinal);
        StringAssert.Contains(xaml, "Expanding=\"CalculationExpander_Expanding\"", StringComparison.Ordinal);
        StringAssert.Contains(xaml, "Collapsed=\"CalculationExpander_Collapsed\"", StringComparison.Ordinal);
        Assert.IsFalse(xaml.Contains(
            "BorderThickness=\"0,0,0,1\" AutomationProperties.Name=\"{Binding AccessibleSummary}\"",
            StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains(">WR<", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains(">WTT<", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains(">ER<", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains(">ETT<", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DialogSwitchesRowsFromActualWidthAndQueuesInitialFocusAfterNativeSetup()
    {
        string xaml = File.ReadAllText(GetAppSourcePath("CountryQuotaPreviewDialog.xaml"));
        string code = File.ReadAllText(GetAppSourcePath("CountryQuotaPreviewDialog.xaml.cs"));

        StringAssert.Contains(xaml, "Opened=\"Dialog_Opened\"", StringComparison.Ordinal);
        StringAssert.Contains(xaml, "Closed=\"Dialog_Closed\"", StringComparison.Ordinal);
        StringAssert.Contains(code, "WideReviewLayoutMinimumWidth = 640", StringComparison.Ordinal);
        StringAssert.Contains(code, "availableWidth >= WideReviewLayoutMinimumWidth", StringComparison.Ordinal);
        StringAssert.Contains(code, "QuotaHeader.Visibility = useWideLayout", StringComparison.Ordinal);
        StringAssert.Contains(
            code,
            "QuotaResults.ItemTemplate = (DataTemplate)Resources[templateKey]",
            StringComparison.Ordinal);
        StringAssert.Contains(code, "DispatcherQueuePriority.Low", StringComparison.Ordinal);
        StringAssert.Contains(
            code,
            "focusRequestGeneration == _focusRequestGeneration && IsLoaded",
            StringComparison.Ordinal);
        StringAssert.Contains(code, "ScopeSelector.Focus(FocusState.Programmatic)", StringComparison.Ordinal);
        StringAssert.Contains(code, "private void Dialog_Closed", StringComparison.Ordinal);
        StringAssert.Contains(code, "args.InRecycleQueue", StringComparison.Ordinal);
        StringAssert.Contains(
            code,
            "AutomationProperties.SetName(args.ItemContainer, string.Empty)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            code,
            "AutomationProperties.SetName(args.ItemContainer, row.AccessibleSummary)",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void DialogKeepsApplyAllBoundToTheOriginalPreview()
    {
        string mainWindow = File.ReadAllText(GetAppSourcePath("MainWindow.xaml.cs"));
        int handlerStart = mainWindow.IndexOf(
            "private async void PreviewCountryQuotas_Click",
            StringComparison.Ordinal);
        int handlerEnd = mainWindow.IndexOf(
            "private async Task RunMaintenanceAsync",
            handlerStart,
            StringComparison.Ordinal);
        Assert.IsTrue(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = mainWindow[handlerStart..handlerEnd];

        int dialogCreation = handler.IndexOf("new CountryQuotaPreviewDialog(preview)", StringComparison.Ordinal);
        int decision = handler.IndexOf(
            "await dialog.ShowAsync() != ContentDialogResult.Primary",
            StringComparison.Ordinal);
        int writeAhead = handler.IndexOf("PrepareMutationWriteAheadAsync(session)", StringComparison.Ordinal);
        int apply = handler.IndexOf("dialog.SourcePreview", StringComparison.Ordinal);

        Assert.IsTrue(dialogCreation >= 0);
        Assert.IsTrue(decision > dialogCreation);
        Assert.IsTrue(writeAhead > decision, "Cancel must return before mutation write-ahead begins.");
        Assert.IsTrue(apply > writeAhead, "Apply must receive the original preview after confirmation.");
        StringAssert.Contains(handler, "if (dialog.ChangeCount > 0)", StringComparison.Ordinal);
        Assert.IsFalse(handler.Contains("quotaChanges.Select", StringComparison.Ordinal));

        string dialog = File.ReadAllText(GetAppSourcePath("CountryQuotaPreviewDialog.xaml.cs"));
        StringAssert.Contains(dialog, "internal CountryQuotaPreview SourcePreview => _projection.Source;", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "PrimaryButtonText = string.Empty;", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "IsPrimaryButtonEnabled = false;", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "Apply all {_projection.ChangeCount:N0} changes", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "ApplyAllCountryQuotaChangesCommand", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "CancelCountryQuotaReviewCommand", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "ReviewBody.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);
        StringAssert.Contains(dialog, "ReviewBody.Visibility = Visibility.Visible;", StringComparison.Ordinal);
    }

    [TestMethod]
    public void PortablePublishAndVerifierRequireTheDialogResource()
    {
        string project = File.ReadAllText(GetAppSourcePath("PcmCdbEditor.App.csproj"));
        StringAssert.Contains(project, "CountryQuotaPreviewDialog.xbf", StringComparison.Ordinal);

        string verifier = File.ReadAllText(GetRepositoryPath("eng", "Verify-Release.ps1"));
        StringAssert.Contains(verifier, "'CountryQuotaPreviewDialog.xbf'", StringComparison.Ordinal);
    }

    private static string GetAppSourcePath(string fileName) =>
        GetRepositoryPath("src", "PcmCdbEditor.App", fileName);

    private static string GetRepositoryPath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = segments.Aggregate(
                directory.FullName,
                static (current, segment) => Path.Combine(current, segment));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"{string.Join('/', segments)} could not be resolved from the test output directory.");
    }
}
