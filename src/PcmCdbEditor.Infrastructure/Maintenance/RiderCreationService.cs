using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PcmCdbEditor.Application;
using PcmCdbEditor.Domain;
using PcmCdbEditor.Infrastructure.Sqlite;

namespace PcmCdbEditor.Infrastructure.Maintenance;

public sealed class RiderCreationService : IRiderCreationService
{
    private const string RiderTable = "DYN_cyclist";
    private const string ContractTable = "DYN_contract_cyclist";
    private const string TeamTable = "DYN_team";
    private const string RegionTable = "STA_region";
    private const string CountryTable = "STA_country";
    private const string RaceTable = "STA_race";
    private const string RaceClassTable = "STA_race_class";
    private const string RiderTypeTable = "STA_type_rider";
    private const string RiderStateTable = "STA_cyclist_state";
    private const string ConfigTable = "GAM_config";
    private const string PreferenceTable = "INF_contract_preference_preset";
    private const string PreferenceColumn = "fkIDcontract_preference_preset";
    private const string RiderIdentity = "IDcyclist";
    private const string ContractIdentity = "IDcontract_cyclist";

    private static readonly RiderAbilityDefinition[] AbilityDefinitions =
    [
        Ability("plain", "Plain"),
        Ability("mountain", "Mountain"),
        Ability("medium_mountain", "Medium mountain"),
        Ability("downhilling", "Downhill"),
        Ability("cobble", "Cobble"),
        Ability("timetrial", "Time trial"),
        Ability("prologue", "Prologue"),
        Ability("sprint", "Sprint"),
        Ability("acceleration", "Acceleration"),
        Ability("endurance", "Endurance"),
        Ability("resistance", "Resistance"),
        Ability("recuperation", "Recuperation"),
        Ability("hill", "Hill"),
        Ability("baroudeur", "Baroudeur")
    ];

    private static readonly HashSet<string> CoreRiderColumns = new(
        new[]
        {
            RiderIdentity,
            "gene_sz_firstname",
            "gene_sz_lastname",
            "gene_sz_firstlastname",
            "fkIDteam",
            "fkIDregion",
            "fkIDcontract",
            "gene_sz_photo",
            "gene_sz_soundname",
            "gene_i_birthdate",
            "fkIDtype_rider",
            "gene_i_size",
            "gene_i_weight",
            "value_f_potentiel",
            "gene_ilist_fkIDfavorite_races",
            "fkIDyear_progression"
        }.Concat(AbilityDefinitions.SelectMany(static ability =>
            new[] { ability.CurrentColumn, ability.LimitColumn })),
        SqliteIdentifierComparer.Instance);

    private static readonly HashSet<string> CoreContractColumns = new(
        [
            ContractIdentity,
            "fkIDcyclist",
            "fkIDteam",
            "fkIDprevteam",
            "finan_i_period_wage",
            "iYearBegin",
            "iYearEnd",
            "gene_b_active_contract",
            "iRole"
        ],
        SqliteIdentifierComparer.Instance);

    private static readonly HashSet<string> KnownRiderColumns = new(
        new[]
        {
            RiderIdentity,
            "gene_sz_lastname", "gene_sz_firstname", "gene_sz_firstlastname",
            "fkIDteam", "fkIDregion", "fkIDcontract", "fkIDprevcontract", "fkIDnextcontract",
            "gene_sz_photo", "gene_i_birthdate", "gene_f_popularity", "gene_f_popularity_max",
            "value_i_rank_voted", "value_f_potentiel", RiderCurrentAbility.ColumnName, "current_f_stage_score",
            "fkIDrace", "fkIDlaststage", "fkIDcyclist_state", "fkIDtype_rider", "fkIDinjury", "fkIDtga_skin",
            "gene_i_size", "gene_i_weight", "prerace_i_cyclist", "race_b_withdrawal",
            "fkIDstaff_physician", "fkIDstaff_trainer", "fitness_i_handicap", "gene_b_will_retire",
            "fkIDtraining_camp", "gene_i_dossard", "gene_i_champion_bit", "gene_b_nominated", "CONSTANT",
            "gene_sz_soundname", "fkIDstate_roster", "gene_b_inshortlist", "gene_i_date_last_breakaway",
            "gene_i_date_last_punchers", "gene_ilist_fkIDfavorite_races", "fkIDworkplan",
            "bit_i_contrat_preference", "value_f_capital", "gene_i_ptmap", "fkIDyear_progression",
            "value_f_gain", "value_i_trainingstyle", "fkIDcontract_preference_preset", "iContract_fidelity",
            "iContract_refusal_cooldown", "value_i_knowledge", "fkIDcyclist_leader", "value_i_yearneopro",
            "gene_i_nb_total_victory", "gene_i_nb_tdf", "gene_i_nb_giro", "gene_i_nb_vuelta",
            "gene_i_nb_sanremo", "gene_i_nb_flandres", "gene_i_nb_roubaix", "gene_i_nb_liege",
            "gene_i_nb_lombardia"
        }.Concat(AbilityDefinitions.SelectMany(static ability =>
            new[] { ability.CurrentColumn, ability.LimitColumn })),
        SqliteIdentifierComparer.Instance);

    private static readonly HashSet<string> KnownContractColumns = new(
        [
            ContractIdentity, "fkIDcyclist", "fkIDteam", "fkIDprevteam", "finan_i_period_wage",
            "iYearBegin", "iYearEnd", "gene_b_active_contract", "iRole"
        ],
        SqliteIdentifierComparer.Instance);

    private static readonly string[] PreferenceWeightColumns =
        ["iWeight_Salary", "iWeight_Nationality", "iWeight_Role"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Requirements =
        CreateRequirements();

    public Task<MaintenanceCapability> CheckCapabilityAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            async () => (await InspectAsync(sqlitePath, cancellationToken).ConfigureAwait(false)).Capability,
            cancellationToken);

    public Task<RiderCreationDraft> PrepareAsync(
        string sqlitePath,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PrepareCoreAsync(sqlitePath, cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<RiderLookupOption>> SearchLookupAsync(
        string sqlitePath,
        RiderLookupTarget target,
        string query,
        int maxResults,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => SearchLookupCoreAsync(sqlitePath, target, query, maxResults, cancellationToken),
            cancellationToken);

    public Task<RiderCreationPreview> PreviewAsync(
        string sqlitePath,
        RiderCreationInput input,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => PreviewCoreAsync(sqlitePath, input, cancellationToken),
            cancellationToken);

    public Task<MaintenanceApplyResult> ApplyAsync(
        string sqlitePath,
        RiderCreationPreview preview,
        CancellationToken cancellationToken) =>
        SqliteOperationRunner.RunAsync(
            () => ApplyCoreAsync(sqlitePath, preview, cancellationToken),
            cancellationToken);

    private static async Task<(DatabaseSchemaCatalog Catalog, MaintenanceCapability Capability)> InspectAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        DatabaseSchemaCatalog catalog = await new SqliteTableCatalog()
            .DiscoverAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        MaintenanceCapability capability = await CheckCapabilityCoreAsync(sqlitePath, catalog, cancellationToken)
            .ConfigureAwait(false);
        return (catalog, capability);
    }

    private static async Task<DatabaseSchemaCatalog> RequireCatalogAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        (DatabaseSchemaCatalog catalog, MaintenanceCapability capability) = await InspectAsync(sqlitePath, cancellationToken)
            .ConfigureAwait(false);
        MaintenanceSupport.RequireEnabled(capability);
        return catalog;
    }

    private static async Task<MaintenanceCapability> CheckCapabilityCoreAsync(
        string sqlitePath,
        DatabaseSchemaCatalog catalog,
        CancellationToken cancellationToken)
    {
        var requirements = Requirements.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            SqliteIdentifierComparer.Instance);
        if (catalog.TryGetTable(RiderTable, out TableSchema? rider)
            && rider.Columns.Any(static column => SqliteIdentifierComparer.Instance.Equals(column.Name, PreferenceColumn)))
        {
            requirements.Add(PreferenceTable, ["IDcontract_preference_preset"]);
        }

        MaintenanceCapability basic = MaintenanceSupport.Check(
            catalog, MaintenanceToolKind.RiderCreation, requirements, [RiderTable, ContractTable]);
        if (!basic.IsEnabled)
        {
            return basic;
        }

        var reasons = new List<string>();
        ValidateLogicalIdentityColumn(catalog, RiderTable, RiderIdentity, reasons);
        ValidateLogicalIdentityColumn(catalog, ContractTable, ContractIdentity, reasons);
        TableSchema riderSchema = RequireTable(catalog, RiderTable);
        ValidateUnknownRequiredColumns(riderSchema, KnownRiderColumns, reasons);
        ValidateUnknownRequiredColumns(RequireTable(catalog, ContractTable), KnownContractColumns, reasons);
        ValidateColumnAffinity(riderSchema, "gene_sz_firstlastname", [SqliteAffinity.Text], reasons);
        ValidateColumnAffinity(
            riderSchema,
            "value_f_potentiel",
            [SqliteAffinity.Real, SqliteAffinity.Numeric],
            reasons);
        ValidateColumnAffinity(riderSchema, "gene_ilist_fkIDfavorite_races", [SqliteAffinity.Text], reasons);
        ValidateColumnAffinity(riderSchema, PreferenceColumn, [SqliteAffinity.Integer], reasons);
        TableSchema raceSchema = RequireTable(catalog, RaceTable);
        ValidateColumnAffinity(raceSchema, "IDrace", [SqliteAffinity.Integer], reasons);
        ValidateColumnAffinity(raceSchema, "gene_sz_race_name", [SqliteAffinity.Text], reasons);

        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await HasStableIntegerValuesAsync(connection, RiderTable, RiderIdentity, cancellationToken)
            .ConfigureAwait(false))
        {
            reasons.Add($"'{RiderTable}.{RiderIdentity}' must contain unique, non-NULL integer values.");
        }

        if (!await HasStableIntegerValuesAsync(connection, ContractTable, ContractIdentity, cancellationToken)
            .ConfigureAwait(false))
        {
            reasons.Add($"'{ContractTable}.{ContractIdentity}' must contain unique, non-NULL integer values.");
        }

        try
        {
            _ = await ReadSaveDateAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            _ = await ReadFreeStateIdAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
            ColumnSchema? preferenceColumn = riderSchema.Columns.FirstOrDefault(static column =>
                SqliteIdentifierComparer.Instance.Equals(column.Name, PreferenceColumn));
            if (preferenceColumn is not null)
            {
                if (preferenceColumn.IsGenerated || preferenceColumn.IsHidden)
                {
                    throw new InvalidDataException(
                        $"Create Rider cannot safely resolve the generated or hidden '{RiderTable}.{PreferenceColumn}' field.");
                }

                _ = ResolvePreferenceDefault(preferenceColumn);
                await ValidatePreferenceRelationshipAsync(connection, catalog, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            reasons.Add(exception.Message);
        }

        return reasons.Count == 0
            ? basic
            : new MaintenanceCapability(
                MaintenanceToolKind.RiderCreation,
                isEnabled: false,
                basic.Reasons.Concat(reasons),
                basic.MissingTables,
                basic.MissingColumns);
    }

    private static async Task<RiderCreationDraft> PrepareCoreAsync(
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        DatabaseSchemaCatalog catalog = await RequireCatalogAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        DateOnly saveDate = await ReadSaveDateAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);
        long freeStateId = await ReadFreeStateIdAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);
        (int? minimumHeight, int? maximumHeight, int? minimumWeight, int? maximumWeight) =
            await ReadObservedProfileRangesAsync(connection, cancellationToken).ConfigureAwait(false);
        var fields = new List<RiderCreationField>();
        AddFields(fields, catalog, RequireTable(catalog, RiderTable), freeStateId, CoreRiderColumns);
        AddFields(fields, catalog, RequireTable(catalog, ContractTable), freeStateId, CoreContractColumns);
        RiderLookupTarget favoriteRaceLookup = ResolveFavoriteRaceLookup(catalog);
        return new RiderCreationDraft(
            saveDate,
            RiderIdentity,
            ContractIdentity,
            AbilityDefinitions,
            fields,
            favoriteRaceLookup,
            minimumHeight,
            maximumHeight,
            minimumWeight,
            maximumWeight);
    }

    private static async Task<IReadOnlyList<RiderLookupOption>> SearchLookupCoreAsync(
        string sqlitePath,
        RiderLookupTarget target,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Lookup searches return between 1 and 50 rows.");
        }

        DatabaseSchemaCatalog catalog = await RequireCatalogAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        ValidateLookupTarget(catalog, target);
        TableSchema targetSchema = RequireTable(catalog, target.TargetTable);
        string? displayColumn = target.DisplayColumn;
        if (displayColumn is not null && !targetSchema.Columns.Any(column =>
                SqliteIdentifierComparer.Instance.Equals(column.Name, displayColumn)))
        {
            throw new ArgumentException("The lookup display column is no longer available.", nameof(target));
        }

        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        string quotedTargetTable = SqliteSupport.QuoteIdentifier(target.TargetTable);
        string quotedId = SqliteSupport.QuoteIdentifier(target.TargetColumn);
        string displayExpression = displayColumn is null
            ? $"CAST(target.{quotedId} AS TEXT)"
            : $"COALESCE(CAST(target.{SqliteSupport.QuoteIdentifier(displayColumn)} AS TEXT), '')";
        string contextExpression = "NULL";
        var textSearchExpressions = new List<string> { displayExpression };
        if (SqliteIdentifierComparer.Instance.Equals(target.TargetTable, RaceTable))
        {
            foreach (string optionalSearchColumn in new[] { "gene_sz_abbreviation", "CONSTANT" })
            {
                if (targetSchema.Columns.Any(column =>
                        SqliteIdentifierComparer.Instance.Equals(column.Name, optionalSearchColumn)))
                {
                    textSearchExpressions.Add(
                        $"COALESCE(CAST(target.{SqliteSupport.QuoteIdentifier(optionalSearchColumn)} AS TEXT), '')");
                }
            }

            contextExpression = BuildRaceLookupContext(catalog, targetSchema, "target");
        }
        else if (SqliteIdentifierComparer.Instance.Equals(target.TargetTable, RegionTable))
        {
            contextExpression = OptionalLookupLabel(catalog, targetSchema, "target", "fkIDcountry", CountryTable, "IDcountry");
        }
        else if (SqliteIdentifierComparer.Instance.Equals(target.TargetTable, PreferenceTable)
                 && PreferenceWeightColumns.All(name =>
                     targetSchema.Columns.Any(column => SqliteIdentifierComparer.Instance.Equals(column.Name, name))))
        {
            displayExpression = $"'Preset ' || CAST(target.{quotedId} AS TEXT)";
            contextExpression = "'Salary ' || target.iWeight_Salary || ', nationality ' || target.iWeight_Nationality || ', role ' || target.iWeight_Role";
        }

        bool exactId = long.TryParse(
            query?.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long id) && id > 0;
        string predicate;
        if (exactId)
        {
            predicate = $"target.{quotedId} = $id";
            command.Parameters.AddWithValue("$id", id);
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            predicate = $"({string.Join(" OR ", textSearchExpressions.Select(static expression => $"{expression} LIKE $query ESCAPE '\\' COLLATE NOCASE"))})";
            command.Parameters.AddWithValue("$query", $"%{SqliteSupport.EscapeLikeLiteral(query.Trim())}%");
        }
        else
        {
            predicate = "1 = 1";
        }

        command.CommandText = $"""
            SELECT target.{quotedId}, {displayExpression}, {contextExpression}
            FROM {quotedTargetTable} target
            WHERE {predicate}
              AND typeof(target.{quotedId}) = 'integer'
              AND target.{quotedId} > 0
            ORDER BY {displayExpression} COLLATE NOCASE, target.{quotedId}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", maxResults);
        var options = new List<RiderLookupOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long optionId = reader.GetInt64(0);
            string display = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            string? context = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
            options.Add(new RiderLookupOption(
                optionId,
                string.IsNullOrWhiteSpace(display) ? $"{target.Label} {optionId}" : display,
                string.IsNullOrWhiteSpace(context) ? null : context));
        }

        return options.AsReadOnly();
    }

    private static async Task<RiderCreationPreview> PreviewCoreAsync(
        string sqlitePath,
        RiderCreationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        DatabaseSchemaCatalog catalog = await RequireCatalogAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        await using var connection = SqliteSupport.CreateConnection(sqlitePath, SqliteOpenMode.ReadOnly);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        return await BuildPreviewAsync(connection, transaction, catalog, input, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MaintenanceApplyResult> ApplyCoreAsync(
        string sqlitePath,
        RiderCreationPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Input.MissingLimitsAcknowledged &&
            preview.Input.Abilities.Any(static ability => ability.Limit is null))
        {
            throw new InvalidOperationException(
                "Acknowledge the unverified in-game handling of blank ability limits before creating the rider.");
        }

        DatabaseSchemaCatalog catalog = await RequireCatalogAsync(sqlitePath, cancellationToken).ConfigureAwait(false);
        TableSchema riderSchema = RequireTable(catalog, RiderTable);
        TableSchema contractSchema = RequireTable(catalog, ContractTable);

        await using var connection = SqliteSupport.CreateConnection(sqlitePath);
        using var interruptRegistration = await SqliteOperationRunner.OpenInterruptiblyAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            await SetDeferredForeignKeysAsync(connection, sqliteTransaction, cancellationToken).ConfigureAwait(false);
            await SqliteMutationSafety.EnsureInsertDeleteAreReversibleAsync(
                    connection, sqliteTransaction, ContractTable, cancellationToken)
                .ConfigureAwait(false);
            await SqliteMutationSafety.EnsureInsertDeleteAreReversibleAsync(
                    connection, sqliteTransaction, RiderTable, cancellationToken)
                .ConfigureAwait(false);

            RiderCreationPreview current;
            try
            {
                current = await BuildPreviewAsync(connection, sqliteTransaction, catalog, preview.Input, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                throw new DBConcurrencyException(exception.Message, exception);
            }
            if (!current.SnapshotToken.Equals(preview.SnapshotToken, StringComparison.Ordinal)
                || current.NewCyclistId != preview.NewCyclistId
                || current.NewContractId != preview.NewContractId)
            {
                throw new DBConcurrencyException(
                    "The save, selected lookup rows, or available rider/contract IDs changed after preview.");
            }

            await InsertAsync(connection, sqliteTransaction, ContractTable, current.ContractValues, cancellationToken)
                .ConfigureAwait(false);
            await InsertAsync(connection, sqliteTransaction, RiderTable, current.RiderValues, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<TypedRow> contractRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection, sqliteTransaction, contractSchema, ContractIdentity,
                    [current.NewContractId], cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<TypedRow> riderRows = await MaintenanceHistoryCapture.ReadByIntegerIdsAsync(
                    connection, sqliteTransaction, riderSchema, RiderIdentity,
                    [current.NewCyclistId], cancellationToken)
                .ConfigureAwait(false);
            if (contractRows.Count != 1 || riderRows.Count != 1)
            {
                throw new DBConcurrencyException("The inserted rider and contract could not be read back exactly once.");
            }

            TypedRow contractRow = contractRows[0];
            TypedRow riderRow = riderRows[0];
            var history = new MaintenanceEditOperation(
                Guid.NewGuid(),
                RiderTable,
                DateTimeOffset.UtcNow,
                MaintenanceToolKind.RiderCreation,
                $"Create rider {current.NewCyclistId}: {current.Input.FirstName} {current.Input.LastName}",
                [
                    new MaintenanceRowChange(ContractTable, beforeRow: null, contractRow),
                    new MaintenanceRowChange(RiderTable, beforeRow: null, riderRow)
                ]);
            RowReplayGuard[] guards =
            [
                RowReplayGuard.Present(ContractTable, contractRow),
                RowReplayGuard.Present(RiderTable, riderRow)
            ];
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceApplyResult(
                2,
                $"Created rider {current.NewCyclistId} and contract {current.NewContractId}.",
                history,
                guards);
        }
        catch
        {
            await SqliteOperationRunner.RollbackAfterFailureAsync(connection, sqliteTransaction)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<RiderCreationPreview> BuildPreviewAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseSchemaCatalog catalog,
        RiderCreationInput input,
        CancellationToken cancellationToken)
    {
        TableSchema riderSchema = RequireTable(catalog, RiderTable);
        TableSchema contractSchema = RequireTable(catalog, ContractTable);
        DateOnly saveDate = await ReadSaveDateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        ValidateInput(input, saveDate);
        long freeStateId = await ReadFreeStateIdAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var (team, teamRevision) = await ReadCoreLookupAsync(
                connection, transaction, catalog, TeamTable, "IDteam", input.TeamId, "Team", cancellationToken)
            .ConfigureAwait(false);
        var (region, regionRevision) = await ReadCoreLookupAsync(
                connection, transaction, catalog, RegionTable, "IDregion", input.RegionId, "Region", cancellationToken)
            .ConfigureAwait(false);
        var (riderType, typeRevision) = await ReadCoreLookupAsync(
                connection, transaction, catalog, RiderTypeTable, "IDtype_rider", input.RiderTypeId, "Rider type",
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<RiderLookupOption> favoriteRaces = await ReadFavoriteRacesAsync(
                connection,
                transaction,
                catalog,
                input.FavoriteRaceIds,
                cancellationToken)
            .ConfigureAwait(false);

        long riderMax = await ReadMaximumIdAsync(
                connection, transaction, RiderTable, RiderIdentity, cancellationToken)
            .ConfigureAwait(false);
        long contractMax = await ReadMaximumIdAsync(
                connection, transaction, ContractTable, ContractIdentity, cancellationToken)
            .ConfigureAwait(false);
        long newRiderId = CheckedNextId(riderMax, RiderTable);
        long newContractId = CheckedNextId(contractMax, ContractTable);
        bool riderAbsent = await IsIdAbsentAsync(
                connection, transaction, RiderTable, RiderIdentity, newRiderId, cancellationToken)
            .ConfigureAwait(false);
        bool contractAbsent = await IsIdAbsentAsync(
                connection, transaction, ContractTable, ContractIdentity, newContractId, cancellationToken)
            .ConfigureAwait(false);
        if (!riderAbsent || !contractAbsent)
        {
            throw new DBConcurrencyException("The next rider or contract ID is already in use.");
        }

        Dictionary<string, SqliteValue> riderValues = CreateDefaultValues(riderSchema, freeStateId);
        Dictionary<string, SqliteValue> contractValues = CreateDefaultValues(contractSchema, freeStateId);
        ApplyAdvancedValues(riderSchema, riderValues, input.RiderAdvancedValues, CoreRiderColumns);
        ApplyAdvancedValues(contractSchema, contractValues, input.ContractAdvancedValues, CoreContractColumns);

        riderValues[RiderIdentity] = SqliteValue.Integer(newRiderId);
        riderValues["gene_sz_firstname"] = SqliteValue.Text(input.FirstName);
        riderValues["gene_sz_lastname"] = SqliteValue.Text(input.LastName);
        riderValues["gene_sz_firstlastname"] = SqliteValue.Text(input.GameDisplayName);

        riderValues["fkIDteam"] = SqliteValue.Integer(input.TeamId);
        riderValues["fkIDregion"] = SqliteValue.Integer(input.RegionId);
        riderValues["fkIDcontract"] = SqliteValue.Integer(newContractId);
        riderValues["gene_sz_photo"] = SqliteValue.Text(input.Photo);
        riderValues["gene_sz_soundname"] = SqliteValue.Text(input.SoundName);
        riderValues["gene_i_birthdate"] = SqliteValue.Integer(ToDatabaseDate(input.BirthDate));
        if (!input.RiderAdvancedValues.ContainsKey("fkIDcyclist_state"))
        {
            riderValues["fkIDcyclist_state"] = SqliteValue.Integer(freeStateId);
        }
        riderValues["fkIDtype_rider"] = SqliteValue.Integer(input.RiderTypeId);
        riderValues["gene_i_size"] = SqliteValue.Integer(input.Height);
        riderValues["gene_i_weight"] = SqliteValue.Integer(input.Weight);
        riderValues["value_f_potentiel"] = SqliteValue.Real(input.Potential);
        riderValues["gene_ilist_fkIDfavorite_races"] = SqliteValue.Text(
            RiderFavoriteRaceList.Serialize(input.FavoriteRaceIds));
        riderValues["fkIDyear_progression"] = SqliteValue.Integer(newRiderId);

        Dictionary<string, RiderAbilityInput> abilities = input.Abilities.ToDictionary(
            static ability => ability.Key,
            StringComparer.OrdinalIgnoreCase);
        foreach (RiderAbilityDefinition definition in AbilityDefinitions)
        {
            RiderAbilityInput ability = abilities[definition.Key];
            riderValues[definition.CurrentColumn] = SqliteValue.Integer(ability.Current);
            riderValues[definition.LimitColumn] = ability.Limit.HasValue
                ? SqliteValue.Integer(ability.Limit.Value)
                : SqliteValue.Null;
        }

        if (input.UsesAutomaticCurrentAbility)
        {
            riderValues[RiderCurrentAbility.ColumnName] = SqliteValue.Real(
                RiderCurrentAbility.Calculate(input.Abilities.Select(static ability => ability.Current)));
        }

        contractValues[ContractIdentity] = SqliteValue.Integer(newContractId);
        contractValues["fkIDcyclist"] = SqliteValue.Integer(newRiderId);
        contractValues["fkIDteam"] = SqliteValue.Integer(input.TeamId);
        contractValues["fkIDprevteam"] = SqliteValue.Integer(input.TeamId);
        contractValues["finan_i_period_wage"] = SqliteValue.Integer(input.Wage);
        contractValues["iYearBegin"] = SqliteValue.Integer(0);
        contractValues["iYearEnd"] = SqliteValue.Integer(input.ContractEndYear);
        contractValues["gene_b_active_contract"] = SqliteValue.Integer(1);
        contractValues["iRole"] = SqliteValue.Integer((int)input.Role);

        string[] missingLimits = AbilityDefinitions
            .Where(definition => abilities[definition.Key].Limit is null)
            .Select(static definition => definition.Key)
            .ToArray();
        string[] warnings = AbilityDefinitions
            .Where(definition => abilities[definition.Key].Limit is int limit
                && abilities[definition.Key].Current > limit)
            .Select(definition =>
                $"{definition.Label}: Current {abilities[definition.Key].Current} exceeds Limit {abilities[definition.Key].Limit}.")
            .Concat(missingLimits.Length switch
            {
                0 => [],
                1 => ["One ability Limit will be NULL in the working copy and may reopen as zero after saving."],
                _ => [$"{missingLimits.Length} ability Limits will be NULL in the working copy and may reopen as zero after saving."]
            })
            .Concat(input.FavoriteRaceIds.Count == 0
                ? ["No favorite races are selected. The field will be stored as (), and in-game behavior is unverified."]
                : [])
            .ToArray();

        string freeStateRevision = await ReadLookupRevisionAsync(
                connection,
                transaction,
                catalog,
                RiderStateTable,
                "IDcyclist_state",
                freeStateId,
                cancellationToken)
            .ConfigureAwait(false);
        string preferenceRevision = "absent";
        if (riderValues.TryGetValue(PreferenceColumn, out SqliteValue preferenceValue))
        {
            ColumnSchema preferenceColumn = riderSchema.Columns.First(static column =>
                SqliteIdentifierComparer.Instance.Equals(column.Name, PreferenceColumn));
            ValidatePreferenceValue(preferenceColumn, preferenceValue);
            preferenceRevision = "NULL";
            if (preferenceValue.Kind == SqliteValueKind.Integer)
            {
                try
                {
                    await RequireLookupIdAsync(
                            connection,
                            transaction,
                            PreferenceTable,
                            "IDcontract_preference_preset",
                            preferenceValue.IntegerValue,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException exception) when (!input.RiderAdvancedValues.ContainsKey(PreferenceColumn))
                {
                    throw new InvalidDataException(
                        $"The effective default for '{RiderTable}.{PreferenceColumn}' is {preferenceValue.IntegerValue}, "
                        + $"which does not resolve to exactly one {PreferenceTable} row. "
                        + "Choose an existing contract preference preset before previewing the rider.",
                        exception);
                }

                preferenceRevision = await ReadLookupRevisionAsync(
                        connection,
                        transaction,
                        catalog,
                        PreferenceTable,
                        "IDcontract_preference_preset",
                        preferenceValue.IntegerValue,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        IReadOnlyList<string> riderAdvancedLookupRevisions = await ReadAdvancedLookupRevisionsAsync(
                connection,
                transaction,
                catalog,
                riderSchema,
                input.RiderAdvancedValues,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> contractAdvancedLookupRevisions = await ReadAdvancedLookupRevisionsAsync(
                connection,
                transaction,
                catalog,
                contractSchema,
                input.ContractAdvancedValues,
                cancellationToken)
            .ConfigureAwait(false);
        var favoriteRaceRevisions = new List<string>(input.FavoriteRaceIds.Count);
        foreach (long raceId in input.FavoriteRaceIds)
        {
            string revision = await ReadLookupRevisionAsync(
                    connection,
                    transaction,
                    catalog,
                    RaceTable,
                    "IDrace",
                    raceId,
                    cancellationToken)
                .ConfigureAwait(false);
            favoriteRaceRevisions.Add($"favorite-race-revision:{raceId}:{revision}");
        }

        string token = MaintenanceSupport.ComputeToken(
            new[]
                {
                    $"schema:{catalog.SchemaSignature}",
                    $"save-date:{ToDatabaseDate(saveDate)}",
                    $"free-state:{freeStateId}",
                    $"rider-max:{MaintenanceSupport.CanonicalNumber(riderMax)}",
                    $"contract-max:{MaintenanceSupport.CanonicalNumber(contractMax)}",
                    $"rider-target-absent:{riderAbsent}",
                    $"contract-target-absent:{contractAbsent}",
                    $"team-revision:{teamRevision}",
                    $"region-revision:{regionRevision}",
                    $"type-revision:{typeRevision}",
                    $"free-state-revision:{freeStateRevision}",
                    $"preference-revision:{preferenceRevision}"
                }
                .Concat(riderAdvancedLookupRevisions)
                .Concat(contractAdvancedLookupRevisions)
                .Concat(favoriteRaceRevisions)
                .Concat(favoriteRaces.Select(static race => $"favorite-race-option:{race.Id}:{race.DisplayName}:{race.Context}"))
                .Concat(missingLimits.Select(static key => $"missing-limit:{key}"))
                .Concat(CanonicalMap("rider-override", input.RiderAdvancedValues))
                .Concat(CanonicalMap("contract-override", input.ContractAdvancedValues))
                .Concat(CanonicalMap("rider", riderValues))
                .Concat(CanonicalMap("contract", contractValues)));
        return new RiderCreationPreview(
            token,
            input,
            RiderIdentity,
            ContractIdentity,
            newRiderId,
            newContractId,
            missingLimits,
            warnings,
            favoriteRaces,
            riderValues,
            contractValues,
            team,
            region,
            riderType,
            AbilityDefinitions);
    }

    private static async Task<IReadOnlyList<string>> ReadAdvancedLookupRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseSchemaCatalog catalog,
        TableSchema sourceSchema,
        IReadOnlyDictionary<string, SqliteValue> overrides,
        CancellationToken cancellationToken)
    {
        var revisions = new List<string>();
        foreach ((string columnName, SqliteValue value) in overrides.OrderBy(
                     static pair => pair.Key,
                     SqliteIdentifierComparer.Instance))
        {
            if (value.Kind != SqliteValueKind.Integer || value.IntegerValue <= 0)
            {
                continue;
            }

            ColumnSchema? column = sourceSchema.Columns.FirstOrDefault(candidate =>
                SqliteIdentifierComparer.Instance.Equals(candidate.Name, columnName));
            if (column is null)
            {
                continue;
            }

            RiderLookupTarget? target = ResolveLookupTarget(catalog, sourceSchema, column);
            if (target is null)
            {
                continue;
            }

            string revision = await ReadLookupRevisionAsync(
                    connection,
                    transaction,
                    catalog,
                    target.TargetTable,
                    target.TargetColumn,
                    value.IntegerValue,
                    cancellationToken)
                .ConfigureAwait(false);
            revisions.Add(
                $"advanced-lookup:{sourceSchema.Name}.{column.Name}:{target.TargetTable}.{target.TargetColumn}:{value.IntegerValue}:{revision}");
        }

        return revisions.AsReadOnly();
    }

    private static async Task<IReadOnlyList<RiderLookupOption>> ReadFavoriteRacesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseSchemaCatalog catalog,
        IReadOnlyList<long> raceIds,
        CancellationToken cancellationToken)
    {
        TableSchema raceSchema = RequireTable(catalog, RaceTable);
        string displayColumn = raceSchema.Columns.First(column =>
            SqliteIdentifierComparer.Instance.Equals(column.Name, "gene_sz_race_name")).Name;
        string contextExpression = BuildRaceLookupContext(catalog, raceSchema, "race");
        var options = new List<RiderLookupOption>(raceIds.Count);
        foreach (long raceId in raceIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COALESCE(CAST(race.{SqliteSupport.QuoteIdentifier(displayColumn)} AS TEXT), ''),
                       {contextExpression}
                FROM {SqliteSupport.QuoteIdentifier(RaceTable)} race
                WHERE race.{SqliteSupport.QuoteIdentifier("IDrace")} = $id
                LIMIT 2
                """;
            command.Parameters.AddWithValue("$id", raceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"Favorite race ID {raceId} is missing.");
            }

            string displayName = reader.GetString(0).Trim();
            string? context = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"Favorite race ID {raceId} resolves to more than one row.");
            }

            options.Add(new RiderLookupOption(
                raceId,
                string.IsNullOrWhiteSpace(displayName) ? $"Race {raceId}" : displayName,
                string.IsNullOrWhiteSpace(context) ? null : context));
        }

        return options.AsReadOnly();
    }

    private static void ValidateInput(RiderCreationInput input, DateOnly saveDate)
    {
        if (input.BirthDate > saveDate)
        {
            throw new ArgumentException("Birth date cannot be later than the save date.", nameof(input));
        }

        if (input.ContractEndYear < saveDate.Year)
        {
            throw new ArgumentException("Contract end year cannot precede the current save year.", nameof(input));
        }

        string[] expected = AbilityDefinitions.Select(static definition => definition.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actual = input.Abilities.Select(static ability => ability.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Exactly one value is required for each of the 14 rider abilities.", nameof(input));
        }
    }

    private static Dictionary<string, SqliteValue> CreateDefaultValues(TableSchema schema, long freeStateId)
    {
        var values = new Dictionary<string, SqliteValue>(SqliteIdentifierComparer.Instance);
        foreach (ColumnSchema column in schema.Columns.Where(static column => !column.IsGenerated && !column.IsHidden))
        {
            bool isPreference = IsPreferenceColumn(schema.Name, column.Name);
            if (column.DefaultExpression is not null && !isPreference)
            {
                continue;
            }

            values[column.Name] = isPreference
                ? ResolvePreferenceDefault(column)
                : ResolveDefault(schema.Name, column, freeStateId);
        }

        return values;
    }

    private static bool IsPreferenceColumn(string tableName, string columnName) =>
        SqliteIdentifierComparer.Instance.Equals(tableName, RiderTable)
        && SqliteIdentifierComparer.Instance.Equals(columnName, PreferenceColumn);

    private static SqliteValue ResolvePreferenceDefault(ColumnSchema column)
    {
        if (column.DefaultExpression is null)
        {
            return SqliteValue.Integer(4);
        }

        string expression = column.DefaultExpression.Trim();
        while (expression.Length >= 2 && expression[0] == '(' && expression[^1] == ')')
        {
            expression = expression[1..^1].Trim();
        }

        if (expression.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return SqliteValue.Null;
        }

        if (long.TryParse(expression, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
        {
            return SqliteValue.Integer(id);
        }

        throw new InvalidDataException(
            $"Create Rider cannot safely resolve the database default expression for '{RiderTable}.{PreferenceColumn}'. "
            + "Only integer or NULL literal defaults are supported for this field.");
    }

    private static void ValidatePreferenceValue(ColumnSchema column, SqliteValue value)
    {
        if (value.Kind == SqliteValueKind.Null && column.IsNullable)
        {
            return;
        }

        if (value.Kind != SqliteValueKind.Integer)
        {
            throw new InvalidDataException(
                $"'{RiderTable}.{PreferenceColumn}' must contain an integer preset ID"
                + (column.IsNullable ? " or NULL." : "; NULL is not allowed."));
        }
    }

    private static async Task ValidatePreferenceRelationshipAsync(
        SqliteConnection connection,
        DatabaseSchemaCatalog catalog,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT fk."table", fk."to",
                   (SELECT COUNT(*) FROM pragma_foreign_key_list($tableName, 'main') AS part WHERE part.id = fk.id)
            FROM pragma_foreign_key_list($tableName, 'main') AS fk
            WHERE fk."from" = $columnName COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$tableName", RiderTable);
        command.Parameters.AddWithValue("$columnName", PreferenceColumn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(2) != 1)
            {
                throw new InvalidDataException(
                    $"Create Rider cannot safely resolve the composite foreign key containing '{RiderTable}.{PreferenceColumn}'. "
                    + $"Only a single-column reference to '{PreferenceTable}.IDcontract_preference_preset' is supported.");
            }

            ColumnSchema[] targetPrimaryKey = RequireTable(catalog, PreferenceTable).Columns
                .Where(static column => column.IsPrimaryKey).ToArray();
            string targetColumn = reader.IsDBNull(1)
                ? targetPrimaryKey.Length == 1 ? targetPrimaryKey[0].Name : string.Empty
                : reader.GetString(1);
            if (!SqliteIdentifierComparer.Instance.Equals(reader.GetString(0), PreferenceTable)
                || !SqliteIdentifierComparer.Instance.Equals(targetColumn, "IDcontract_preference_preset"))
            {
                throw new InvalidDataException(
                    $"Create Rider cannot safely resolve the declared foreign key for '{RiderTable}.{PreferenceColumn}'. "
                    + $"Only a single-column reference to '{PreferenceTable}.IDcontract_preference_preset' is supported.");
            }
        }
    }

    private static SqliteValue ResolveDefault(string tableName, ColumnSchema column, long freeStateId)
    {
        if (column.Affinity == SqliteAffinity.Blob)
        {
            return SqliteValue.Null;
        }

        if (SqliteIdentifierComparer.Instance.Equals(tableName, ContractTable))
        {
            return SqliteIdentifierComparer.ToLowerAscii(column.Name) switch
            {
                "iyearbegin" => SqliteValue.Integer(0),
                "gene_b_active_contract" => SqliteValue.Integer(1),
                _ => SqliteValue.Null
            };
        }

        if (!KnownRiderColumns.Contains(column.Name))
        {
            return SqliteValue.Null;
        }

        return SqliteIdentifierComparer.ToLowerAscii(column.Name) switch
        {
            "gene_sz_firstname" or "gene_sz_lastname" or "gene_sz_firstlastname"
                or "gene_sz_photo" or "gene_sz_soundname" or "constant" => SqliteValue.Text(string.Empty),
            "gene_ilist_fkidfavorite_races" => SqliteValue.Text("()"),
            "fkidcyclist_state" => SqliteValue.Integer(freeStateId),
            "fkidstate_roster" => SqliteValue.Integer(3),
            "gene_i_date_last_breakaway" => SqliteValue.Integer(101),
            "fkidworkplan" => SqliteValue.Integer(1),
            "gene_i_ptmap" => SqliteValue.Integer(25343),
            "icontract_fidelity" => SqliteValue.Integer(3),
            "value_f_potentiel" => SqliteValue.Real(3.0),
            _ when CoreRiderColumns.Contains(column.Name) => SqliteValue.Null,
            _ when column.Affinity == SqliteAffinity.Text => SqliteValue.Text(string.Empty),
            _ when column.Affinity == SqliteAffinity.Real => SqliteValue.Real(0),
            _ => SqliteValue.Integer(0)
        };
    }

    private static void ApplyAdvancedValues(
        TableSchema schema,
        Dictionary<string, SqliteValue> values,
        IReadOnlyDictionary<string, SqliteValue> overrides,
        HashSet<string> lockedColumns)
    {
        Dictionary<string, ColumnSchema> insertable = schema.Columns
            .Where(static column => !column.IsGenerated && !column.IsHidden)
            .ToDictionary(static column => column.Name, SqliteIdentifierComparer.Instance);
        foreach ((string name, SqliteValue value) in overrides)
        {
            if (!insertable.TryGetValue(name, out ColumnSchema? column))
            {
                throw new ArgumentException($"'{schema.Name}.{name}' is not a writable column.", nameof(overrides));
            }

            if (lockedColumns.Contains(column.Name))
            {
                throw new ArgumentException($"'{schema.Name}.{column.Name}' is controlled by rider creation.", nameof(overrides));
            }

            if (column.Affinity == SqliteAffinity.Blob || value.Kind == SqliteValueKind.Blob)
            {
                throw new ArgumentException(
                    $"BLOB column '{schema.Name}.{column.Name}' is not editable during rider creation.",
                    nameof(overrides));
            }

            if (value.Kind == SqliteValueKind.Null && !column.IsNullable && column.DefaultExpression is null)
            {
                throw new ArgumentException($"'{schema.Name}.{column.Name}' cannot be NULL.", nameof(overrides));
            }

            values[column.Name] = value;
        }
    }

    private static void AddFields(
        List<RiderCreationField> target,
        DatabaseSchemaCatalog catalog,
        TableSchema schema,
        long freeStateId,
        HashSet<string> lockedColumns)
    {
        foreach (ColumnSchema column in schema.Columns.Where(static column => !column.IsGenerated && !column.IsHidden))
        {
            target.Add(new RiderCreationField(
                schema.Name,
                column,
                HumanizeColumnName(column.Name),
                lockedColumns.Contains(column.Name),
                column.DefaultExpression is not null,
                IsPreferenceColumn(schema.Name, column.Name)
                    ? ResolvePreferenceDefault(column)
                    : column.DefaultExpression is null ? ResolveDefault(schema.Name, column, freeStateId) : SqliteValue.Null,
                ResolveLookupTarget(catalog, schema, column)));
        }
    }

    private static RiderLookupTarget? ResolveLookupTarget(
        DatabaseSchemaCatalog catalog,
        TableSchema source,
        ColumnSchema column)
    {
        ForeignKeyRelation[] relations = source.Relationships.Where(candidate =>
                SqliteIdentifierComparer.Instance.Equals(candidate.SourceColumn, column.Name))
            .Take(2)
            .ToArray();
        if (relations is [{ IsComposite: false } relation]
            && catalog.TryGetTable(relation.TargetTable, out TableSchema? target)
            && target.Columns.Any(candidate => !candidate.IsHidden
                && SqliteIdentifierComparer.Instance.Equals(candidate.Name, relation.TargetColumn)))
        {
            return new RiderLookupTarget(
                source.Name,
                column.Name,
                target.Name,
                relation.TargetColumn,
                relation.DisplayColumn ?? ResolveDisplayColumn(target),
                HumanizeColumnName(column.Name));
        }

        if (relations.Length != 0)
        {
            return null;
        }

        (string Table, string Id)? known = SqliteIdentifierComparer.ToLowerAscii(column.Name) switch
        {
            "fkidteam" => (TeamTable, "IDteam"),
            "fkidregion" => (RegionTable, "IDregion"),
            "fkidtype_rider" => (RiderTypeTable, "IDtype_rider"),
            "fkidcyclist_state" => (RiderStateTable, "IDcyclist_state"),
            "fkidcontract_preference_preset" => (PreferenceTable, "IDcontract_preference_preset"),
            "gene_ilist_fkidfavorite_races" => (RaceTable, "IDrace"),
            _ => null
        };
        if (known is not { } mapping || !catalog.TryGetTable(mapping.Table, out TableSchema? knownTarget))
        {
            return null;
        }

        return new RiderLookupTarget(
            source.Name,
            column.Name,
            mapping.Table,
            mapping.Id,
            ResolveDisplayColumn(knownTarget),
            HumanizeColumnName(column.Name));
    }

    private static RiderLookupTarget ResolveFavoriteRaceLookup(DatabaseSchemaCatalog catalog)
    {
        TableSchema rider = RequireTable(catalog, RiderTable);
        ColumnSchema column = rider.Columns.First(candidate =>
            SqliteIdentifierComparer.Instance.Equals(candidate.Name, "gene_ilist_fkIDfavorite_races"));
        return ResolveLookupTarget(catalog, rider, column)
            ?? throw new InvalidDataException("The favorite-race lookup relationship is unavailable.");
    }

    private static void ValidateLookupTarget(DatabaseSchemaCatalog catalog, RiderLookupTarget target)
    {
        TableSchema source = RequireTable(catalog, target.SourceTable);
        ColumnSchema? sourceColumn = source.Columns.FirstOrDefault(column =>
            SqliteIdentifierComparer.Instance.Equals(column.Name, target.SourceColumn));
        if (sourceColumn is null)
        {
            throw new ArgumentException("The lookup source column is unavailable.", nameof(target));
        }

        RiderLookupTarget? discovered = ResolveLookupTarget(catalog, source, sourceColumn);
        if (discovered is null
            || !SqliteIdentifierComparer.Instance.Equals(discovered.TargetTable, target.TargetTable)
            || !SqliteIdentifierComparer.Instance.Equals(discovered.TargetColumn, target.TargetColumn))
        {
            throw new ArgumentException("The lookup relationship is unavailable or ambiguous.", nameof(target));
        }
    }

    private static string? ResolveDisplayColumn(TableSchema table)
    {
        foreach (string candidate in new[]
                 {
                     "gene_sz_name", "gene_sz_race_name", "gene_sz_full_name", "gene_sz_firstname", "gene_sz_lastname",
                     "value_sz_name", "name", "gene_sz_code", "CONSTANT"
                 })
        {
            ColumnSchema? match = table.Columns.FirstOrDefault(column =>
                SqliteIdentifierComparer.Instance.Equals(column.Name, candidate));
            if (match is not null)
            {
                return match.Name;
            }
        }

        return table.Columns.FirstOrDefault(static column => column.Affinity == SqliteAffinity.Text)?.Name;
    }

    private static string BuildRaceLookupContext(DatabaseSchemaCatalog catalog, TableSchema race, string alias)
    {
        string country = OptionalLookupLabel(catalog, race, alias, "fkIDcountry", CountryTable, "IDcountry");
        string raceClass = OptionalLookupLabel(catalog, race, alias, "fkIDrace_class", RaceClassTable, "IDrace_class");
        return $"COALESCE({country} || ' · ' || {raceClass}, {country}, {raceClass})";
    }

    private static string OptionalLookupLabel(
        DatabaseSchemaCatalog catalog, TableSchema source, string sourceAlias, string sourceColumn,
        string targetTable, string targetColumn)
    {
        if (!source.Columns.Any(column => SqliteIdentifierComparer.Instance.Equals(column.Name, sourceColumn)) ||
            !catalog.TryGetTable(targetTable, out TableSchema? target) ||
            !target.Columns.Any(column => SqliteIdentifierComparer.Instance.Equals(column.Name, targetColumn)) ||
            ResolveDisplayColumn(target) is not { } label)
        {
            return "NULL";
        }

        return $"""
            (SELECT NULLIF(TRIM(MIN(CAST({SqliteSupport.QuoteIdentifier(label)} AS TEXT))), '')
             FROM {SqliteSupport.QuoteIdentifier(targetTable)} lookup
             WHERE lookup.{SqliteSupport.QuoteIdentifier(targetColumn)} = {sourceAlias}.{SqliteSupport.QuoteIdentifier(sourceColumn)}
             HAVING COUNT(*) = 1)
            """;
    }

    private static void ValidateUnknownRequiredColumns(
        TableSchema schema,
        HashSet<string> knownColumns,
        List<string> reasons)
    {
        foreach (ColumnSchema column in schema.Columns.Where(static column =>
                     !column.IsGenerated && !column.IsHidden && !column.IsNullable && column.DefaultExpression is null))
        {
            if (!knownColumns.Contains(column.Name))
            {
                reasons.Add(
                    $"'{schema.Name}.{column.Name}' is an unfamiliar required column without a database default.");
            }
            else if (column.Affinity == SqliteAffinity.Blob)
            {
                reasons.Add($"'{schema.Name}.{column.Name}' is a required BLOB without a database default.");
            }
        }
    }

    private static void ValidateColumnAffinity(
        TableSchema schema,
        string columnName,
        IReadOnlyCollection<SqliteAffinity> allowedAffinities,
        List<string> reasons)
    {
        ColumnSchema? column = schema.Columns.FirstOrDefault(candidate =>
            SqliteIdentifierComparer.Instance.Equals(candidate.Name, columnName));
        if (column is not null && !allowedAffinities.Contains(column.Affinity))
        {
            reasons.Add(
                $"'{schema.Name}.{column.Name}' must use {string.Join(" or ", allowedAffinities)} affinity.");
        }
    }

    private static void ValidateLogicalIdentityColumn(
        DatabaseSchemaCatalog catalog,
        string tableName,
        string identityColumn,
        List<string> reasons)
    {
        TableSchema table = RequireTable(catalog, tableName);
        ColumnSchema? identity = table.Columns.FirstOrDefault(column =>
            SqliteIdentifierComparer.Instance.Equals(column.Name, identityColumn));
        if (identity?.Affinity != SqliteAffinity.Integer)
        {
            reasons.Add($"'{tableName}.{identityColumn}' must have integer affinity.");
        }

        if (table.StableIdentity.Kind == StableIdentityKind.None)
        {
            reasons.Add($"'{tableName}' does not expose a stable row identity for history and Undo.");
        }
    }

    private static async Task<DateOnly> ReadSaveDateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SqliteSupport.QuoteIdentifier("gene_i_date")} FROM {SqliteSupport.QuoteIdentifier(ConfigTable)} LIMIT 2";
        var dates = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetFieldType(0) != typeof(long))
            {
                throw new InvalidDataException("'GAM_config.gene_i_date' must contain an integer date.");
            }

            dates.Add(reader.GetInt64(0));
        }

        if (dates.Count != 1 || !TryParseDatabaseDate(dates[0], out DateOnly date))
        {
            throw new InvalidDataException("GAM_config must contain exactly one valid current save date.");
        }

        return date;
    }

    private static async Task<(int? MinimumHeight, int? MaximumHeight, int? MinimumWeight, int? MaximumWeight)>
        ReadObservedProfileRangesAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                MIN(CASE WHEN typeof({SqliteSupport.QuoteIdentifier("gene_i_size")}) = 'integer'
                              AND {SqliteSupport.QuoteIdentifier("gene_i_size")} > 0
                         THEN {SqliteSupport.QuoteIdentifier("gene_i_size")} END),
                MAX(CASE WHEN typeof({SqliteSupport.QuoteIdentifier("gene_i_size")}) = 'integer'
                              AND {SqliteSupport.QuoteIdentifier("gene_i_size")} > 0
                         THEN {SqliteSupport.QuoteIdentifier("gene_i_size")} END),
                MIN(CASE WHEN typeof({SqliteSupport.QuoteIdentifier("gene_i_weight")}) = 'integer'
                              AND {SqliteSupport.QuoteIdentifier("gene_i_weight")} > 0
                         THEN {SqliteSupport.QuoteIdentifier("gene_i_weight")} END),
                MAX(CASE WHEN typeof({SqliteSupport.QuoteIdentifier("gene_i_weight")}) = 'integer'
                              AND {SqliteSupport.QuoteIdentifier("gene_i_weight")} > 0
                         THEN {SqliteSupport.QuoteIdentifier("gene_i_weight")} END)
            FROM {SqliteSupport.QuoteIdentifier(RiderTable)}
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null, null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }

    private static async Task<long> ReadFreeStateIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SqliteSupport.QuoteIdentifier("IDcyclist_state")}
            FROM {SqliteSupport.QuoteIdentifier(RiderStateTable)}
            WHERE {SqliteSupport.QuoteIdentifier("CONSTANT")} = 'FREE' COLLATE NOCASE
            LIMIT 2
            """;
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetFieldType(0) != typeof(long))
            {
                throw new InvalidDataException("The FREE cyclist state must have an integer ID.");
            }

            ids.Add(reader.GetInt64(0));
        }

        if (ids.Count != 1 || ids[0] <= 0)
        {
            throw new InvalidDataException("STA_cyclist_state must contain exactly one positive FREE state.");
        }

        return ids[0];
    }

    private static async Task RequireLookupIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string idColumn,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {SqliteSupport.QuoteIdentifier(tableName)}
            WHERE {SqliteSupport.QuoteIdentifier(idColumn)} = $id
              AND typeof({SqliteSupport.QuoteIdentifier(idColumn)}) = 'integer'
            """;
        command.Parameters.AddWithValue("$id", id);
        long count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidDataException(
                $"{HumanizeColumnName(idColumn)} {id} does not resolve to exactly one {tableName} row.");
        }
    }

    private static async Task<string> ReadLookupRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseSchemaCatalog catalog,
        string tableName,
        string idColumn,
        long id,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, SqliteValue> values = await ReadLookupRowAsync(
                connection, transaction, RequireTable(catalog, tableName), idColumn, id, cancellationToken)
            .ConfigureAwait(false);
        return RowRevision.Compute(values).ToString();
    }

    private static async Task<(RiderLookupOption Option, string Revision)> ReadCoreLookupAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseSchemaCatalog catalog,
        string tableName,
        string idColumn,
        long id,
        string label,
        CancellationToken cancellationToken)
    {
        TableSchema schema = RequireTable(catalog, tableName);
        IReadOnlyDictionary<string, SqliteValue> values = await ReadLookupRowAsync(
                connection, transaction, schema, idColumn, id, cancellationToken)
            .ConfigureAwait(false);
        if (values.GetValueOrDefault(idColumn) != SqliteValue.Integer(id))
        {
            throw new InvalidDataException($"The selected {tableName} row must have integer {idColumn} {id}.");
        }

        string display = ResolveDisplayColumn(schema) is { } displayColumn &&
            values.TryGetValue(displayColumn, out SqliteValue value) && value.Kind == SqliteValueKind.Text
                ? value.TextValue!.Trim()
                : string.Empty;
        var option = new RiderLookupOption(id, string.IsNullOrWhiteSpace(display) ? label : display);
        return (option, RowRevision.Compute(values).ToString());
    }

    private static async Task<IReadOnlyDictionary<string, SqliteValue>> ReadLookupRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TableSchema schema,
        string idColumn,
        long id,
        CancellationToken cancellationToken)
    {
        ColumnSchema[] columns = schema.Columns.Where(static column => !column.IsHidden).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {string.Join(", ", columns.Select(column => SqliteSupport.QuoteIdentifier(column.Name)))}
            FROM {SqliteSupport.QuoteIdentifier(schema.Name)}
            WHERE {SqliteSupport.QuoteIdentifier(idColumn)} = $id
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$id", id);
        var rows = new List<IReadOnlyDictionary<string, SqliteValue>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, SqliteValue>(SqliteIdentifierComparer.Instance);
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            {
                values.Add(columns[ordinal].Name, MaintenanceHistoryCapture.ReadValue(reader, ordinal));
            }

            rows.Add(values);
        }

        if (rows.Count != 1)
        {
            throw new InvalidDataException($"The selected {schema.Name} row for {idColumn} {id} is missing or duplicated.");
        }

        return rows[0];
    }

    private static async Task<bool> HasStableIntegerValuesAsync(
        SqliteConnection connection,
        string tableName,
        string identityColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT NOT EXISTS (
                       SELECT 1
                       FROM {SqliteSupport.QuoteIdentifier(tableName)}
                       WHERE typeof({SqliteSupport.QuoteIdentifier(identityColumn)}) <> 'integer'
                   )
               AND NOT EXISTS (
                       SELECT 1
                       FROM {SqliteSupport.QuoteIdentifier(tableName)}
                       GROUP BY {SqliteSupport.QuoteIdentifier(identityColumn)}
                       HAVING COUNT(*) > 1
                   )
            """;
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<long> ReadMaximumIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string identityColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT MAX({SqliteSupport.QuoteIdentifier(identityColumn)}) FROM {SqliteSupport.QuoteIdentifier(tableName)}";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long CheckedNextId(long currentMaximum, string tableName)
    {
        try
        {
            return checked(currentMaximum + 1);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException($"'{tableName}' has no available MAX + 1 integer identity.", exception);
        }
    }

    private static async Task<bool> IsIdAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string identityColumn,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT NOT EXISTS (SELECT 1 FROM {SqliteSupport.QuoteIdentifier(tableName)} WHERE {SqliteSupport.QuoteIdentifier(identityColumn)} = $id)";
        command.Parameters.AddWithValue("$id", id);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        IReadOnlyDictionary<string, SqliteValue> values,
        CancellationToken cancellationToken)
    {
        string[] parameterNames = values.Select((_, index) => $"$value{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT OR ABORT INTO {SqliteSupport.QuoteIdentifier(tableName)}
                ({string.Join(", ", values.Keys.Select(SqliteSupport.QuoteIdentifier))})
            VALUES ({string.Join(", ", parameterNames)})
            """;
        for (var index = 0; index < parameterNames.Length; index++)
        {
            SqliteValue value = values.ElementAt(index).Value;
            command.Parameters.AddWithValue(parameterNames[index], value.ToClrValue() ?? DBNull.Value);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException($"'{tableName}' did not insert exactly one row.");
        }
    }

    private static async Task SetDeferredForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA defer_foreign_keys = ON";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, IReadOnlyCollection<string>> CreateRequirements()
    {
        string[] riderColumns =
        [
            RiderIdentity, "gene_sz_firstname", "gene_sz_lastname", "gene_sz_firstlastname",
            "fkIDcontract", "fkIDteam", "fkIDregion", "fkIDcyclist_state", "fkIDtype_rider",
            "gene_sz_photo", "gene_sz_soundname", "gene_i_birthdate", "gene_i_size", "gene_i_weight",
            "value_f_potentiel", RiderCurrentAbility.ColumnName, "gene_ilist_fkIDfavorite_races",
            "fkIDyear_progression",
            .. AbilityDefinitions.SelectMany(static ability => new[] { ability.CurrentColumn, ability.LimitColumn })
        ];
        return new Dictionary<string, IReadOnlyCollection<string>>(SqliteIdentifierComparer.Instance)
        {
            [RiderTable] = riderColumns,
            [ContractTable] =
            [
                ContractIdentity, "fkIDcyclist", "fkIDteam", "fkIDprevteam", "finan_i_period_wage",
                "iYearBegin", "iYearEnd", "gene_b_active_contract", "iRole"
            ],
            [TeamTable] = ["IDteam"],
            [RegionTable] = ["IDregion"],
            [RiderTypeTable] = ["IDtype_rider"],
            [RiderStateTable] = ["IDcyclist_state", "CONSTANT"],
            [ConfigTable] = ["gene_i_date"],
            [RaceTable] = ["IDrace", "gene_sz_race_name"]
        };
    }

    private static RiderAbilityDefinition Ability(string key, string label) =>
        new(key, label, $"charac_i_{key}", $"limit_i_{key}");

    private static TableSchema RequireTable(DatabaseSchemaCatalog catalog, string tableName) =>
        catalog.TryGetTable(tableName, out TableSchema? table)
            ? table
            : throw new InvalidDataException($"Required table '{tableName}' is unavailable.");

    private static long ToDatabaseDate(DateOnly date) =>
        (date.Year * 10_000L) + (date.Month * 100L) + date.Day;

    private static bool TryParseDatabaseDate(long value, out DateOnly date)
    {
        int year = (int)(value / 10_000);
        int month = (int)((value / 100) % 100);
        int day = (int)(value % 100);
        return DateOnly.TryParseExact(
            $"{year:D4}{month:D2}{day:D2}",
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static string HumanizeColumnName(string name)
    {
        string value = name;
        foreach (string prefix in new[]
                 {
                     "fkID", "gene_sz_", "gene_i_", "gene_f_", "gene_b_", "value_i_", "value_f_",
                     "current_f_", "fitness_i_", "race_b_", "prerace_i_", "bit_i_", "limit_i_", "charac_i_"
                 })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        value = value.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(value)
            ? name
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static IEnumerable<string> CanonicalMap(
        string prefix,
        IReadOnlyDictionary<string, SqliteValue> values) =>
        values.OrderBy(static pair => pair.Key, SqliteIdentifierComparer.Instance)
            .Select(pair => $"{prefix}:{pair.Key}:{MaintenanceSupport.CanonicalValue(pair.Value)}");

}
