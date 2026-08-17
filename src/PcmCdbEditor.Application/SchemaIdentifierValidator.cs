using PcmCdbEditor.Domain;

namespace PcmCdbEditor.Application;

public sealed class UnknownSchemaIdentifierException : ArgumentException
{
    public UnknownSchemaIdentifierException(string message, string parameterName)
        : base(message, parameterName)
    {
    }
}

public static class SchemaIdentifierValidator
{
    public static TableSchema RequireTable(DatabaseSchemaCatalog catalog, string requestedTable)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        RequireWellFormed(requestedTable, nameof(requestedTable));

        if (!catalog.TryGetTable(requestedTable, out var table))
        {
            throw new UnknownSchemaIdentifierException(
                $"Table '{requestedTable}' is not present in the discovered schema.",
                nameof(requestedTable));
        }

        return table;
    }

    public static ColumnSchema RequireColumn(TableSchema table, string requestedColumn)
    {
        ArgumentNullException.ThrowIfNull(table);
        RequireWellFormed(requestedColumn, nameof(requestedColumn));

        var column = table.Columns.FirstOrDefault(
            candidate => candidate.Name.Equals(requestedColumn, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            throw new UnknownSchemaIdentifierException(
                $"Column '{requestedColumn}' is not present on table '{table.Name}'.",
                nameof(requestedColumn));
        }

        return column;
    }

    public static void ValidateQuery(DatabaseSchemaCatalog catalog, TableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var table = RequireTable(catalog, query.TableName);

        foreach (var sort in query.Sorts)
        {
            RequireColumn(table, sort.ColumnName);
        }

        if (query.Filter is not null)
        {
            ValidateFilter(table, query.Filter);
        }

        if (query.Search is not null)
        {
            foreach (var column in query.Search.EligibleColumns)
            {
                RequireColumn(table, column);
            }
        }
    }

    public static void ValidateFilter(TableSchema table, FilterExpression expression)
    {
        switch (expression)
        {
            case FilterCondition condition:
                RequireColumn(table, condition.ColumnName);
                break;
            case FilterGroup group:
                foreach (var child in group.Children)
                {
                    ValidateFilter(table, child);
                }

                break;
            default:
                throw new ArgumentException("Unsupported filter expression type.", nameof(expression));
        }
    }

    private static void RequireWellFormed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new UnknownSchemaIdentifierException(
                "SQLite identifiers must be non-empty and cannot contain NUL characters.",
                parameterName);
        }
    }
}
