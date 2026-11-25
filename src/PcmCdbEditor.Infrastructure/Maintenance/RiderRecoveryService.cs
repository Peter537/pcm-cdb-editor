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
    private const string CyclistTable = "DYN_cyclist";
    private const string TeamTable = "DYN_team";
    private static readonly string[] TeamDisplayColumns =
        ["gene_sz_name", "gene_sz_shortname", "gene_sz_abbreviation", "CONSTANT", "name"];
    private static readonly string[] RiderDisplayColumns =
        ["gene_sz_firstlastname", "gene_sz_name", "gene_sz_full_name", "gene_sz_firstname", "gene_sz_lastname", "name"];
    private static readonly string[] PresetColumns =
    [
        "value_f_FIT", "value_f_injury", "value_i_injury_num_days",
        "value_f_fat_phy", "value_f_freshness", "value_f_prepa"
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Requirements =
        new Dictionary<string, IReadOnlyCollection<string>>(SqliteIdentifierComparer.Instance)
        {
            [Table] = new[] { "IDcyclist" }.Concat(PresetColumns).ToArray()
        };

    private readonly Action<long>? _afterRowUpdate;

    public RiderRecoveryService()
    {
    }

    internal RiderRecoveryService(Action<long> afterRowUpdate)
    {
        ArgumentNullException.ThrowIfNull(afterRowUpdate);
        _afterRowUpdate = afterRowUpdate;
    }

    public Task<MaintenanceCapability> CheckCapabilityAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        MaintenanceSupport.CheckAsync(sqlitePath, CheckCapability, cancellationToken);

    private static MaintenanceCapability CheckCapability(DatabaseSchemaCatalog catalog) =>
        MaintenanceSupport.Check(catalog, MaintenanceToolKind.RiderRecovery, Requirements, [Table]);

    public Task<IReadOnlyList<RiderTeamOption>> ListTeamsAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ListTeamsCoreAsync(sqlitePath, cancellationToken),
            cancellationToken);

    private static async Task<IReadOnlyList<RiderTeamOption>> ListTeamsCoreAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await RequireTeamSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        HashSet<string> teamColumns = await SqliteSupport.ReadColumnNamesAsync(
                connection,
                TeamTable,
                cancellationToken)
            .ConfigureAwait(false);
        string[] displayColumns = TeamDisplayColumns.Where(teamColumns.Contains)
            .Select(column => SqliteRelationshipSql.BuildLabelExpression(column, null, "team."))
            .ToArray();
        string displayExpression = displayColumns.Length == 0
            ? "''"
            : $"COALESCE({string.Join(", ", displayColumns)}, '')";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT team.IDteam,
                   {displayExpression},
                   COUNT(DISTINCT cyclist.IDcyclist)
            FROM {SqliteSupport.QuoteIdentifier(TeamTable)} team
            LEFT JOIN {SqliteSupport.QuoteIdentifier(CyclistTable)} cyclist
              ON cyclist.fkIDteam = team.IDteam AND cyclist.IDcyclist > 0
            WHERE team.IDteam > 0
            GROUP BY team.IDteam, {displayExpression}
            ORDER BY {displayExpression} COLLATE NOCASE, team.IDteam
            """;
        var options = new List<RiderTeamOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long teamId = reader.GetInt64(0);
            string name = reader.GetString(1).Trim();
            options.Add(new RiderTeamOption(
                teamId,
                name,
                reader.GetInt64(2)));
        }

        return options.AsReadOnly();
    }

    public Task<RiderRecoveryPreview> PreviewAsync(
        string sqlitePath,
        RiderRecoveryTarget target,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PreviewCoreAsync(sqlitePath, target, cancellationToken),
            cancellationToken);

    public Task<RiderRecoveryPreview> PreviewAsync(
        string sqlitePath,
        IReadOnlyCollection<long> cyclistIds,
        CancellationToken cancellationToken) =>
        PreviewAsync(
            sqlitePath,
            RiderRecoveryTarget.ForRiderIds(cyclistIds),
            cancellationToken);

    private async Task<RiderRecoveryPreview> PreviewCoreAsync(
        string sqlitePath,
        RiderRecoveryTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var capability = await CheckCapabilityAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        long[]? resolved = await ResolveTargetIdsAsync(
                connection,
                transaction: null,
                target,
                cancellationToken)
            .ConfigureAwait(false);
        long[] ids = resolved ?? [];
        List<RiderRecoveryChange> changes = ids.Length == 0
            ? []
            : await ReadChangesAsync(connection, transaction: null, ids, cancellationToken).ConfigureAwait(false);
        var names = await ReadRiderNamesAsync(connection, ids, cancellationToken).ConfigureAwait(false);
        return new RiderRecoveryPreview(ComputeToken(target, ids, changes), target, ids, changes,
            targetTeamMissing: resolved is null, riderNames: names);
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
        DatabaseSchemaCatalog catalog = await MaintenanceSupport.RequireCatalogAsync(sqlitePath, CheckCapability, cancellationToken)
            .ConfigureAwait(false);
        if (preview.CyclistIds.Count == 0)
        {
            return new MaintenanceApplyResult(0, preview.NoChangeSummary ?? "No rider IDs were supplied.");
        }

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
            long[]? resolvedIds = await ResolveTargetIdsAsync(
                    connection,
                    sqliteTransaction,
                    preview.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolvedIds is null || !resolvedIds.SequenceEqual(preview.CyclistIds))
            {
                throw new DBConcurrencyException(resolvedIds is null
                    ? "The selected team was removed after the preview was generated."
                    : "The selected team roster changed after the preview was generated.");
            }

            List<RiderRecoveryChange> current;
            try
            {
                current = await ReadChangesAsync(connection, sqliteTransaction, preview.CyclistIds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                throw new DBConcurrencyException(exception.Message, exception);
            }
            if (!ComputeToken(preview.Target, preview.CyclistIds, current)
                .Equals(preview.SnapshotToken, StringComparison.Ordinal))
            {
                throw new DBConcurrencyException("Rider fitness changed after the preview was generated.");
            }

            long[] changedIds = current
                .Where(static change => change.OldValues != change.NewValues)
                .Select(static change => change.CyclistId)
                .ToArray();
            if (changedIds.Length == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MaintenanceApplyResult(0, preview.NoChangeSummary!);
            }

            await SqliteMutationSafety.EnsureUpdateIsReversibleAsync(
                    connection, sqliteTransaction, Table, PresetColumns, cancellationToken)
                .ConfigureAwait(false);
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
UPDATE OR ABORT DYN_cyclist_fitness
SET value_f_FIT = $fit,
    value_f_injury = $injury,
    value_i_injury_num_days = $injuryDays,
    value_f_fat_phy = $fatigue,
    value_f_freshness = $freshness,
    value_f_prepa = $preparation
WHERE IDcyclist = $cyclistId";
                AddPresetParameters(command, change.CyclistId);
                affected += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _afterRowUpdate?.Invoke(change.CyclistId);
            }

            if (affected != changedIds.Length)
            {
                throw new DBConcurrencyException("The preset did not update every reviewed fitness row exactly once.");
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
                $"Applied the recovery preset to {affected:N0} rider {(affected == 1 ? "row" : "rows")}.",
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
        string parameters = AddIdParameters(command, ids);
        command.CommandText = $@"
SELECT IDcyclist, value_f_FIT, value_f_injury, value_i_injury_num_days,
       value_f_fat_phy, value_f_freshness, value_f_prepa
FROM DYN_cyclist_fitness
WHERE IDcyclist IN ({parameters})
ORDER BY IDcyclist";

        var changes = new List<RiderRecoveryChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long cyclistId = reader.GetInt64(0);
            changes.Add(new RiderRecoveryChange(
                cyclistId,
                new RiderRecoveryValues(
                    ReadFitnessNumber(reader, 1, cyclistId),
                    ReadFitnessNumber(reader, 2, cyclistId),
                    ReadInjuryDays(reader, 3, cyclistId),
                    ReadFitnessNumber(reader, 4, cyclistId),
                    ReadFitnessNumber(reader, 5, cyclistId),
                    ReadFitnessNumber(reader, 6, cyclistId)),
                RiderRecoveryValues.Default));
        }

        return changes;
    }

    private static double ReadFitnessNumber(SqliteDataReader reader, int ordinal, long cyclistId) =>
        reader.GetValue(ordinal) switch
        {
            long value when (Int128)(double)value == value => value,
            double value when double.IsFinite(value) => value,
            _ => throw InvalidFitnessValue(reader, ordinal, cyclistId, "a finite SQLite number")
        };

    private static long ReadInjuryDays(SqliteDataReader reader, int ordinal, long cyclistId) =>
        reader.GetValue(ordinal) is long value
            ? value
            : throw InvalidFitnessValue(reader, ordinal, cyclistId, "a SQLite integer");

    private static InvalidDataException InvalidFitnessValue(
        SqliteDataReader reader,
        int ordinal,
        long cyclistId,
        string expected) =>
        new($"Rider recovery cannot read {Table}.{reader.GetName(ordinal)} for rider ID {cyclistId}. Expected {expected}.");

    private static async Task<Dictionary<long, string>> ReadRiderNamesAsync(
        SqliteConnection connection,
        long[] ids,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<long, string>();
        if (ids.Length == 0)
        {
            return names;
        }

        HashSet<string> columns = await SqliteSupport.ReadColumnNamesAsync(connection, CyclistTable, cancellationToken)
            .ConfigureAwait(false);
        List<string> labels = RiderDisplayColumns.Where(columns.Contains)
            .Select(column => SqliteRelationshipSql.BuildLabelExpression(column, null))
            .ToList();
        if (labels.Count == 0 || !columns.Contains("IDcyclist"))
        {
            return names;
        }

        if (columns.Contains("gene_sz_firstname") && columns.Contains("gene_sz_lastname"))
        {
            labels.Insert(0, SqliteRelationshipSql.BuildLabelExpression("gene_sz_firstname", "gene_sz_lastname"));
        }
        string label = labels.Count == 1 ? labels[0] : $"COALESCE({string.Join(", ", labels)})";
        await using var command = connection.CreateCommand();
        string parameters = AddIdParameters(command, ids);
        command.CommandText = $"""
            SELECT IDcyclist, {label}
            FROM {SqliteSupport.QuoteIdentifier(CyclistTable)}
            WHERE IDcyclist IN ({parameters})
            GROUP BY IDcyclist
            HAVING COUNT(*) = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1) && long.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out long id))
            {
                names.TryAdd(id, reader.GetString(1));
            }
        }

        return names;
    }

    private static string AddIdParameters(SqliteCommand command, IEnumerable<long> ids)
    {
        var names = new List<string>();
        foreach (long id in ids)
        {
            string name = $"$id{names.Count}";
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return string.Join(", ", names);
    }

    private static string ComputeToken(
        RiderRecoveryTarget target,
        IEnumerable<long> requestedIds,
        IEnumerable<RiderRecoveryChange> changes)
    {
        var values = new[]
            {
                $"target:{target.Kind}",
                $"team:{(target.TeamId.HasValue ? MaintenanceSupport.CanonicalNumber(target.TeamId.Value) : "none")}"
            }
            .Concat(requestedIds.Select(id => $"requested:{MaintenanceSupport.CanonicalNumber(id)}"))
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

    private static async Task<long[]?> ResolveTargetIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RiderRecoveryTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind == RiderRecoveryTargetKind.RiderIds)
        {
            return target.RiderIds.ToArray();
        }

        await RequireTeamSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$teamId", target.TeamId!.Value);
        command.CommandText = $"SELECT COUNT(*) FROM {SqliteSupport.QuoteIdentifier(TeamTable)} WHERE IDteam = $teamId";
        var teamMatches = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (teamMatches == 0)
        {
            return null;
        }
        if (teamMatches > 1)
        {
            throw new DBConcurrencyException("The selected team ID matches more than one team row.");
        }

        command.CommandText = $"""
            SELECT IDcyclist
            FROM {SqliteSupport.QuoteIdentifier(CyclistTable)}
            WHERE fkIDteam = $teamId
              AND IDcyclist > 0
            ORDER BY IDcyclist
            """;
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.Distinct().Order().ToArray();
    }

    private static async Task RequireTeamSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> tables = await SqliteSupport.ReadTableNamesAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (!tables.Contains(CyclistTable) || !tables.Contains(TeamTable))
        {
            throw new InvalidOperationException(
                "Team selection is unavailable because DYN_cyclist or DYN_team is missing. Rider IDs can still be used.");
        }

        HashSet<string> cyclistColumns = await SqliteSupport.ReadColumnNamesAsync(
                connection,
                CyclistTable,
                cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> teamColumns = await SqliteSupport.ReadColumnNamesAsync(
                connection,
                TeamTable,
                cancellationToken)
            .ConfigureAwait(false);
        if (!cyclistColumns.Contains("IDcyclist") ||
            !cyclistColumns.Contains("fkIDteam") ||
            !teamColumns.Contains("IDteam"))
        {
            throw new InvalidOperationException(
                "Team selection is unavailable because the rider/team lookup columns are missing. Rider IDs can still be used.");
        }
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
                PresetColumns.Select(column => KeyValuePair.Create(column, before.Values[column])),
                PresetColumns.Select(column => KeyValuePair.Create(column, after.Values[column]))));
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
