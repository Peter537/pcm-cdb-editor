using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PcmCdbEditor.UnitTests;

[TestClass]
public sealed class TableGridAdapterSourceTests
{
    [TestMethod]
    public void WideGridColumnsUseDirectTemplateElementsWithoutLosingEditOrClipboardContracts()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());

        Assert.IsFalse(
            source.Contains("new WinUI.TableView.TableViewTextColumn", StringComparison.Ordinal),
            "TableViewTextColumn re-enters the 1.4.1 infinite-width measurement path.");
        StringAssert.Contains(
            source,
            "DirectTextTableViewColumn : WinUI.TableView.TableViewTemplateColumn",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override FrameworkElement GenerateElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override FrameworkElement GenerateEditingElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "Text = GetText(dataItem)",
            StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override void RefreshElement(",
            StringComparison.Ordinal);
        StringAssert.Contains(source, "EditingTemplate = new DataTemplate()", StringComparison.Ordinal);
        StringAssert.Contains(
            source,
            "public override object? GetClipboardContent(object? dataItem) => GetText(dataItem)",
            StringComparison.Ordinal);
        Assert.IsFalse(
            source.Contains("SetBinding(", StringComparison.Ordinal),
            "Wide-grid display and edit elements must not allocate per-cell binding expressions.");
        Assert.IsFalse(
            source.Contains("new ContentControl", StringComparison.Ordinal),
            "Cell elements must remain direct children for bounded TableView measurement.");
    }

    [TestMethod]
    public void InlineEditMutationIsPublishedOnlyAfterTableViewFinishesTheCommit()
    {
        string source = File.ReadAllText(GetAdapterSourcePath());
        string xaml = File.ReadAllText(GetAdapterXamlPath());

        StringAssert.Contains(
            xaml,
            "CellEditEnding=\"GridControl_CellEditEnding\"",
            StringComparison.Ordinal);
        StringAssert.Contains(
            xaml,
            "CellEditEnded=\"GridControl_CellEditEnded\"",
            StringComparison.Ordinal);

        int endingStart = source.IndexOf(
            "private void GridControl_CellEditEnding(",
            StringComparison.Ordinal);
        int endedStart = source.IndexOf(
            "private void GridControl_CellEditEnded(",
            StringComparison.Ordinal);
        Assert.IsTrue(endingStart >= 0, "The cancelable validation event must remain wired.");
        Assert.IsTrue(endedStart > endingStart, "The post-commit event must publish staged edits.");

        string endingBody = source[endingStart..endedStart];
        Assert.IsFalse(
            endingBody.Contains("AnnounceEdit(", StringComparison.Ordinal),
            "CellEditEnding runs before TableView commits and must not start a mutation or rebind.");
        StringAssert.Contains(
            source[endedStart..],
            "AnnounceEdit(",
            StringComparison.Ordinal);
    }

    private static string GetAdapterSourcePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "PcmCdbEditor.App",
                "Controls",
                "TableGridAdapterControl.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "TableGridAdapterControl.xaml.cs could not be resolved from the test output directory.");
    }

    private static string GetAdapterXamlPath()
    {
        string sourcePath = GetAdapterSourcePath();
        return sourcePath[..^".cs".Length];
    }
}
