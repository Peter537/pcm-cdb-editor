using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

public sealed class RiderRecoveryService : IRiderRecoveryService
{
    private const string Table = "DYN_cyclist_fitness";

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Requirements =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Table] = new[]
            {
                "IDcyclist",
                "value_f_FIT",
                "value_f_injury",
                "value_i_injury_num_days",
                "value_f_fat_phy",
                "value_f_freshness",
                "value_f_prepa"
            }
        };

    public Task<MaintenanceCapability> CheckCapabilityAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        MaintenanceSupport.CheckAsync(
            sqlitePath,
            MaintenanceToolKind.RiderRecovery,
            Requirements,
            cancellationToken);

    public Task<RiderRecoveryPreview> PreviewAsync(
        string sqlitePath,
        IReadOnlyCollection<long> cyclistIds,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PreviewCoreAsync(sqlitePath, cyclistIds, cancellationToken),
            cancellationToken);

    private async Task<RiderRecoveryPreview> PreviewCoreAsync(
        string sqlitePath,
        IReadOnlyCollection<long> cyclistIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cyclistIds);
        var capability = await CheckCapabilityAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        var ids = cyclistIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return new RiderRecoveryPreview(MaintenanceSupport.ComputeToken([]), ids, []);
        }

        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        var changes = await ReadChangesAsync(connection, transaction: null, ids, cancellationToken).ConfigureAwait(false);
        return new RiderRecoveryPreview(ComputeToken(ids, changes), ids, changes);
    }

    public Task<MaintenanceApplyResult> ApplyAsync(
        string sqlitePath,
        RiderRecoveryPreview preview,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ApplyCoreAsync(sqlitePath, preview, cancellationToken),
            cancellationToken);

    private async Task<MaintenanceApplyResult> ApplyCoreAsync(
        string sqlitePath,
        RiderRecoveryPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var capability = await CheckCapabilityAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        if (preview.CyclistIds.Count == 0)
        {
            return new MaintenanceApplyResult(0, "No rider recovery changes were required.");
        }

        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        TableSchema historyTable = MaintenanceHistoryCapture.RequireEditableTable(catalog, Table);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadChangesAsync(
                    connection,
                    sqliteTransaction,
                    preview.CyclistIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ComputeToken(preview.CyclistIds, current).Equals(preview.SnapshotToken, StringComparison.Ordinal))
            {
                throw new DBConcurrencyException("Rider fitness changed after the preview was generated.");
            }

            long[] changedIds = current
                .Where(static change => change.OldValues != change.NewValues)
                .Select(static change => change.CyclistId)
                .ToArray();
            IReadOnlyList<TypedRow> beforeRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection,
                    sqliteTransaction,
                    historyTable,
                    "IDcyclist",
                    changedIds,
                    cancellationToken)
                .ConfigureAwait(false);
            var affected = 0;
            foreach (var change in current.Where(static change => change.OldValues != change.NewValues))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = sqliteTransaction;
                command.CommandText = @"
UPDATE DYN_cyclist_fitness
SET value_f_FIT = $fit,
    value_f_injury = $injury,
    value_i_injury_num_days = $injuryDays,
    value_f_fat_phy = $fatigue,
    value_f_freshness = $freshness,
    value_f_prepa = $preparation
WHERE IDcyclist = $cyclistId";
                AddPresetParameters(command, change.CyclistId);
                affected += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MaintenanceApplyResult(0, "No rider recovery changes were required.");
            }

            IReadOnlyList<TypedRow> afterRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection,
                    sqliteTransaction,
                    historyTable,
                    "IDcyclist",
                    changedIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (beforeRows.Count != affected || afterRows.Count != affected)
            {
                throw new DBConcurrencyException("One or more rider rows disappeared while the preset was applied.");
            }

            MaintenanceEditOperation historyOperation = CreateHistoryOperation(beforeRows, afterRows);
            IReadOnlyList<RowReplayGuard> undoGuards = afterRows
                .Select(row => RowReplayGuard.Present(Table, row))
                .ToArray();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceApplyResult(
                affected,
                $"Applied the recovery preset to {affected} rider row(s).",
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

    private static async Task<List<RiderRecoveryChange>> ReadChangesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = ids.Select((id, index) => (id, Name: $"$id{index}")).ToArray();
        command.CommandText = $@"
SELECT IDcyclist, value_f_FIT, value_f_injury, value_i_injury_num_days,
       value_f_fat_phy, value_f_freshness, value_f_prepa
FROM DYN_cyclist_fitness
WHERE IDcyclist IN ({string.Join(", ", parameters.Select(static parameter => parameter.Name))})
ORDER BY IDcyclist";
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.id);
        }

        var changes = new List<RiderRecoveryChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new RiderRecoveryChange(
                reader.GetInt64(0),
                new RiderRecoveryValues(
                    Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture)),
                RiderRecoveryValues.Default));
        }

        return changes;
    }

    private static string ComputeToken(
        IEnumerable<long> requestedIds,
        IEnumerable<RiderRecoveryChange> changes)
    {
        var values = requestedIds.Select(id => $"requested:{MaintenanceSupport.CanonicalNumber(id)}")
            .Concat(changes.Select(change => string.Join(':',
                MaintenanceSupport.CanonicalNumber(change.CyclistId),
                MaintenanceSupport.CanonicalNumber(change.OldValues.Fit),
                MaintenanceSupport.CanonicalNumber(change.OldValues.Injury),
                MaintenanceSupport.CanonicalNumber(change.OldValues.InjuryDays),
                MaintenanceSupport.CanonicalNumber(change.OldValues.PhysicalFatigue),
                MaintenanceSupport.CanonicalNumber(change.OldValues.Freshness),
                MaintenanceSupport.CanonicalNumber(change.OldValues.Preparation))));
        return MaintenanceSupport.ComputeToken(values);
    }

    private static void AddPresetParameters(SqliteCommand command, long cyclistId)
    {
        var preset = RiderRecoveryValues.Default;
        command.Parameters.AddWithValue("$fit", preset.Fit);
        command.Parameters.AddWithValue("$injury", preset.Injury);
        command.Parameters.AddWithValue("$injuryDays", preset.InjuryDays);
        command.Parameters.AddWithValue("$fatigue", preset.PhysicalFatigue);
        command.Parameters.AddWithValue("$freshness", preset.Freshness);
        command.Parameters.AddWithValue("$preparation", preset.Preparation);
        command.Parameters.AddWithValue("$cyclistId", cyclistId);
    }

    private static MaintenanceEditOperation CreateHistoryOperation(
        IReadOnlyList<TypedRow> beforeRows,
        IReadOnlyList<TypedRow> afterRows)
    {
        Dictionary<string, TypedRow> afterByIdentity = afterRows.ToDictionary(
            static row => row.Identity?.ToString()
                ?? throw new InvalidDataException("A rider history row has no identity."),
            StringComparer.Ordinal);
        string[] columns =
        [
            "value_f_FIT",
            "value_f_injury",
            "value_i_injury_num_days",
            "value_f_fat_phy",
            "value_f_freshness",
            "value_f_prepa"
        ];
        var changes = new List<MaintenanceRowChange>(beforeRows.Count);
        foreach (TypedRow before in beforeRows)
        {
            string key = before.Identity?.ToString()
                ?? throw new InvalidDataException("A rider history row has no identity.");
            if (!afterByIdentity.TryGetValue(key, out TypedRow? after))
            {
                throw new DBConcurrencyException("A rider row disappeared while history was captured.");
            }

            changes.Add(new MaintenanceRowChange(
                Table,
                before.Identity!,
                columns.Select(column => KeyValuePair.Create(column, before.Values[column])),
                columns.Select(column => KeyValuePair.Create(column, after.Values[column]))));
        }

        return new MaintenanceEditOperation(
            Guid.NewGuid(),
            Table,
            DateTimeOffset.UtcNow,
            MaintenanceToolKind.RiderRecovery,
            "Rider recovery preset",
            changes);
    }
}
