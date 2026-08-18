using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

public sealed class CountryQuotaMaintenanceService : ICountryQuotaMaintenanceService
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Requirements =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GAM_config"] = new[] { "gene_i_date" },
            ["DYN_result_season"] = new[]
            {
                "fkIDstage", "fkIDcyclist", "fkIDresult_season_team", "gene_i_rank_stage_time",
                "gene_i_rank_race_time", "gene_i_rank_race_mountain", "gene_i_rank_race_points"
            },
            ["DYN_result_season_stage"] = new[] { "IDresult_season_stage", "gene_b_isFinalStage", "gene_b_isTTT" },
            ["DYN_cyclist"] = new[] { "IDcyclist", "fkIDregion" },
            ["STA_region"] = new[] { "IDregion", "fkIDcountry" },
            ["STA_country"] = new[]
            {
                "IDcountry", "CONSTANT", "fkIDcontinent", "gene_i_num_cyclist_WC",
                "gene_i_num_cyclist_WC_ITT", "gene_i_num_cyclist_EC", "gene_i_num_cyclist_EC_ITT"
            },
            ["STA_continent"] = new[] { "IDcontinent", "CONSTANT" },
            ["STA_stage"] = new[] { "IDstage", "fkIDrace", "gene_i_stage_number" },
            ["STA_race"] = new[] { "IDrace", "fkIDrace_class" },
            ["STA_race_class"] = new[] { "IDrace_class", "CONSTANT" },
            ["STA_race_bonus"] = new[]
            {
                "fkIDrace_class", "fkIDclassification_source", "fkIDclassification_type", "gene_ilist_bonus"
            },
            ["STA_classification_source"] = new[] { "IDclassification_source_cym5", "CONSTANT" },
            ["STA_classification_type"] = new[] { "IDclassification_type_cym5", "CONSTANT" }
        };

    public Task<MaintenanceCapability> CheckCapabilityAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        MaintenanceSupport.CheckAsync(
            sqlitePath,
            MaintenanceToolKind.CountryChampionshipQuota,
            Requirements,
            ["STA_country"],
            cancellationToken);

    public Task<CountryQuotaPreview> PreviewAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PreviewCoreAsync(sqlitePath, cancellationToken),
            cancellationToken);

    private async Task<CountryQuotaPreview> PreviewCoreAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        var capability = await CheckCapabilityAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        var date = await SqliteSupport.ReadSingleGameDateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        EnsureNovember(date);
        return await BuildPreviewAsync(connection, null, date, cancellationToken).ConfigureAwait(false);
    }

    public Task<MaintenanceApplyResult> ApplyAsync(
        string sqlitePath,
        CountryQuotaPreview preview,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ApplyCoreAsync(sqlitePath, preview, cancellationToken),
            cancellationToken);

    private async Task<MaintenanceApplyResult> ApplyCoreAsync(
        string sqlitePath,
        CountryQuotaPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var capability = await CheckCapabilityAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        TableSchema historyTable = MaintenanceHistoryCapture.RequireEditableTable(catalog, "STA_country");
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
            EnsureNovember(date);
            var current = await BuildPreviewAsync(connection, sqliteTransaction, date, cancellationToken)
                .ConfigureAwait(false);
            if (!current.SnapshotToken.Equals(preview.SnapshotToken, StringComparison.Ordinal))
            {
                throw new DBConcurrencyException("Country results or quotas changed after the preview was generated.");
            }

            CountryQuotaChange[] changed = current.Changes
                .Where(static change => change.OldValues != change.NewValues)
                .ToArray();
            if (changed.Length == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MaintenanceApplyResult(0, "Country quotas already match the calculated allocation.");
            }

            IReadOnlyList<TypedRow> beforeRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection,
                    sqliteTransaction,
                    historyTable,
                    "IDcountry",
                    changed.Select(static change => change.CountryId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            var affected = 0;
            foreach (CountryQuotaChange change in changed)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = sqliteTransaction;
                command.CommandText = @"
UPDATE STA_country
SET gene_i_num_cyclist_WC = $worldRoad,
    gene_i_num_cyclist_WC_ITT = $worldTimeTrial,
    gene_i_num_cyclist_EC = $europeanRoad,
    gene_i_num_cyclist_EC_ITT = $europeanTimeTrial
WHERE IDcountry = $countryId";
                command.Parameters.AddWithValue("$worldRoad", change.NewValues.WorldRoad);
                command.Parameters.AddWithValue("$worldTimeTrial", change.NewValues.WorldTimeTrial);
                command.Parameters.AddWithValue("$europeanRoad", change.NewValues.EuropeanRoad);
                command.Parameters.AddWithValue("$europeanTimeTrial", change.NewValues.EuropeanTimeTrial);
                command.Parameters.AddWithValue("$countryId", change.CountryId);
                affected += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected != changed.Length)
            {
                throw new DBConcurrencyException("One or more country rows disappeared while quotas were being updated.");
            }

            IReadOnlyList<TypedRow> afterRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection,
                    sqliteTransaction,
                    historyTable,
                    "IDcountry",
                    changed.Select(static change => change.CountryId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (beforeRows.Count != affected || afterRows.Count != affected)
            {
                throw new DBConcurrencyException("One or more country rows disappeared while quota history was captured.");
            }

            MaintenanceEditOperation historyOperation = CreateHistoryOperation(beforeRows, afterRows);
            IReadOnlyList<RowReplayGuard> undoGuards = afterRows
                .Select(row => RowReplayGuard.Present("STA_country", row))
                .ToArray();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceApplyResult(
                affected,
                $"Updated {affected} country quota row(s).",
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

    private static async Task<CountryQuotaPreview> BuildPreviewAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var bonuses = await LoadBonusesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (bonuses.Count == 0)
        {
            throw new InvalidDataException("No usable UCI point scales were found.");
        }

        var resultRows = await LoadResultsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var points = CalculateCountryPoints(resultRows, bonuses);
        if (!points.Values.Any(static value => value > 0))
        {
            throw new InvalidDataException("No positive current-season UCI points could be calculated; no quotas were changed.");
        }

        var countries = await LoadCountriesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var rankedWorld = RankPositive(countries, points);
        var rankedEurope = RankPositive(
            countries.Where(static country => country.Continent.Equals("Europa", StringComparison.OrdinalIgnoreCase)),
            points);
        var worldRankById = rankedWorld.Select((country, index) => (country.Id, Rank: index + 1))
            .ToDictionary(static item => item.Id, static item => item.Rank);
        var europeanRankById = rankedEurope.Select((country, index) => (country.Id, Rank: index + 1))
            .ToDictionary(static item => item.Id, static item => item.Rank);
        var changes = new List<CountryQuotaChange>();
        foreach (var country in countries.OrderBy(static country => country.Id))
        {
            var worldRank = worldRankById.GetValueOrDefault(country.Id);
            var europeRank = europeanRankById.TryGetValue(country.Id, out var value) ? value : (int?)null;
            var next = Combine(WorldQuota(worldRank), EuropeanQuota(europeRank));
            var canonical = CountryCodeAliases.Canonicalize(country.RawCode);
            changes.Add(new CountryQuotaChange(
                country.Id,
                country.RawCode,
                canonical,
                canonical.Equals(country.RawCode, StringComparison.OrdinalIgnoreCase)
                    ? country.RawCode
                    : $"{canonical} ({country.RawCode})",
                points.GetValueOrDefault(country.Id),
                worldRank,
                europeRank,
                country.Current,
                next));
        }

        var tokenValues = new List<string>
        {
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        };
        tokenValues.AddRange(bonuses
            .OrderBy(static item => item.Key.RaceClass, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.Source, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.Type, StringComparer.Ordinal)
            .Select(static item => $"bonus:{item.Key.RaceClass}:{item.Key.Source}:{item.Key.Type}:{string.Join(',', item.Value)}"));
        tokenValues.AddRange(resultRows
            .OrderBy(static row => row.StageId)
            .ThenBy(static row => row.CountryId)
            .ThenBy(static row => row.TeamResultId)
            .ThenBy(static row => row.StageRank)
            .Select(CanonicalResult));
        tokenValues.AddRange(changes.Select(CanonicalChange));
        var token = MaintenanceSupport.ComputeToken(tokenValues);
        return new CountryQuotaPreview(
            token,
            date,
            changes,
            worldRankById.Count(static item => item.Value <= 25),
            europeanRankById.Count(static item => item.Value <= 18));
    }

    private static async Task<Dictionary<(string RaceClass, string Source, string Type), int[]>> LoadBonusesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT rc.CONSTANT, cs.CONSTANT, ct.CONSTANT, rb.gene_ilist_bonus
FROM STA_race_bonus rb
INNER JOIN STA_race_class rc ON rb.fkIDrace_class = rc.IDrace_class
INNER JOIN STA_classification_source cs ON rb.fkIDclassification_source = cs.IDclassification_source_cym5
INNER JOIN STA_classification_type ct ON rb.fkIDclassification_type = ct.IDclassification_type_cym5
WHERE rb.gene_ilist_bonus IS NOT NULL AND rb.gene_ilist_bonus <> '()'";
        var bonuses = new Dictionary<(string, string, string), int[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                throw new InvalidDataException("A championship-points scale has incomplete classification metadata.");
            }

            var key = (
                Normalize(reader.GetString(0)),
                Normalize(reader.GetString(1)),
                Normalize(reader.GetString(2)));
            if (key.Item1.Length == 0 || key.Item2.Length == 0 || key.Item3.Length == 0)
            {
                throw new InvalidDataException("A championship-points scale has an empty classification key.");
            }

            int[] scale = ParseBonusList(reader.GetString(3));
            if (!bonuses.TryAdd(key, scale))
            {
                throw new InvalidDataException("Championship-points scales contain a duplicate classification key.");
            }
        }

        return bonuses;
    }

    private static async Task<List<ResultRow>> LoadResultsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT result.fkIDstage, result.fkIDresult_season_team,
       result.gene_i_rank_stage_time, result.gene_i_rank_race_time,
       result.gene_i_rank_race_mountain, result.gene_i_rank_race_points,
       stage.gene_i_stage_number,
       COALESCE(stage_result.gene_b_isFinalStage, 0),
       COALESCE(stage_result.gene_b_isTTT, 0),
       race_class.CONSTANT, country.IDcountry
FROM DYN_result_season result
INNER JOIN STA_stage stage ON stage.IDstage = result.fkIDstage
INNER JOIN STA_race race ON race.IDrace = stage.fkIDrace
INNER JOIN STA_race_class race_class ON race_class.IDrace_class = race.fkIDrace_class
INNER JOIN DYN_cyclist cyclist ON cyclist.IDcyclist = result.fkIDcyclist
INNER JOIN STA_region region ON region.IDregion = cyclist.fkIDregion
INNER JOIN STA_country country ON country.IDcountry = region.fkIDcountry
LEFT JOIN DYN_result_season_stage stage_result ON stage_result.IDresult_season_stage = result.fkIDstage";
        var rows = new List<ResultRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(6) || reader.IsDBNull(9) || reader.IsDBNull(10))
            {
                continue;
            }

            rows.Add(new ResultRow(
                reader.GetInt64(0), ReadNullableLong(reader, 1), ReadNullableInt(reader, 2),
                ReadNullableInt(reader, 3), ReadNullableInt(reader, 4), ReadNullableInt(reader, 5),
                reader.GetInt32(6), reader.GetInt32(7) == 1, reader.GetInt32(8) == 1,
                Normalize(reader.GetString(9)), reader.GetInt64(10)));
        }

        return rows;
    }

    private static Dictionary<long, double> CalculateCountryPoints(
        IReadOnlyList<ResultRow> rows,
        IReadOnlyDictionary<(string RaceClass, string Source, string Type), int[]> bonuses)
    {
        var teamCounts = rows.Where(static row => row.TeamResultId.HasValue)
            .GroupBy(static row => (row.StageId, row.TeamResultId!.Value))
            .ToDictionary(static group => group.Key, static group => group.Count());
        var totals = new Dictionary<long, double>();
        foreach (var row in rows)
        {
            var points = 0D;
            if (row.IsFinal && row.StageNumber == 1)
            {
                points += ResolvePoints(bonuses, row.RaceClass, "RACE_FINAL", "TIME", row.StageRank);
            }
            else if (row.IsFinal && row.StageNumber > 1)
            {
                points += ResolvePoints(bonuses, row.RaceClass, "RACE_FINAL", "TIME", row.GeneralRank);
                points += ResolvePoints(bonuses, row.RaceClass, "RACE_FINAL", "MOUNTAIN", row.MountainRank);
                points += ResolvePoints(bonuses, row.RaceClass, "RACE_FINAL", "POINTS", row.PointsRank);
            }
            else
            {
                points += ResolvePoints(bonuses, row.RaceClass, "RACE", "TIME", row.GeneralRank);
            }

            var stagePoints = ResolvePoints(bonuses, row.RaceClass, "STAGE", "TIME", row.StageRank);
            if (row.IsTtt && row.TeamResultId.HasValue
                && teamCounts.TryGetValue((row.StageId, row.TeamResultId.Value), out var count)
                && count > 0)
            {
                stagePoints = Math.Round(stagePoints / count, 2, MidpointRounding.ToEven);
            }

            points += stagePoints;
            if (points > 0)
            {
                totals[row.CountryId] = totals.GetValueOrDefault(row.CountryId) + points;
            }
        }

        // Keep only strictly positive aggregates; zero-point countries are intentionally
        // ranked as unqualified by the caller rather than materialized in this map.
        return totals;
    }

    private static async Task<List<CountryRow>> LoadCountriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string countryProjection = """
            SELECT c.IDcountry AS country_id,
                   COALESCE(c.CONSTANT, '') AS raw_code,
                   COALESCE(ct.CONSTANT, '') AS continent_code,
                   COALESCE(c.gene_i_num_cyclist_WC, 0) AS world_road,
                   COALESCE(c.gene_i_num_cyclist_WC_ITT, 0) AS world_time_trial,
                   COALESCE(c.gene_i_num_cyclist_EC, 0) AS european_road,
                   COALESCE(c.gene_i_num_cyclist_EC_ITT, 0) AS european_time_trial
            FROM STA_country AS c
            LEFT JOIN STA_continent AS ct ON ct.IDcontinent = c.fkIDcontinent
            """;

        await using SqliteCommand countryCommand = connection.CreateCommand();
        countryCommand.Transaction = transaction;
        countryCommand.CommandText = countryProjection;
        var result = new List<CountryRow>();
        await using SqliteDataReader countryReader = await countryCommand
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await countryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadCountry(countryReader));
        }

        return result;
    }

    private static CountryRow ReadCountry(SqliteDataReader reader) =>
        new(
            reader.GetInt64(reader.GetOrdinal("country_id")),
            reader.GetString(reader.GetOrdinal("raw_code")),
            reader.GetString(reader.GetOrdinal("continent_code")).Trim(),
            new CountryQuotaValues(
                reader.GetInt64(reader.GetOrdinal("world_road")),
                reader.GetInt64(reader.GetOrdinal("world_time_trial")),
                reader.GetInt64(reader.GetOrdinal("european_road")),
                reader.GetInt64(reader.GetOrdinal("european_time_trial"))));

    private static List<CountryRow> RankPositive(IEnumerable<CountryRow> countries, IReadOnlyDictionary<long, double> points) =>
        countries.Where(country => points.GetValueOrDefault(country.Id) > 0)
            .OrderByDescending(country => points.GetValueOrDefault(country.Id))
            .ThenBy(static country => country.RawCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static country => country.Id)
            .ToList();

    private static double ResolvePoints(
        IReadOnlyDictionary<(string, string, string), int[]> bonuses,
        string raceClass,
        string source,
        string type,
        int? rank)
    {
        if (!IsScoringRank(rank))
        {
            return 0;
        }

        return bonuses.TryGetValue((raceClass, source, type), out int[]? scale)
            && rank!.Value <= scale.Length
                ? scale[rank.Value - 1]
                : 0;
    }

    private static bool IsScoringRank(int? rank) =>
        rank is > 0 and not 252;

    private static int[] ParseBonusList(string value)
    {
        var text = value.Trim();
        bool startsWithParenthesis = text.StartsWith('(');
        bool endsWithParenthesis = text.EndsWith(')');
        if (startsWithParenthesis != endsWithParenthesis)
        {
            throw new InvalidDataException("A championship-points scale has unbalanced parentheses.");
        }

        if (startsWithParenthesis)
        {
            text = text[1..^1];
        }

        var values = new List<int>();
        foreach (string token in text.Split(','))
        {
            string trimmedToken = token.Trim();
            if (trimmedToken.Length == 0
                || !int.TryParse(
                    trimmedToken,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed)
                || parsed < 0)
            {
                throw new InvalidDataException("A championship-points scale contains an invalid award token.");
            }

            values.Add(parsed);
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException("A championship-points scale is empty.");
        }

        return values.ToArray();
    }

    private static CountryQuotaValues WorldQuota(int rank) => rank switch
    {
        >= 1 and <= 10 => new CountryQuotaValues(8, 2, 0, 0),
        >= 11 and <= 19 => new CountryQuotaValues(6, 2, 0, 0),
        >= 20 and <= 25 => new CountryQuotaValues(4, 2, 0, 0),
        _ => new CountryQuotaValues(0, 0, 0, 0)
    };

    private static CountryQuotaValues EuropeanQuota(int? rank) => rank switch
    {
        >= 1 and <= 10 => new CountryQuotaValues(0, 0, 8, 2),
        >= 11 and <= 18 => new CountryQuotaValues(0, 0, 6, 2),
        _ => new CountryQuotaValues(0, 0, 0, 0)
    };

    private static CountryQuotaValues Combine(CountryQuotaValues world, CountryQuotaValues europe) =>
        new(world.WorldRoad, world.WorldTimeTrial, europe.EuropeanRoad, europe.EuropeanTimeTrial);

    private static MaintenanceEditOperation CreateHistoryOperation(
        IReadOnlyList<TypedRow> beforeRows,
        IReadOnlyList<TypedRow> afterRows)
    {
        Dictionary<string, TypedRow> afterByIdentity = afterRows.ToDictionary(
            static row => row.Identity?.ToString()
                ?? throw new InvalidDataException("A country history row has no identity."),
            StringComparer.Ordinal);
        string[] columns =
        [
            "gene_i_num_cyclist_WC",
            "gene_i_num_cyclist_WC_ITT",
            "gene_i_num_cyclist_EC",
            "gene_i_num_cyclist_EC_ITT"
        ];
        var changes = new List<MaintenanceRowChange>(beforeRows.Count);
        foreach (TypedRow before in beforeRows)
        {
            string key = before.Identity?.ToString()
                ?? throw new InvalidDataException("A country history row has no identity.");
            if (!afterByIdentity.TryGetValue(key, out TypedRow? after))
            {
                throw new DBConcurrencyException("A country row disappeared while history was captured.");
            }

            changes.Add(new MaintenanceRowChange(
                "STA_country",
                before.Identity!,
                columns.Select(column => KeyValuePair.Create(column, before.Values[column])),
                columns.Select(column => KeyValuePair.Create(column, after.Values[column]))));
        }

        return new MaintenanceEditOperation(
            Guid.NewGuid(),
            "STA_country",
            DateTimeOffset.UtcNow,
            MaintenanceToolKind.CountryChampionshipQuota,
            "World and European championship quotas",
            changes);
    }

    private static string CanonicalChange(CountryQuotaChange change) => string.Join(':',
        change.CountryId,
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(change.RawCode)),
        MaintenanceSupport.CanonicalNumber(change.UciPoints),
        change.WorldRank,
        change.EuropeanRank?.ToString(CultureInfo.InvariantCulture) ?? "N",
        change.OldValues.WorldRoad, change.OldValues.WorldTimeTrial,
        change.OldValues.EuropeanRoad, change.OldValues.EuropeanTimeTrial,
        change.NewValues.WorldRoad, change.NewValues.WorldTimeTrial,
        change.NewValues.EuropeanRoad, change.NewValues.EuropeanTimeTrial);

    private static string CanonicalResult(ResultRow row) => string.Join(':',
        "result",
        row.StageId,
        row.TeamResultId?.ToString(CultureInfo.InvariantCulture) ?? "N",
        row.StageRank?.ToString(CultureInfo.InvariantCulture) ?? "N",
        row.GeneralRank?.ToString(CultureInfo.InvariantCulture) ?? "N",
        row.MountainRank?.ToString(CultureInfo.InvariantCulture) ?? "N",
        row.PointsRank?.ToString(CultureInfo.InvariantCulture) ?? "N",
        row.StageNumber,
        row.IsFinal ? 1 : 0,
        row.IsTtt ? 1 : 0,
        row.RaceClass,
        row.CountryId);

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static int? ReadNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? ReadNullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static void EnsureNovember(DateOnly date)
    {
        if (date.Month != 11)
        {
            throw new InvalidOperationException(
                $"Country quotas can be maintained only during November; the in-game date is {date:yyyy-MM-dd}.");
        }
    }

    private sealed record ResultRow(
        long StageId,
        long? TeamResultId,
        int? StageRank,
        int? GeneralRank,
        int? MountainRank,
        int? PointsRank,
        int StageNumber,
        bool IsFinal,
        bool IsTtt,
        string RaceClass,
        long CountryId);

    private sealed record CountryRow(long Id, string RawCode, string Continent, CountryQuotaValues Current);
}
