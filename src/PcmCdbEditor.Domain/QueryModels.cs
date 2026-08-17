namespace PcmCdbEditor.Domain;

public enum FilterOperator
{
    Contains,
    StartsWith,
    EndsWith,
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsNull,
    IsNotNull
}

public enum FilterGroupOperator
{
    And,
    Or
}

public abstract record FilterExpression;

public sealed record FilterCondition(string ColumnName, FilterOperator Operator, SqliteValue Value) : FilterExpression;

public sealed record FilterGroup : FilterExpression
{
    public FilterGroup(FilterGroupOperator @operator, IEnumerable<FilterExpression> children)
    {
        Operator = @operator;
        Children = ModelCollections.Freeze(children);
        if (Children.Count == 0)
        {
            throw new ArgumentException("A filter group needs at least one child.", nameof(children));
        }
    }

    public FilterGroupOperator Operator { get; }

    public IReadOnlyList<FilterExpression> Children { get; }
}

public sealed record NumberedFilterRule(int Number, FilterCondition Condition);

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record SortDescriptor(string ColumnName, SortDirection Direction);

public enum ForeignKeyDisplayMode
{
    RawValue,
    ResolvedName,
    RawAndName
}

public sealed record GlobalSearchRequest
{
    public GlobalSearchRequest(string text, IEnumerable<string> eligibleColumns)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        EligibleColumns = ModelCollections.Freeze(eligibleColumns);
    }

    public string Text { get; }

    public IReadOnlyList<string> EligibleColumns { get; }
}

public sealed record PageRequest
{
    public PageRequest(long offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 10_000);

        Offset = offset;
        Limit = limit;
    }

    public long Offset { get; }

    public int Limit { get; }
}

public sealed record TableQuery
{
    public TableQuery(
        string tableName,
        PageRequest page,
        IEnumerable<SortDescriptor>? sorts = null,
        FilterExpression? filter = null,
        GlobalSearchRequest? search = null,
        ForeignKeyDisplayMode foreignKeyDisplayMode = ForeignKeyDisplayMode.RawAndName)
    {
        TableName = tableName;
        Page = page;
        Sorts = ModelCollections.Freeze(sorts);
        Filter = filter;
        Search = search;
        ForeignKeyDisplayMode = foreignKeyDisplayMode;
    }

    public string TableName { get; }

    public PageRequest Page { get; }

    public IReadOnlyList<SortDescriptor> Sorts { get; }

    public FilterExpression? Filter { get; }

    public GlobalSearchRequest? Search { get; }

    public ForeignKeyDisplayMode ForeignKeyDisplayMode { get; }
}

public sealed record TablePage
{
    public TablePage(string tableName, PageRequest request, long totalRows, IEnumerable<TypedRow> rows, bool hasMore)
    {
        TableName = tableName;
        Request = request;
        TotalRows = totalRows;
        Rows = ModelCollections.Freeze(rows);
        HasMore = hasMore;
    }

    public string TableName { get; }

    public PageRequest Request { get; }

    public long TotalRows { get; }

    public IReadOnlyList<TypedRow> Rows { get; }

    public bool HasMore { get; }
}

public sealed record TableSlice
{
    public TableSlice(string tableName, PageRequest request, IEnumerable<TypedRow> rows, bool hasMore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(request);

        TableName = tableName;
        Request = request;
        Rows = ModelCollections.Freeze(rows);
        HasMore = hasMore;
    }

    public string TableName { get; }

    public PageRequest Request { get; }

    public IReadOnlyList<TypedRow> Rows { get; }

    public bool HasMore { get; }
}

public enum TableRowCountStatus
{
    Unknown,
    Loading,
    Available,
    Cancelled,
    Failed
}

public sealed record TableRowCountState
{
    private TableRowCountState(TableRowCountStatus status, long? value, string? problem)
    {
        Status = status;
        Value = value;
        Problem = problem;
    }

    public static TableRowCountState Unknown { get; } = new(TableRowCountStatus.Unknown, null, null);

    public static TableRowCountState Loading { get; } = new(TableRowCountStatus.Loading, null, null);

    public static TableRowCountState Cancelled { get; } = new(TableRowCountStatus.Cancelled, null, null);

    public static TableRowCountState Available(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return new TableRowCountState(TableRowCountStatus.Available, value, null);
    }

    public static TableRowCountState Failed(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return new TableRowCountState(TableRowCountStatus.Failed, null, problem);
    }

    public TableRowCountStatus Status { get; }

    public long? Value { get; }

    public string? Problem { get; }
}
