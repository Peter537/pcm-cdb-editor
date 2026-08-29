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
            ["CreateRiderNavigationItem"] = "NavigationCreateRider",
            ["RecoveryNavigationItem"] = "NavigationRecovery",
            ["WorkspaceCommandBar"] = "WorkspaceCommandBar",
            ["WorkingCopyStatusText"] = "WorkingCopyReadyStatus",
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
            ["RecoveryIdsModeRadioButton"] = "RiderRecoveryIdsMode",
            ["RecoveryTeamModeRadioButton"] = "RiderRecoveryTeamMode",
            ["RiderIdsTextBox"] = "RiderRecoveryIds",
            ["UseSelectedRiderRowsButton"] = "UseSelectedRiderRowsCommand",
            ["RiderRecoveryTeamComboBox"] = "RiderRecoveryTeam",
            ["RiderRecoveryTeamStatusText"] = "RiderRecoveryTeamStatus",
            ["RiderCreationCapabilityInfo"] = "RiderCreationCapabilityStatus",
            ["RiderFirstNameTextBox"] = "RiderCreationFirstName",
            ["RiderLastNameTextBox"] = "RiderCreationLastName",
            ["RiderTeamSuggestBox"] = "RiderCreationTeam",
            ["RiderRegionSuggestBox"] = "RiderCreationRegion",
            ["RiderTypeSuggestBox"] = "RiderCreationType",
            ["RiderFavoriteRaceSuggestBox"] = "RiderCreationFavoriteRaceSearch",
            ["AddFavoriteRaceButton"] = "AddRiderFavoriteRaceCommand",
            ["RiderFavoriteRacesList"] = "RiderCreationFavoriteRaces",
            ["RiderFavoriteRaceStatusText"] = "RiderCreationFavoriteRaceStatus",
            ["RiderPotentialNumberBox"] = "RiderCreationPotential",
            ["RiderBulkCurrentNumberBox"] = "RiderCreationBulkCurrent",
            ["RiderBulkLimitNumberBox"] = "RiderCreationBulkLimit",
            ["RiderGameDisplayNameTextBox"] = "RiderCreationGameDisplayName",
            ["ResetRiderGameDisplayNameButton"] = "ResetRiderGameDisplayNameCommand",
            ["RiderReviewFavoriteRacesText"] = "RiderCreationReviewFavoriteRaces",
            ["RiderRoleComboBox"] = "RiderCreationRole",
            ["RiderCreationBackButton"] = "RiderCreationBackCommand",
            ["RiderCreationNextButton"] = "RiderCreationNextCommand",
            ["CreateRiderButton"] = "CreateRiderCommand",
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
    public void SortHelpDistinguishesTheHeaderShortcutFromTheFullEditor()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement sortButton = document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SortButton");
        string helpText = (string?)sortButton.Attribute("AutomationProperties.HelpText") ?? string.Empty;

        StringAssert.Contains(helpText, "Edit or clear the ordered multi-column sort.", StringComparison.Ordinal);
        StringAssert.Contains(helpText, "make it priority 1", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateRiderIsASixStepDestinationWithoutTemplateControls()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement navigation = document.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "CreateRiderNavigationItem");
        Assert.AreEqual("create-rider", (string?)navigation.Attribute("Tag"));

        string[] stepIds =
        [
            "RiderCreationStepIdentity",
            "RiderCreationStepProfile",
            "RiderCreationStepAbilities",
            "RiderCreationStepContract",
            "RiderCreationStepAdvanced",
            "RiderCreationStepReview"
        ];
        foreach (string stepId in stepIds)
        {
            Assert.IsNotNull(document.Descendants().SingleOrDefault(element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == stepId));
        }

        string xamlSource = File.ReadAllText(GetMainWindowXamlPath());
        Assert.IsFalse(xamlSource.Contains(string.Concat("Rider", "Template"), StringComparison.Ordinal));
        Assert.IsFalse(xamlSource.Contains(
            string.Concat("Create rider from ", "template"),
            StringComparison.OrdinalIgnoreCase));

        string codeSource = File.ReadAllText(GetMainWindowCodePath());
        StringAssert.Contains(codeSource, "CreateRiderRoleOptions", StringComparison.Ordinal);
        StringAssert.Contains(codeSource, "Absolute leader", StringComparison.Ordinal);
        StringAssert.Contains(codeSource, "Luxury teammate", StringComparison.Ordinal);
        StringAssert.Contains(codeSource, "RiderAbilityCurrent_", StringComparison.Ordinal);
        StringAssert.Contains(codeSource, "RiderAbilityLimit_", StringComparison.Ordinal);
        StringAssert.Contains(codeSource, "RiderCreationCommandAvailability.CanCreate", StringComparison.Ordinal);
        int endOperationStart = codeSource.IndexOf("private void EndOperation()", StringComparison.Ordinal);
        int nextMethod = codeSource.IndexOf("private void SetConflictingCommandsEnabled", endOperationStart, StringComparison.Ordinal);
        StringAssert.Contains(
            codeSource[endOperationStart..nextMethod],
            "UpdateCreateRiderButtonState();",
            StringComparison.Ordinal);
        StringAssert.Contains(xamlSource, "Favorite races (optional)", StringComparison.Ordinal);
        StringAssert.Contains(xamlSource, "Reset to generated", StringComparison.Ordinal);
        StringAssert.Contains(xamlSource, "value_f_potentiel", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateRiderDraftIsSessionScopedAndNavigationOnlyUnlocksVisitedSteps()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] stepNames =
        [
            "RiderStepIdentityButton",
            "RiderStepProfileButton",
            "RiderStepAbilitiesButton",
            "RiderStepContractButton",
            "RiderStepAdvancedButton",
            "RiderStepReviewButton"
        ];
        for (var index = 0; index < stepNames.Length; index++)
        {
            XElement step = document.Descendants().Single(element =>
                (string?)element.Attribute(xaml + "Name") == stepNames[index]);
            Assert.AreEqual(index.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)step.Attribute("Tag"));
        }

        XElement abilityRows = document.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "RiderAbilityRowsPanel");
        XElement abilityScroller = abilityRows.Ancestors().First(element =>
            element.Name.LocalName == "ScrollViewer");
        Assert.AreEqual("Enabled", (string?)abilityScroller.Attribute("HorizontalScrollMode"));
        Assert.AreEqual("Auto", (string?)abilityScroller.Attribute("HorizontalScrollBarVisibility"));

        string source = File.ReadAllText(GetMainWindowCodePath());
        StringAssert.Contains(
            source,
            "if (_riderCreationSessionId == session.SessionId && _riderCreationDraft is not null)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "|| step > _riderCreationMaxVisitedStep)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "ResetRiderCreationForSession(sessionId);",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "Create Rider draft retained · Step",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void RiderRecoveryKeepsManualInputUntilUseSelectedRowsIsInvoked()
    {
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement riderIds = document.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "RiderIdsTextBox");
        Assert.IsNull(riderIds.Attribute("IsReadOnly"));

        string source = File.ReadAllText(GetMainWindowCodePath());
        int selectionStart = source.IndexOf("private void TableGrid_SelectionChanged", StringComparison.Ordinal);
        int selectionEnd = source.IndexOf("private async void TableGrid_EditCommitted", selectionStart, StringComparison.Ordinal);
        string selectionHandler = source[selectionStart..selectionEnd];
        Assert.IsFalse(
            selectionHandler.Contains("RiderIdsTextBox.Text =", StringComparison.Ordinal),
            "Ordinary grid selection must not overwrite manual rider IDs.");
        StringAssert.Contains(source, "private void UseSelectedRiderRows_Click", StringComparison.Ordinal);
        StringAssert.Contains(source, "RiderIdInputParser.Parse", StringComparison.Ordinal);
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

    [TestMethod]
    public void WorkingCopyStatusIsCompactAccessibleCommandBarContent()
    {
        const string workingCopyLabel = "Working copy ready";
        const string workingCopyHelp =
            "The original CDB remains untouched until you choose Save.";
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement commandBar = document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "WorkspaceCommandBar");
        XElement commandContent = commandBar
            .Elements()
            .Single(element => element.Name.LocalName == "CommandBar.Content");
        XElement workingCopyStatus = commandContent
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "WorkingCopyStatus");
        XElement visibleLabel = workingCopyStatus
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBlock"
                && (string?)element.Attribute("Text") == workingCopyLabel);

        Assert.AreEqual("True", (string?)commandBar.Attribute("IsDynamicOverflowEnabled"));
        Assert.AreEqual("Center", (string?)commandBar.Attribute("VerticalContentAlignment"));
        Assert.AreEqual("Collapsed", (string?)workingCopyStatus.Attribute("Visibility"));
        Assert.AreEqual("Transparent", (string?)workingCopyStatus.Attribute("Background"));
        Assert.AreEqual(
            "WorkingCopyReadyStatus",
            (string?)visibleLabel.Attribute("AutomationProperties.AutomationId"));
        Assert.AreEqual(
            workingCopyLabel,
            (string?)visibleLabel.Attribute("AutomationProperties.Name"));
        Assert.AreEqual(
            workingCopyHelp,
            (string?)visibleLabel.Attribute("AutomationProperties.HelpText"));
        Assert.AreEqual(
            "Polite",
            (string?)visibleLabel.Attribute("AutomationProperties.LiveSetting"));
        Assert.AreEqual(
            workingCopyHelp,
            (string?)workingCopyStatus.Attribute("ToolTipService.ToolTip"));
        Assert.AreEqual("NoWrap", (string?)visibleLabel.Attribute("TextWrapping"));
        Assert.AreEqual("CharacterEllipsis", (string?)visibleLabel.Attribute("TextTrimming"));

        XElement operationInfo = document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "OperationInfo");
        Assert.IsFalse(
            operationInfo.Ancestors().Contains(commandContent),
            "Consequential operation messages must remain separate from compact command-bar content.");
        Assert.AreEqual("1", (string?)operationInfo.Attribute("Grid.Row"));
        Assert.AreEqual(
            "OperationStatusMessage",
            (string?)operationInfo.Attribute("AutomationProperties.AutomationId"));
    }

    [TestMethod]
    public void WorkingCopyStatusOnlyReplacesTheRoutineOpenSuccessMessage()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        string openPath = GetMethodSource(source, "private async Task OpenPathAsync(string path)");
        string presentWorkingCopy = GetMethodSource(source, "private void PresentWorkingCopyReady()");
        string presentInfoBar = GetMethodSource(
            source,
            "private void PresentInfoBar(string title, string message, InfoBarSeverity severity)");
        string resetToNoFile = GetMethodSource(source, "private void ResetToNoFile()");

        StringAssert.Contains(openPath, "PresentWorkingCopyReady();", StringComparison.Ordinal);
        Assert.IsFalse(
            openPath.Contains("PresentSuccess(", StringComparison.Ordinal),
            "The routine open result must use the compact working-copy presenter.");
        Assert.IsFalse(
            openPath.Contains("\"Working copy ready\"", StringComparison.Ordinal),
            "The compact presenter's accessible text belongs to its XAML contract, not a generic InfoBar call.");

        AssertAppearsBefore(
            presentWorkingCopy,
            "OperationInfo.IsOpen = false;",
            "WorkingCopyStatus.Visibility = Visibility.Visible;",
            "The full-width InfoBar must close before the compact status becomes visible.");
        AssertAppearsBefore(
            presentInfoBar,
            "WorkingCopyStatus.Visibility = Visibility.Collapsed;",
            "OperationInfo.IsOpen = true;",
            "Generic operation feedback must hide the compact status before opening the InfoBar.");
        StringAssert.Contains(
            resetToNoFile,
            "WorkingCopyStatus.Visibility = Visibility.Collapsed;",
            StringComparison.Ordinal);
    }

    private static string GetMainWindowXamlPath()
    {
        return GetMainWindowSourcePath("MainWindow.xaml");
    }

    private static string GetMainWindowCodePath()
    {
        return GetMainWindowSourcePath("MainWindow.xaml.cs");
    }

    private static string GetMethodSource(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.IsTrue(signatureStart >= 0, $"Expected method '{signature}' was not found.");

        int bodyStart = source.IndexOf('{', signatureStart);
        Assert.IsTrue(bodyStart >= 0, $"Expected method '{signature}' has no body.");

        var depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[signatureStart..(index + 1)];
                    }
                    break;
            }
        }

        Assert.Fail($"Expected method '{signature}' has an unterminated body.");
        return string.Empty;
    }

    private static void AssertAppearsBefore(
        string source,
        string first,
        string second,
        string message)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.IsTrue(firstIndex >= 0, $"{message} Missing '{first}'.");
        Assert.IsTrue(secondIndex >= 0, $"{message} Missing '{second}'.");
        Assert.IsTrue(firstIndex < secondIndex, message);
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
