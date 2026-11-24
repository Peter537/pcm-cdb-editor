using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

public sealed class JanuaryFirstRepairService : IJanuaryFirstRepairService
{
    private const string TargetTable = "DYN_result_season_stage";

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Requirements =
        new Dictionary<string, IReadOnlyCollection<string>>(SqliteIdentifierComparer.Instance)
        {
            ["GAM_config"] = new[] { "gene_i_date" },
            [TargetTable] = Array.Empty<string>()
        };

    public Task<MaintenanceCapability> CheckCapabilityAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        MaintenanceSupport.CheckAsync(sqlitePath, CheckCapability, cancellationToken);

    private static MaintenanceCapability CheckCapability(DatabaseSchemaCatalog catalog) =>
        MaintenanceSupport.Check(catalog, MaintenanceToolKind.JanuaryFirstSeasonStageRepair, Requirements, [TargetTable],
            static table => FindRowIdAlias(table) is null
                ? $"The mutation target '{TargetTable}' must expose an unshadowed SQLite rowid."
                : null);

    public Task<JanuaryFirstRepairPreview> PreviewAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PreviewCoreAsync(sqlitePath, cancellationToken),
            cancellationToken);

    private static async Task<JanuaryFirstRepairPreview> PreviewCoreAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        DatabaseSchemaCatalog catalog = await MaintenanceSupport.RequireCatalogAsync(sqlitePath, CheckCapability, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        var date = await SqliteSupport.ReadSingleGameDateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        EnsureJanuaryFirst(date);
        string rowIdAlias = RequireRowIdAlias(catalog);
        var state = await ReadStateAsync(connection, null, date, rowIdAlias, cancellationToken).ConfigureAwait(false);
        return new JanuaryFirstRepairPreview(state.Token, date, state.Count);
    }

    public Task<MaintenanceApplyResult> ApplyAsync(
        string sqlitePath,
        JanuaryFirstRepairPreview preview,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ApplyCoreAsync(sqlitePath, preview, cancellationToken),
            cancellationToken);

    private static async Task<MaintenanceApplyResult> ApplyCoreAsync(
        string sqlitePath,
        JanuaryFirstRepairPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        DatabaseSchemaCatalog catalog = await MaintenanceSupport.RequireCatalogAsync(sqlitePath, CheckCapability, cancellationToken)
            .ConfigureAwait(false);
        TableSchema historyTable = MaintenanceHistoryCapture.RequireEditableTable(catalog, TargetTable);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var date = await SqliteSupport.ReadSingleGameDateAsync(connection, sqliteTransaction, cancellationToken)
                .ConfigureAwait(false);
            EnsureJanuaryFirst(date);
            string rowIdAlias = RequireRowIdAlias(catalog);
            var state = await ReadStateAsync(connection, sqliteTransaction, date, rowIdAlias, cancellationToken)
                .ConfigureAwait(false);
            if (date != preview.CurrentDate
                || state.Count != preview.RowCount
                || !state.Token.Equals(preview.SnapshotToken, StringComparison.Ordinal))
            {
                throw new DBConcurrencyException("Season-stage data changed after the preview was generated.");
            }

            if (state.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MaintenanceApplyResult(0, $"{TargetTable} is empty. There were no rows to delete.");
            }

            await SqliteMutationSafety.EnsureInsertDeleteAreReversibleAsync(
                    connection,
                    sqliteTransaction,
                    TargetTable,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<TypedRow> deletedRows = await MaintenanceHistoryCapture.ReadAllAsync(
                    connection,
                    sqliteTransaction,
                    historyTable,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = $"DELETE FROM {SqliteSupport.QuoteIdentifier(TargetTable)}";
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != deletedRows.Count)
            {
                throw new DBConcurrencyException("Season-stage rows changed while deletion history was captured.");
            }

            var historyOperation = new MaintenanceEditOperation(
                Guid.NewGuid(),
                TargetTable,
                DateTimeOffset.UtcNow,
                MaintenanceToolKind.JanuaryFirstSeasonStageRepair,
                "January 1 season-stage cleanup",
                deletedRows.Select(row => new MaintenanceRowChange(TargetTable, row, null)));
            IReadOnlyList<RowReplayGuard> undoGuards = deletedRows
                .Select(row => RowReplayGuard.Absent(TargetTable, row.Identity!))
                .ToArray();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceApplyResult(
                affected,
                $"Deleted {affected:N0} {(affected == 1 ? "row" : "rows")} from {TargetTable} in the working copy.",
                historyOperation,
                undoGuards);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(long Count, string Token)> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateOnly date,
        string rowIdAlias,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        string rowId = SqliteSupport.QuoteIdentifier(rowIdAlias);
        command.CommandText = $"SELECT {rowId}, * FROM {SqliteSupport.QuoteIdentifier(TargetTable)} ORDER BY {rowId}";
        var tokenValues = new List<string> { date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) };
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
            var row = new List<string>(reader.FieldCount);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row.Add(CanonicalValue(reader, ordinal));
            }

            tokenValues.Add(string.Join(':', row));
        }

        return (count, MaintenanceSupport.ComputeToken(tokenValues));
    }

    private static string RequireRowIdAlias(DatabaseSchemaCatalog catalog) =>
        catalog.TryGetTable(TargetTable, out TableSchema? table) && FindRowIdAlias(table) is { } rowId
            ? rowId
            : throw new InvalidOperationException($"The mutation target '{TargetTable}' must expose an unshadowed SQLite rowid.");

    private static string? FindRowIdAlias(TableSchema table)
    {
        if (table.IsWithoutRowId)
        {
            return null;
        }

        string[] aliases = ["rowid", "_rowid_", "oid"];
        return aliases.FirstOrDefault(alias => !table.Columns.Any(column =>
            SqliteIdentifierComparer.Instance.Equals(column.Name, alias)));
    }

    private static string CanonicalValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return "N";
        }

        return reader.GetFieldType(ordinal) switch
        {
            var type when type == typeof(long) => $"I{reader.GetInt64(ordinal)}",
            var type when type == typeof(double) => $"R{BitConverter.DoubleToInt64Bits(reader.GetDouble(ordinal)):X16}",
            var type when type == typeof(string) => $"T{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(reader.GetString(ordinal)))}",
            var type when type == typeof(byte[]) => $"B{Convert.ToBase64String((byte[])reader.GetValue(ordinal))}",
            _ => throw new InvalidDataException("The target table contains an unsupported SQLite storage class.")
        };
    }

    private static void EnsureJanuaryFirst(DateOnly date)
    {
        if (date.Month != 1 || date.Day != 1)
        {
            throw new InvalidOperationException(
                $"The season-stage repair is available only on January 1. The in-game date is {date:yyyy-MM-dd}.");
        }
    }
}
