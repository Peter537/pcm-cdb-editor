using PcmCdbEditor.Domain;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;

namespace PcmCdbEditor.Infrastructure.Sqlite;

internal static class SqliteMutationSafety
{
    // An insertion is undone by a deletion, and a deletion by an insertion.
    // Check both directions while the same transaction still owns the schema.
    public static Task EnsureInsertDeleteAreReversibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken) =>
        EnsureReversibleAsync(connection, transaction, tableName, updatedColumns: null, cancellationToken);

    public static Task EnsureUpdateIsReversibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        IEnumerable<string> updatedColumns,
        CancellationToken cancellationToken) =>
        EnsureReversibleAsync(connection, transaction, tableName,
            updatedColumns.ToHashSet(SqliteIdentifierComparer.Instance), cancellationToken);

    private static async Task EnsureReversibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        HashSet<string>? updatedColumns,
        CancellationToken cancellationToken)
    {
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                SELECT type, name, sql FROM main.sqlite_schema
                WHERE tbl_name = $tableName COLLATE NOCASE
                  AND (type = 'trigger' OR (type = 'table' AND $checkAllocation))
                """;
            schema.Parameters.AddWithValue("$tableName", tableName);
            schema.Parameters.AddWithValue("$checkAllocation", updatedColumns is null);
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string sql = reader.GetString(2);
                if (reader.GetString(0) == "table")
                {
                    if (EnumerateBareWords(sql).Contains("AUTOINCREMENT", SqliteIdentifierComparer.Instance))
                    {
                        throw new UnsafeRowMutationException(
                            $"Table '{tableName}' uses AUTOINCREMENT. Row history cannot restore the sqlite_sequence counter that SQLite uses to allocate future IDs.");
                    }

                    continue;
                }

                string triggerName = reader.GetString(1);
                string? triggerEvent = ReadTriggerEvent(sql);
                if (triggerEvent is null)
                {
                    throw new UnsafeRowMutationException(
                        $"The event for trigger '{triggerName}' on '{tableName}' could not be identified. Row history cannot guarantee a reversible change.");
                }

                if ((triggerEvent == "UPDATE") == (updatedColumns is not null))
                {
                    throw new UnsafeRowMutationException(
                        $"The {triggerEvent} trigger '{triggerName}' on '{tableName}' may change other rows. Row history cannot restore its side effects.");
                }
            }
        }

        string eventName = updatedColumns is null ? "DELETE" : "UPDATE";
        string actionColumn = updatedColumns is null ? "on_delete" : "on_update";
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.Transaction = transaction;
        foreignKeys.CommandText = $"""
            SELECT child.name, fk.{actionColumn}, parentColumn.name, parentColumn.hidden
            FROM main.sqlite_schema AS child
            CROSS JOIN pragma_foreign_key_list(child.name, 'main') AS fk
            LEFT JOIN pragma_table_xinfo($tableName, 'main') AS parentColumn
              ON parentColumn.name = fk."to" COLLATE NOCASE
              OR (fk."to" IS NULL AND parentColumn.pk = fk.seq + 1)
            WHERE child.type = 'table' AND fk."table" = $tableName COLLATE NOCASE
              AND fk.{actionColumn} IN ('CASCADE', 'SET NULL', 'SET DEFAULT')
            """;
        foreignKeys.Parameters.AddWithValue("$tableName", tableName);
        await using var foreignKeyReader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await foreignKeyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Generated parent keys can change when their source columns are written.
            // An unresolved parent key likewise cannot establish a safe update.
            if (updatedColumns is not null && !foreignKeyReader.IsDBNull(2)
                && foreignKeyReader.GetInt32(3) == 0 && !updatedColumns.Contains(foreignKeyReader.GetString(2)))
            {
                continue;
            }

            throw new UnsafeRowMutationException(
                $"Table '{foreignKeyReader.GetString(0)}' references '{tableName}' with ON {eventName} {foreignKeyReader.GetString(1)}. Row history cannot restore its side effects.");
        }
    }

    private static string? ReadTriggerEvent(string sql)
    {
        bool foundTriggerKeyword = false;
        foreach (string word in EnumerateBareWords(sql))
        {
            if (!foundTriggerKeyword)
            {
                foundTriggerKeyword = SqliteIdentifierComparer.Instance.Equals(word, "TRIGGER");
                continue;
            }

            if (SqliteIdentifierComparer.Instance.Equals(word, "DELETE")
                || SqliteIdentifierComparer.Instance.Equals(word, "INSERT")
                || SqliteIdentifierComparer.Instance.Equals(word, "UPDATE"))
            {
                return word.ToUpperInvariant();
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBareWords(string sql)
    {
        for (int index = 0; index < sql.Length;)
        {
            char current = sql[index];
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n')
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

            if (char.IsAsciiLetter(current) || (current >= '\u0080' && current != '\uFEFF') || current == '_')
            {
                int start = index++;
                while (index < sql.Length
                       && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] >= '\u0080' || sql[index] is '_' or '$'))
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
}
