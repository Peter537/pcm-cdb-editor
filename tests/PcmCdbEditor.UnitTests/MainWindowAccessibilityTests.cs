using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class MainWindowAccessibilityTests
{
    private static readonly string[] ExpectedTableRowDisplayLabels =
        ["100 rows", "250 rows", "500 rows", "All rows (slower)"];

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
            ["RecoverySessionsList"] = "RecoverableSessionList",
            ["ResumeRecoveryButton"] = "ResumeRecoverySessionCommand",
            ["DiscardRecoveryButton"] = "DiscardRecoverySessionCommand",
            ["CleanCompletedSessionsButton"] = "CleanCompletedSessionsCommand",
            ["RecentDatabasesButton"] = "ShowRecentDatabasesCommand",
            ["EmptyStateRecentDatabasesButton"] = "EmptyStateRecentDatabasesCommand",
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
            "if (!startAnother && _riderCreationSessionId == session.SessionId && _riderCreationDraft is not null)",
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
        const string workingCopyLabel = "{x:Bind ViewModel.SaveStatus, Mode=OneWay}";
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
    public void PersistentSaveStatusRemainsSeparateFromOperationFeedback()
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
            "UpdatePersistentSaveState();",
            "OperationInfo.IsOpen = true;",
            "Operation feedback must preserve the independent saved/unsaved status.");
        StringAssert.Contains(
            resetToNoFile,
            "WorkingCopyStatus.Visibility = Visibility.Collapsed;",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void TableRowDisplayOptionsAreOrderedAndDescribeTheUnboundedChoice()
    {
        const string helpText =
            "Choose 100, 250, or 500 rows per page, or load all matching rows for the current table. " +
            "Loading all rows may require substantial time and memory.";
        XDocument document = XDocument.Load(GetMainWindowXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement pageSizeBox = document
            .Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PageSizeBox");
        string[] optionLabels = pageSizeBox
            .Elements()
            .Where(static element => element.Name.LocalName == "ComboBoxItem")
            .Select(static element => (string?)element.Attribute("Content"))
            .OfType<string>()
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedTableRowDisplayLabels,
            optionLabels);
        Assert.AreEqual("TablePageSize", (string?)pageSizeBox.Attribute("AutomationProperties.AutomationId"));
        Assert.AreEqual("Rows to show", (string?)pageSizeBox.Attribute("AutomationProperties.Name"));
        Assert.AreEqual(helpText, (string?)pageSizeBox.Attribute("AutomationProperties.HelpText"));

        string source = File.ReadAllText(GetMainWindowCodePath());
        string updateChrome = GetMethodSource(source, "private void UpdateTabChrome(");
        string selectionChanged = GetMethodSource(
            source,
            "private async void PageSizeBox_SelectionChanged(");
        string settings = GetMethodSource(source, "private async Task ShowSettingsAsync()");

        StringAssert.Contains(
            updateChrome,
            "TableRowDisplayOptions.GetToolbarIndex(context.Display.RowDisplay)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            selectionChanged,
            "TableRowDisplayOptions.FromToolbarIndex(",
            StringComparison.Ordinal);
        Assert.IsFalse(
            selectionChanged.Contains("SelectedIndex ==", StringComparison.Ordinal),
            "Page-size values must come from the canonical option map rather than fragile item positions.");
        StringAssert.Contains(
            settings,
            "ItemsSource = TableRowDisplayOptions.FinitePageSizes",
            StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "SelectedItem = opening.PageSize",
            StringComparison.Ordinal);
        Assert.IsFalse(
            settings.Contains("ToolbarChoices", StringComparison.Ordinal),
            "The session-only All rows choice must not be offered as a persisted setting.");
    }

    [TestMethod]
    public void AllRowsRemainsAnExplicitTabOnlyDisplayMode()
    {
        string source = File.ReadAllText(GetMainWindowCodePath());
        string normalizedSource = source.ReplaceLineEndings("\n");
        string activateSession = GetMethodSource(source, "private async Task ActivateSessionAsync(");
        string loadSelectedTable = GetMethodSource(source, "private async Task LoadSelectedTableAsync(");
        string openTable = GetMethodSource(source, "private async Task OpenTableAsync(");
        string ensureTabBound = GetMethodSource(source, "private void EnsureTabBound(");
        string updatePaging = GetMethodSource(source, "private void UpdateCountAndPagingChrome(");
        string previousPage = GetMethodSource(source, "private async void PreviousPage_Click(");
        string nextPage = GetMethodSource(source, "private async void NextPage_Click(");
        string refreshSelectedTable = GetMethodSource(
            source,
            "private async Task RefreshSelectedTableAsync()");
        string tabSelectionChanged = GetMethodSource(
            source,
            "private async void TableTabs_SelectionChanged(");
        string invalidateTableCaches = GetMethodSource(
            source,
            "private void InvalidateTableCaches()");
        string invalidateForeignKeyQueries = GetMethodSource(
            source,
            "private void InvalidateOpenTableQueriesForForeignKeyMode()");
        string selectionChanged = GetMethodSource(
            source,
            "private async void PageSizeBox_SelectionChanged(");
        string searchChanged = GetMethodSource(
            source,
            "private async void CurrentTableSearchBox_TextChanged(");
        string filtersClick = GetMethodSource(source, "private async void Filters_Click(");
        string sortClick = GetMethodSource(source, "private async void Sort_Click(");
        string settings = GetMethodSource(source, "private async Task ShowSettingsAsync()");

        const string persistedFiniteChoice =
            "TableRowDisplayChoice.FromPageSize(_preferences.PageSize)";
        StringAssert.Contains(activateSession, persistedFiniteChoice, StringComparison.Ordinal);
        StringAssert.Contains(loadSelectedTable, persistedFiniteChoice, StringComparison.Ordinal);

        StringAssert.Contains(
            normalizedSource,
            "private sealed record TableDisplayWindow(\n        TableRowDisplayChoice RowDisplay,",
            StringComparison.Ordinal);
        StringAssert.Contains(
            normalizedSource,
            "private sealed record TableTabContext(\n        TableSchema Schema,\n        TableDisplayWindow Display,",
            StringComparison.Ordinal);
        StringAssert.Contains(
            normalizedSource,
            "TableDisplayWindow Display,\n        TableRowDisplayChoice? PendingRowDisplay,",
            StringComparison.Ordinal);

        StringAssert.Contains(openTable, "if (effectiveRowDisplay.IsAllRows)", StringComparison.Ordinal);
        StringAssert.Contains(openTable, "offset = 0;", StringComparison.Ordinal);
        StringAssert.Contains(openTable, "coordinator.LoadAllRowsAsync(cancellationToken)", StringComparison.Ordinal);
        StringAssert.Contains(openTable, "Offset: 0", StringComparison.Ordinal);
        StringAssert.Contains(openTable, "HasMore: false", StringComparison.Ordinal);
        StringAssert.Contains(
            openTable,
            "countState = TableRowCountState.Available(rows.Count);",
            StringComparison.Ordinal);
        StringAssert.Contains(openTable, "PendingRowDisplay: null", StringComparison.Ordinal);
        StringAssert.Contains(
            ensureTabBound,
            "!context.Display.RowDisplay.IsAllRows",
            StringComparison.Ordinal);
        StringAssert.Contains(
            ensureTabBound,
            "context.CountState.Status != TableRowCountStatus.Available",
            StringComparison.Ordinal);
        StringAssert.Contains(ensureTabBound, "StartVirtualCount(tab, context);", StringComparison.Ordinal);
        StringAssert.Contains(
            invalidateTableCaches,
            "TableRowCountState.Available(context.Display.Rows.Count)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            invalidateTableCaches,
            "context.Display.RowDisplay.IsAllRows",
            StringComparison.Ordinal);
        StringAssert.Contains(
            invalidateForeignKeyQueries,
            "TableRowCountState.Available(context.Display.Rows.Count)",
            StringComparison.Ordinal);

        StringAssert.Contains(
            updatePaging,
            "context.PendingRowDisplay is null",
            StringComparison.Ordinal);
        StringAssert.Contains(updatePaging, "!context.Display.RowDisplay.IsAllRows", StringComparison.Ordinal);
        StringAssert.Contains(
            updatePaging,
            "PreviousPageButton.IsEnabled = canNavigatePages",
            StringComparison.Ordinal);
        StringAssert.Contains(
            updatePaging,
            "NextPageButton.IsEnabled = canNavigatePages",
            StringComparison.Ordinal);
        StringAssert.Contains(previousPage, "context.Display.RowDisplay.IsAllRows", StringComparison.Ordinal);
        StringAssert.Contains(nextPage, "context.Display.RowDisplay.IsAllRows", StringComparison.Ordinal);
        StringAssert.Contains(previousPage, "context.PendingRowDisplay is not null", StringComparison.Ordinal);
        StringAssert.Contains(nextPage, "context.PendingRowDisplay is not null", StringComparison.Ordinal);
        StringAssert.Contains(
            refreshSelectedTable,
            "_operationLease?.Token ??",
            StringComparison.Ordinal);
        StringAssert.Contains(
            refreshSelectedTable,
            "TableRowDisplayChoice rowDisplay = GetRequestedRowDisplay(context);",
            StringComparison.Ordinal);
        StringAssert.Contains(refreshSelectedTable, "GetRequestedOffset(context)", StringComparison.Ordinal);
        StringAssert.Contains(tabSelectionChanged, "GetRequestedRowDisplay(context)", StringComparison.Ordinal);
        StringAssert.Contains(tabSelectionChanged, "GetRequestedOffset(context)", StringComparison.Ordinal);

        int preferenceSaveIndex = selectionChanged.IndexOf(
            "await SavePreferencesAsync(",
            StringComparison.Ordinal);
        int queryIndex = selectionChanged.IndexOf(
            "await LoadTableQueryAsync(",
            StringComparison.Ordinal);
        int completedFinitePageIndex = selectionChanged.IndexOf(
            "outcome != TableQueryOutcome.Succeeded || rowDisplay.IsAllRows",
            StringComparison.Ordinal);
        Assert.IsTrue(
            queryIndex >= 0 && completedFinitePageIndex > queryIndex &&
            preferenceSaveIndex > completedFinitePageIndex,
            "Only a completed finite page may update the default preference.");
        Assert.IsFalse(selectionChanged.Contains("PendingRowDisplay =", StringComparison.Ordinal));
        StringAssert.Contains(selectionChanged, "_operationLease is not null", StringComparison.Ordinal);
        StringAssert.Contains(selectionChanged, "_activeTableLoad is not null", StringComparison.Ordinal);
        StringAssert.Contains(
            searchChanged,
            "tab.Tag is not TableTabContext currentContext",
            StringComparison.Ordinal);
        StringAssert.Contains(
            searchChanged,
            "!ReferenceEquals(TableTabs.SelectedItem, tab)",
            StringComparison.Ordinal);
        int searchDelayIndex = searchChanged.IndexOf(
            "await Task.Delay(SearchDelayMilliseconds",
            StringComparison.Ordinal);
        int searchResolveIndex = searchChanged.IndexOf(
            "context = currentContext;",
            StringComparison.Ordinal);
        int searchDispatchIndex = searchChanged.IndexOf(
            "await OpenTableAsync(",
            StringComparison.Ordinal);
        Assert.IsTrue(
            searchDelayIndex >= 0 &&
            searchResolveIndex > searchDelayIndex &&
            searchDispatchIndex > searchResolveIndex,
            "A debounced search must resolve the latest row-display intent before dispatch.");
        AssertLatestDialogContextIsResolvedBeforeQuery(
            filtersClick,
            "TableFilterDialogOutcome? outcome = await ShowTableFilterDialogAsync(");
        AssertLatestDialogContextIsResolvedBeforeQuery(
            sortClick,
            "IReadOnlyList<SortDescriptor>? sorts = await ShowTableSortDialogAsync(");
        StringAssert.Contains(settings, "TableRowDisplayChoice rowDisplay = pageSizeChanged", StringComparison.Ordinal);
        StringAssert.Contains(settings, "TableRowDisplayChoice? pendingRowDisplay = null;", StringComparison.Ordinal);
        StringAssert.Contains(settings, "pendingRowDisplay = rowDisplay;", StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "bool tableQueryChanged = pageSizeChanged || foreignKeyModeChanged;",
            StringComparison.Ordinal);
        StringAssert.Contains(settings, "if (tableQueryChanged &&", StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "TryBeginOperation(\"Save preferences\", out CancellationToken cancellationToken)",
            StringComparison.Ordinal);
        StringAssert.Contains(settings, "await SavePreferencesAsync(", StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "queryOutcome = await LoadTableQueryAsync(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "queryOutcome != TableQueryOutcome.Succeeded",
            StringComparison.Ordinal);
        StringAssert.Contains(settings, "IsInvalidated = true", StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "PendingRowDisplay = pendingRowDisplay ?? pendingContext.PendingRowDisplay",
            StringComparison.Ordinal);
        StringAssert.Contains(
            settings,
            "Preferences saved; table refresh cancelled",
            StringComparison.Ordinal);
        StringAssert.Contains(settings, "finally", StringComparison.Ordinal);
        StringAssert.Contains(settings, "EndOperation();", StringComparison.Ordinal);
        StringAssert.Contains(settings, "if (tableRefreshPending && !_disposed)", StringComparison.Ordinal);
        StringAssert.Contains(settings, persistedFiniteChoice, StringComparison.Ordinal);
        StringAssert.Contains(settings, ": GetRequestedRowDisplay(context);", StringComparison.Ordinal);
        StringAssert.Contains(
            normalizedSource,
            "context.PendingRowDisplay ?? context.Display.RowDisplay",
            StringComparison.Ordinal);
        StringAssert.Contains(
            normalizedSource,
            "context.PendingRowDisplay is null ? context.Display.Offset : 0",
            StringComparison.Ordinal);
        Assert.IsFalse(
            settings.Contains("TableRowDisplayChoice.AllRows", StringComparison.Ordinal) ||
            settings.Contains("ToolbarChoices", StringComparison.Ordinal),
            "Settings must never offer or persist the tab-only All rows choice.");
    }

    private static void AssertLatestDialogContextIsResolvedBeforeQuery(
        string methodSource,
        string dialogAwait)
    {
        int dialogIndex = methodSource.IndexOf(dialogAwait, StringComparison.Ordinal);
        int guardIndex = methodSource.IndexOf(
            "!ReferenceEquals(TableTabs.SelectedItem, tab)",
            StringComparison.Ordinal);
        int tagIndex = methodSource.IndexOf(
            "tab.Tag is not TableTabContext currentContext",
            StringComparison.Ordinal);
        int resolveIndex = methodSource.IndexOf("context = currentContext;", StringComparison.Ordinal);
        int queryIndex = methodSource.IndexOf("await LoadTableQueryAsync(", StringComparison.Ordinal);

        Assert.IsTrue(
            dialogIndex >= 0 &&
            guardIndex > dialogIndex &&
            tagIndex > dialogIndex &&
            resolveIndex > Math.Max(guardIndex, tagIndex) &&
            queryIndex > resolveIndex,
            "A dialog query must resolve the selected tab's latest row-display intent after the dialog and before dispatch.");
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
