using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MainWindowAccessibilityTests
{
    private static readonly IReadOnlyDictionary<string, string> CriticalAutomationIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Navigation"] = "NavigationShell",
            ["TablesNavigationItem"] = "NavigationTables",
            ["MaintenanceNavigationItem"] = "NavigationMaintenance",
            ["RecoveryNavigationItem"] = "NavigationRecovery",
            ["WorkspaceCommandBar"] = "WorkspaceCommandBar",
            ["OpenButton"] = "OpenDatabaseCommand",
            ["SaveButton"] = "SaveDatabaseCommand",
            ["SaveAsButton"] = "SaveDatabaseAsCommand",
            ["CancelOperationButton"] = "CancelOperationCommand",
            ["UndoButton"] = "UndoEditCommand",
            ["RedoButton"] = "RedoEditCommand",
            ["TableSearchBox"] = "TableCatalogSearch",
            ["TablesList"] = "TableCatalogList",
            ["TableTabs"] = "OpenTableTabs",
            ["CurrentTableSearchBox"] = "CurrentTableSearch",
            ["FiltersButton"] = "ConfigureTableFiltersCommand",
            ["SortButton"] = "ConfigureTableSortCommand",
            ["PageSizeBox"] = "TablePageSize",
            ["PreviousPageButton"] = "PreviousTablePageCommand",
            ["NextPageButton"] = "NextTablePageCommand",
            ["TableGrid"] = "CurrentTableGrid",
            ["TableLoadingState"] = "TableLoadingState",
            ["EmptyStateOpenButton"] = "EmptyStateOpenDatabaseCommand",
            ["BusyIndicator"] = "DatabaseOperationBusyState",
            ["CloseInspectorButton"] = "CloseRowInspectorCommand",
            ["EditRowButton"] = "EditSelectedRowCommand",
            ["DeleteRowButton"] = "DeleteSelectedRowCommand",
            ["InsertRowButton"] = "InsertTableRowCommand",
            ["OverflowEditRowButton"] = "OverflowEditSelectedRowCommand",
            ["OverflowDeleteRowButton"] = "OverflowDeleteSelectedRowCommand",
            ["OverflowInsertRowButton"] = "OverflowInsertTableRowCommand",
            ["PreviewRiderRecoveryButton"] = "PreviewRiderRecoveryCommand",
            ["PreviewJanuaryRepairButton"] = "PreviewJanuaryRepairCommand",
            ["PreviewCountryQuotasButton"] = "PreviewCountryQuotasCommand",
            ["CheckRecoveryButton"] = "CheckRecoverySessionsCommand",
        };

    [TestMethod]
    public void CriticalControlsExposeStableUniqueAutomationIdsAndActionNames()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach ((string elementName, string expectedAutomationId) in CriticalAutomationIds)
        {
            XElement? element = document
                .Descendants()
                .SingleOrDefault(candidate => (string?)candidate.Attribute(xaml + "Name") == elementName);

            Assert.IsNotNull(element, $"MainWindow.xaml must retain the named control '{elementName}'.");
            Assert.AreEqual(
                expectedAutomationId,
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                $"The automation ID for '{elementName}' is an acceptance-test contract.");
        }

        string[] automationIds = document
            .Descendants()
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

        CollectionAssert.AreEquivalent(
            automationIds,
            automationIds.Distinct(StringComparer.Ordinal).ToArray(),
            "Automation IDs must be unique within MainWindow.");

        XElement[] clickTargets = document
            .Descendants()
            .Where(static element => element.Attribute("Click") is not null)
            .ToArray();
        foreach (XElement clickTarget in clickTargets)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace((string?)clickTarget.Attribute("AutomationProperties.AutomationId")),
                $"Click target '{clickTarget.Attribute("Click")?.Value}' requires a stable automation ID.");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace((string?)clickTarget.Attribute("AutomationProperties.Name")),
                $"Click target '{clickTarget.Attribute("Click")?.Value}' requires an accessible name.");
        }
    }

    [TestMethod]
    public void MaintenancePreviewButtonsHaveDistinctActionSpecificNamesAndHelp()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] buttonNames =
        [
            "PreviewRiderRecoveryButton",
            "PreviewJanuaryRepairButton",
            "PreviewCountryQuotasButton",
        ];

        XElement[] buttons = buttonNames
            .Select(buttonName => document
                .Descendants()
                .Single(element => (string?)element.Attribute(xaml + "Name") == buttonName))
            .ToArray();
        string[] accessibleNames = buttons
            .Select(button => (string?)button.Attribute("AutomationProperties.Name") ?? string.Empty)
            .ToArray();

        Assert.IsTrue(
            accessibleNames.All(static name => name.StartsWith("Check and preview", StringComparison.Ordinal)),
            "Each maintenance accessible name must include the visible button label.");
        Assert.AreEqual(
            accessibleNames.Length,
            accessibleNames.Distinct(StringComparer.Ordinal).Count(),
            "Maintenance preview buttons require distinct accessible names.");
        Assert.IsTrue(
            buttons.All(static button =>
                !string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.HelpText"))),
            "Each maintenance preview button requires action-specific help text.");
    }

    [TestMethod]
    public void InspectorToggleAndDynamicEmptyStatesStayWiredToShellPresentation()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement inspectorToggle = document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "RowInspectorToggle");

        Assert.AreEqual(
            "RowInspectorToggle_Click",
            (string?)inspectorToggle.Attribute("Click"),
            "The inspector preference binding does not update the imperative column presentation by itself.");

        string source = File.ReadAllText(GetMainWindowCodePath());
        StringAssert.Contains(
            source,
            "AutomationProperties.SetName(NoTablesState, NoTablesTitle.Text);",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "AutomationProperties.SetName(EmptyState, title);",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void NarrowLayoutKeepsCrudActionsInTheCommandBarOverflow()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement commandBar = document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "WorkspaceCommandBar");
        XElement secondaryCommands = commandBar
            .Elements()
            .Single(element => element.Name.LocalName == "CommandBar.SecondaryCommands");

        (string OverflowName, string InspectorName, string Handler, string Label)[] actions =
        [
            ("OverflowInsertRowButton", "InsertRowButton", "InsertRow_Click", "Insert row"),
            ("OverflowEditRowButton", "EditRowButton", "EditRow_Click", "Edit row"),
            ("OverflowDeleteRowButton", "DeleteRowButton", "DeleteRow_Click", "Delete row"),
        ];

        foreach ((string overflowName, string inspectorName, string handler, string label) in actions)
        {
            XElement overflowAction = secondaryCommands
                .Elements()
                .Single(element => (string?)element.Attribute(xaml + "Name") == overflowName);
            XElement inspectorAction = document
                .Descendants()
                .Single(element => (string?)element.Attribute(xaml + "Name") == inspectorName);

            Assert.AreEqual(handler, (string?)overflowAction.Attribute("Click"));
            Assert.AreEqual(label, (string?)overflowAction.Attribute("Label"));
            Assert.AreEqual(label, (string?)overflowAction.Attribute("AutomationProperties.Name"));
            Assert.AreEqual(
                (string?)inspectorAction.Attribute("AutomationProperties.HelpText"),
                (string?)overflowAction.Attribute("AutomationProperties.HelpText"));
            Assert.AreEqual("False", (string?)overflowAction.Attribute("IsEnabled"));
            Assert.IsNull(
                overflowAction.Attribute("Visibility"),
                $"{overflowName} must remain available when the inspector is collapsed below 980 DIPs.");
        }

        string source = File.ReadAllText(GetMainWindowCodePath());
        StringAssert.Contains(source, "InsertRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
        StringAssert.Contains(source, "OverflowInsertRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
        StringAssert.Contains(source, "EditRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
        StringAssert.Contains(source, "OverflowEditRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
        StringAssert.Contains(source, "DeleteRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
        StringAssert.Contains(source, "OverflowDeleteRowButton.IsEnabled = isEnabled;", StringComparison.Ordinal);
    }

    private static string GetMainWindowXamlPath()
    {
        return GetMainWindowSourcePath("MainWindow.xaml");
    }

    private static string GetMainWindowCodePath()
    {
        return GetMainWindowSourcePath("MainWindow.xaml.cs");
    }

    private static string GetMainWindowSourcePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "PcmCdbEditor.App",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"{fileName} could not be resolved from the test output directory.");
    }
}
