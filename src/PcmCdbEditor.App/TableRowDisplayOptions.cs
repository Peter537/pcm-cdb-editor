namespace PcmCdbEditor.App;

internal readonly record struct TableRowDisplayChoice
{
    private TableRowDisplayChoice(int? pageSize)
    {
        PageSize = pageSize;
    }

    public static TableRowDisplayChoice AllRows { get; } = new(pageSize: null);

    public int? PageSize { get; }

    public bool IsAllRows => !PageSize.HasValue;

    public string PickerLabel => IsAllRows ? "All rows" : $"{PageSize!.Value:N0} rows";

    public string StatusLabel => IsAllRows ? "All rows" : $"{PageSize!.Value:N0} rows/page";

    public static TableRowDisplayChoice FromPageSize(int pageSize)
    {
        if (!TableRowDisplayOptions.IsSupportedFinitePageSize(pageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size is not supported.");
        }

        return new TableRowDisplayChoice(pageSize);
    }
}

internal static class TableRowDisplayOptions
{
    public const int DefaultPageSize = 100;

    private static readonly int[] FinitePageSizeValues = [100, 250, 500];
    private static readonly TableRowDisplayChoice[] ToolbarChoiceValues =
    [
        .. FinitePageSizeValues.Select(TableRowDisplayChoice.FromPageSize),
        TableRowDisplayChoice.AllRows,
    ];

    public static IReadOnlyList<int> FinitePageSizes { get; } =
        Array.AsReadOnly(FinitePageSizeValues);

    public static IReadOnlyList<TableRowDisplayChoice> ToolbarChoices { get; } =
        Array.AsReadOnly(ToolbarChoiceValues);

    public static bool IsSupportedFinitePageSize(int pageSize) =>
        Array.IndexOf(FinitePageSizeValues, pageSize) >= 0;

    public static TableRowDisplayChoice FromToolbarIndex(int index)
    {
        if (index < 0 || index >= ToolbarChoiceValues.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ToolbarChoiceValues[index];
    }

    public static int GetToolbarIndex(TableRowDisplayChoice choice)
    {
        int index = Array.IndexOf(ToolbarChoiceValues, choice);
        return index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(choice), choice, "The row display choice is not supported.");
    }
}
