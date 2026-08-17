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
}
