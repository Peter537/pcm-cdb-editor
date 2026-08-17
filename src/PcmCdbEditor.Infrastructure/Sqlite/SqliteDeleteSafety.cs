using Microsoft.Data.Sqlite;

namespace PcmCdbEditor.Infrastructure.Sqlite;

internal static class SqliteDeleteSafety
{
    public static async Task EnsureDeleteIsReversibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        if (await HasUserDefinedDeleteTriggerAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Rows in '{tableName}' cannot be deleted safely because the table has a DELETE trigger whose side effects cannot be restored by undo.");
        }

        InboundDeleteAction? inboundAction = await FindSideEffectingInboundForeignKeyAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false);
        if (inboundAction is not null)
        {
            throw new InvalidOperationException(
                $"Rows in '{tableName}' cannot be deleted safely because '{inboundAction.ChildTable}' uses ON DELETE {inboundAction.Action}, whose side effects cannot be restored by undo.");
        }
    }

    private static async Task<bool> HasUserDefinedDeleteTriggerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sql
            FROM sqlite_schema
            WHERE type = 'trigger'
              AND tbl_name = $tableName COLLATE NOCASE
              AND sql IS NOT NULL
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsDeleteTrigger(reader.GetString(0)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<InboundDeleteAction?> FindSideEffectingInboundForeignKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var childTables = new List<string>();
        await using (var tablesCommand = connection.CreateCommand())
        {
            tablesCommand.Transaction = transaction;
            tablesCommand.CommandText = """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table'
                  AND substr(name, 1, 7) <> 'sqlite_'
                ORDER BY name COLLATE NOCASE
                """;
            await using var reader = await tablesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                childTables.Add(reader.GetString(0));
            }
        }

        foreach (string childTable in childTables)
        {
            await using var foreignKeysCommand = connection.CreateCommand();
            foreignKeysCommand.Transaction = transaction;
            foreignKeysCommand.CommandText = $"PRAGMA foreign_key_list({SqliteSupport.QuoteIdentifier(childTable)})";
            await using var reader = await foreignKeysCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string parentTable = reader.GetString(2);
                string action = reader.GetString(6);
                if (parentTable.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                    && IsSideEffectingDeleteAction(action))
                {
                    return new InboundDeleteAction(childTable, action.ToUpperInvariant());
                }
            }
        }

        return null;
    }

    private static bool IsSideEffectingDeleteAction(string action) =>
        action.Equals("CASCADE", StringComparison.OrdinalIgnoreCase)
        || action.Equals("SET NULL", StringComparison.OrdinalIgnoreCase)
        || action.Equals("SET DEFAULT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeleteTrigger(string sql)
    {
        bool foundTriggerKeyword = false;
        foreach (string word in EnumerateBareWords(sql))
        {
            if (!foundTriggerKeyword)
            {
                foundTriggerKeyword = word.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (word.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (word.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateBareWords(string sql)
    {
        for (int index = 0; index < sql.Length;)
        {
            char current = sql[index];
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                index = SkipQuotedToken(sql, index, current);
                continue;
            }

            if (current == '[')
            {
                index = SkipQuotedToken(sql, index, ']');
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                int start = index++;
                while (index < sql.Length
                       && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                yield return sql[start..index];
                continue;
            }

            index++;
        }
    }

    private static int SkipQuotedToken(string sql, int start, char terminator)
    {
        int index = start + 1;
        while (index < sql.Length)
        {
            if (sql[index] != terminator)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == terminator)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private sealed record InboundDeleteAction(string ChildTable, string Action);
}
