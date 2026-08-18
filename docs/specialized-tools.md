# Maintenance tools

The app's maintenance tools are an optional layer above generic schema-agnostic editing. This page describes the visible tools, their schema requirements, and their SQL-level rules. See [Testing](testing.md) for verification guidance.

Each tool checks its schema and date requirements and shows what it will change. Applying changes requires explicit confirmation. Apply recomputes a snapshot fingerprint inside one transaction and attempts rollback on failure or cancellation. If the final transaction result is unknown after the UI prepares the mutation state, the app leaves the isolated session dirty for review because it cannot determine whether a late commit occurred. An empty input or already-matching target is a clean no-op and creates no history command.

## Shared rules

- The identifiers listed below are required. The app compares them case-insensitively against discovered schema metadata. Unknown or stale identifiers stop the tool.
- SQL values are parameters. Identifiers are emitted only through the dedicated SQLite quoting helper.
- `GAM_config` date-gated tools require exactly one row and one non-null `gene_i_date` value in `yyyyMMdd` form.
- A preview token covers the relevant input rows and lookup/ranking data. Apply rejects a token that no longer matches.
- Applying changes also requires the mutation target to be an ordinary table that the catalog marks editable from a stable declared-primary-key or `rowid` identity, so the app can capture complete target-row Undo history. A same-named view may pass the initial name/column check, but the tool cannot apply changes to it.
- Mutation targets reject user-defined operations whose trigger or delete/cascade side effects cannot be represented by Undo. January repair runs the same reversible-delete preflight before removing rows. Rider creation rejects INSERT triggers and delete behavior that would make later Undo incomplete.
- Empty input, no matching rows, or an already-matching update set is a reported no-op.
- Generic table browsing remains available when any specialized capability gate fails.

## Rider recovery preset

### Required schema

Catalog-editable ordinary table `DYN_cyclist_fitness` with:

- `IDcyclist`
- `value_f_FIT`
- `value_f_injury`
- `value_i_injury_num_days`
- `value_f_fat_phy`
- `value_f_freshness`
- `value_f_prepa`

There is no date gate. Recovery has two target modes:

- **Entire team** requires readable `DYN_cyclist(IDcyclist, fkIDteam)` and `DYN_team(IDteam)`. Team choices load once per open session, include empty teams, and resolve the current `DYN_cyclist.fkIDteam` roster during preview. Apply rejects a changed roster.
- **Rider IDs** accepts positive IDs separated by commas, semicolons, or whitespace, then normalizes ordering and duplicates. This mode remains available when the optional rider/team lookup schema is absent.

**Use selected rows** copies distinct integer `IDcyclist` or `fkIDcyclist` values from the current grid selection into the manual field. Selecting rows by itself does not clear or overwrite typed IDs. IDs not present in `DYN_cyclist_fitness.IDcyclist` are retained as missing IDs in the immutable preview and are not updated.

### Preview and update

The **Rider recovery preset** summary shows the resolved roster, found fitness rows, rows needing changes, and missing IDs. It lists old and new values for found riders whose values would change. The preset sets:

| Column | Value |
| --- | ---: |
| `value_f_FIT` | 99 |
| `value_f_injury` | 0 |
| `value_i_injury_num_days` | 0 |
| `value_f_fat_phy` | 0 |
| `value_f_freshness` | 100 |
| `value_f_prepa` | 99 |

An empty team, no found fitness rows, or rows already at the preset is a no-op. The app applies the exact previewed ID set in one transaction.

## Create Rider

Create Rider is a primary navigation destination rather than a Maintenance card. Its six steps are **Identity**, **Profile**, **Abilities**, **Contract**, **Advanced**, and **Review**. A draft stays intact while moving between steps or destinations in the same database session and is cleared when that session changes. No database row changes before the final reviewed Create action.

### Required schema

Creation requires:

- catalog-editable ordinary `DYN_cyclist` and `DYN_contract_cyclist` tables with stable integer identities in `IDcyclist` and `IDcontract_cyclist`;
- the identity, profile, linkage, and complete 14-field Current/Limit schema described below;
- readable `DYN_team(IDteam)`, `STA_region(IDregion)`, `STA_type_rider(IDtype_rider)`, `STA_cyclist_state(IDcyclist_state, CONSTANT)`, `GAM_config(gene_i_date)`, and `INF_contract_preference_preset(IDcontract_preference_preset)` sources;
- writable rider columns `gene_sz_firstlastname`, `value_f_potentiel`, and `gene_ilist_fkIDfavorite_races`, plus readable `STA_race(IDrace, gene_sz_race_name)`;
- exactly one positive cyclist-state ID whose `CONSTANT` is `FREE`, case-insensitively; and
- contract columns `fkIDcyclist`, `fkIDteam`, `fkIDprevteam`, `finan_i_period_wage`, `iYearBegin`, `iYearEnd`, `gene_b_active_contract`, and `iRole`.

Lookup sources may be ordinary tables or readable views. The two mutation targets may not be views or virtual tables. An unfamiliar nullable column is initialized to SQLite `NULL`; a genuine database default is left to SQLite. Capability is rejected when an unfamiliar required column has no default or when a required BLOB has no default. Other BLOB fields remain null/default and are not editable.

### Guided fields and lookups

Identity requires first name, last name, team, region, and rider type. Team, region, and type use bounded searches that render `Name · ID`; region results add country context when `STA_country` can be resolved. Declared foreign keys and conservative, unambiguous known relationships receive the same picker treatment in Advanced, including cyclist state and contract-preference preset. An ambiguous or unresolved `fkID...` field remains a labelled numeric field and says that no reliable lookup was found.

Profile requires birth date, positive integer height, and positive integer weight. Photo, sound name, and favorite races are optional. Dates are stored as `yyyyMMdd`. The wizard reports the positive height and weight ranges observed in the current save as guidance, not additional limits.

The game display name is a dedicated Rider game data field. It updates live as `Last name F.` while first or last name changes, using the first Unicode character of the first name. Manual editing stops automatic synchronization, and **Reset to generated** restores it. The value and override state survive step and destination navigation in the current database session and reset with that session.

Favorite-race search matches `STA_race` by ID or race name and also uses abbreviation or constant when those columns are available. Results show `Race name · country · race class · ID` when the optional context can be resolved. The selected list prevents duplicate IDs, preserves order, and supports removal and keyboard-accessible reordering. It has no imposed maximum. No selection is valid and is stored as `()`, but Review warns because empty favorite-race behavior in the game is unverified. Non-empty lists use ordered, whitespace-free text such as `(11,43,25)`.

Abilities require one Current value for each row below. Current and entered Limit values must be integers from 50 through 85. A Current value may exceed its Limit; the wizard warns but does not rewrite either value. Potential maps to `value_f_potentiel`, defaults to 3.0, and accepts values from 0.5 through 6.0 in 0.5 increments. `value_f_current_ability` remains a separate Advanced field.

| Label | Current column | Limit column |
| --- | --- | --- |
| Plain | `charac_i_plain` | `limit_i_plain` |
| Mountain | `charac_i_mountain` | `limit_i_mountain` |
| Medium Mountain | `charac_i_medium_mountain` | `limit_i_medium_mountain` |
| Downhill | `charac_i_downhilling` | `limit_i_downhilling` |
| Cobble | `charac_i_cobble` | `limit_i_cobble` |
| Time Trial | `charac_i_timetrial` | `limit_i_timetrial` |
| Prologue | `charac_i_prologue` | `limit_i_prologue` |
| Sprint | `charac_i_sprint` | `limit_i_sprint` |
| Acceleration | `charac_i_acceleration` | `limit_i_acceleration` |
| Endurance | `charac_i_endurance` | `limit_i_endurance` |
| Resistance | `charac_i_resistance` | `limit_i_resistance` |
| Recuperation | `charac_i_recuperation` | `limit_i_recuperation` |
| Hill | `charac_i_hill` | `limit_i_hill` |
| Baroudeur | `charac_i_baroudeur` | `limit_i_baroudeur` |

Current and Limit have separate bulk-entry actions. A blank Limit is stored as SQLite `NULL`. Review lists every blank Limit and requires acknowledgement that the game's handling of those values is unverified.

Contract requires a role, positive wage, and end year no earlier than the year in `GAM_config.gene_i_date`. Roles are displayed with their stored `iRole` codes:

| Code | Role |
| ---: | --- |
| 0 | Absolute leader |
| 1 | Absolute sprinter |
| 2 | Leader |
| 3 | Sprinter |
| 4 | Important rider |
| 5 | Luxury teammate |
| 6 | Teammate |

The selected team is reused for rider team, contract current team, and previous team. `iYearBegin` is 0 and `gene_b_active_contract` is 1.

### Clean defaults and review

The workflow does not read or clone a source rider. It allocates cyclist and contract identities with checked `MAX + 1`, links both new rows, and sets `fkIDyear_progression` to the new cyclist ID. It resolves cyclist state to the unique `FREE` row and uses these explicit baseline exceptions:

| Field | Clean value |
| --- | ---: |
| `fkIDstate_roster` | 3 |
| `gene_i_date_last_breakaway` | 101 |
| `fkIDworkplan` | 1 |
| `gene_i_ptmap` | 25343 |
| `fkIDcontract_preference_preset` | 4 |
| `iContract_fidelity` | 3 |
| `value_f_potentiel` | 3.0, editable in Abilities |

Known race, stage, injury, staff, training-camp, leader, retirement, shortlist, nomination, withdrawal, result, victory, and historical counters use neutral zero/empty values. Optional scalar text uses an empty string, and empty list text uses `()`. Favorite-race IDs are an explicit exception because Profile controls their ordered list. `value_f_current_ability` starts as the arithmetic mean of the 14 Current fields and remains editable in Advanced; the app does not claim that this reproduces the game's internal formula.

Review shows identity/profile/contract summaries, the game display name, potential, ordered favorite-race names and IDs, every ability, generated IDs, warnings, and both rows. An expandable technical section exposes every typed insert value without presenting storage-class controls in the normal workflow. The immutable preview token covers schema, save date, FREE state, selected lookup-row and favorite-race revisions, both maxima, target-ID absence, defaults, overrides, role, abilities, potential, ordered favorite IDs and their serialized text, blank Limits and acknowledgement, and the complete insert maps. The Create command becomes available only after preview releases its operation lease, the preview belongs to the current session, and any blank-Limit acknowledgement is satisfied.

### Apply, Undo, and scope

Apply recomputes the preview within one deferred-foreign-key transaction, inserts the contract and rider, reads both complete typed rows back, and commits only if every check succeeds. Collision, changed schema/save/lookup/maxima, overflow, cancellation, INSERT trigger, unsafe delete behavior, SQLite rejection, or read-back mismatch rolls back both inserts.

History stores one maintenance operation ordered as contract then rider. Undo reverses that order, deleting rider before contract; Redo restores both in a deferred-foreign-key transaction. Creation produces no fitness, season, pro-cyclist, history, result, ranking, transfer, or other related rows.

## January 1 season-stage repair

### Required schema and gate

- `GAM_config(gene_i_date)` containing exactly one valid game date
- a catalog-editable ordinary `DYN_result_season_stage` table that exposes SQLite `rowid`

The tool is enabled only on January 1 of the parsed game year.

### Preview and update

The **January 1 season-stage repair** summary shows the parsed database date and the total number of rows that will be deleted from `DYN_result_season_stage`; it does not display every row. The snapshot token nevertheless covers all target rows. After confirmation, the app first rejects DELETE triggers and inbound `CASCADE`, `SET NULL`, or `SET DEFAULT` behavior that Undo cannot restore, then deletes every row in one transaction. An empty table is a no-op. A changed snapshot token blocks apply; partial deletion is forbidden.

## World and European country quotas

The **World and European country quotas** tool uses one preview to calculate the World and European fields for every country. One confirmed transaction applies all four fields, including resetting non-qualifying countries to zero.

### Date and schema gates

The game date must be in November. These exact tables/columns are required:

| Table | Required columns |
| --- | --- |
| `GAM_config` | `gene_i_date` |
| `DYN_result_season` | `fkIDstage`, `fkIDcyclist`, `fkIDresult_season_team`, `gene_i_rank_stage_time`, `gene_i_rank_race_time`, `gene_i_rank_race_mountain`, `gene_i_rank_race_points` |
| `DYN_result_season_stage` | `IDresult_season_stage`, `gene_b_isFinalStage`, `gene_b_isTTT` |
| `DYN_cyclist` | `IDcyclist`, `fkIDregion` |
| `STA_region` | `IDregion`, `fkIDcountry` |
| `STA_country` | `IDcountry`, `CONSTANT`, `fkIDcontinent`, `gene_i_num_cyclist_WC`, `gene_i_num_cyclist_WC_ITT`, `gene_i_num_cyclist_EC`, `gene_i_num_cyclist_EC_ITT` |
| `STA_continent` | `IDcontinent`, `CONSTANT` |
| `STA_stage` | `IDstage`, `fkIDrace`, `gene_i_stage_number` |
| `STA_race` | `IDrace`, `fkIDrace_class` |
| `STA_race_class` | `IDrace_class`, `CONSTANT` |
| `STA_race_bonus` | `fkIDrace_class`, `fkIDclassification_source`, `fkIDclassification_type`, `gene_ilist_bonus` |
| `STA_classification_source` | `IDclassification_source_cym5`, `CONSTANT` |
| `STA_classification_type` | `IDclassification_type_cym5`, `CONSTANT` |

The calculation uses these exact join paths:

- result cyclist -> cyclist -> region -> country;
- result `fkIDstage` -> `STA_stage.IDstage` -> race -> race class;
- historical stage-result lookup: `DYN_result_season_stage.IDresult_season_stage = DYN_result_season.fkIDstage`;
- bonus -> race class on `fkIDrace_class = IDrace_class`;
- bonus -> classification source/type using the two `_cym5` IDs.

The historical stage-result join is unusual but intentional in the implemented calculation; do not replace it with a guessed relationship.

`STA_country` is the mutation target and must be a catalog-editable ordinary table with a stable identity. The other required objects are read through the listed joins.

### Points-scale lookup

Bonus rows are read through inner joins to race class, classification source, and classification type, so rows with dangling lookup references are not returned. Rows whose `STA_race_bonus.gene_ilist_bonus` is NULL or exactly `()` are ignored. For every remaining joined row, classification `CONSTANT` values must be non-null and normalize to non-empty keys. The bonus value must be a comma-separated ordered list of non-negative integers, optionally enclosed in balanced parentheses. Empty strings or tokens, non-integer or negative tokens, unmatched parentheses, and duplicate normalized `(race class, source, type)` keys are hard errors. If no usable scales remain overall, the operation stops with a hard error. Classification selection uses the joined lookup `CONSTANT` values:

- final one-day race: source `RACE_FINAL`, type `TIME`, rank `gene_i_rank_stage_time`;
- final stage race with more than one stage: source `RACE_FINAL`; sum `TIME`/`gene_i_rank_race_time`, `MOUNTAIN`/`gene_i_rank_race_mountain`, and `POINTS`/`gene_i_rank_race_points`;
- non-final race result: source `RACE`, type `TIME`, rank `gene_i_rank_race_time`;
- every stage result: source `STAGE`, type `TIME`, rank `gene_i_rank_stage_time`.

The app reads the list in order. For rank `r`, it takes item `r - 1`. A null rank, rank `<= 0`, sentinel rank `252`, missing classification scale, or rank beyond the list contributes zero.

For a TTT stage (`gene_b_isTTT = 1`), the app divides the stage points only when `fkIDresult_season_team` is non-null and the result count for the same `(fkIDstage, fkIDresult_season_team)` group is found and greater than zero. Otherwise, it leaves the stage award undivided. It rounds a divided award to two decimals using midpoint-to-even.

The calculation sums all positive rider points by country. Countries with no positive total are not ranked.

### Ranking and quotas

Countries are ranked by:

1. total positive points descending;
2. raw `STA_country.CONSTANT` using ordinal case-insensitive comparison; and
3. `STA_country.IDcountry` ascending.

World quota mapping:

| World rank | Road (`gene_i_num_cyclist_WC`) | ITT (`gene_i_num_cyclist_WC_ITT`) |
| --- | ---: | ---: |
| 1-10 | 8 | 2 |
| 11-19 | 6 | 2 |
| 20-25 | 4 | 2 |
| otherwise | 0 | 0 |

For the European ranking, the app first filters countries whose joined continent `CONSTANT`, after trimming, equals `Europa` case-insensitively. It retains the deterministic country ordering above and maps:

| European rank | Road (`gene_i_num_cyclist_EC`) | ITT (`gene_i_num_cyclist_EC_ITT`) |
| --- | ---: | ---: |
| 1-10 | 8 | 2 |
| 11-18 | 6 | 2 |
| otherwise | 0 | 0 |

Zero total positive points across all countries is a hard error; no quotas are changed. Otherwise the immutable preview retains every calculated country row, including raw/canonical code, display name, points, World and European ranks, and old/new four-field values. An already matching database is a no-op.

The read-only preview has three views:

- **Changes** — every row whose old and new quota values differ;
- **World qualifiers** — every row with a positive calculated World road or time-trial quota; and
- **European qualifiers** — every row with a positive calculated European road or time-trial quota.

Changes and World qualifiers initially sort by World rank, best first. European qualifiers initially sort by European rank, best first. Each view can instead sort in either direction by canonical country code, UCI points, World rank, or European rank. Unranked countries remain last; canonical code, raw code, and country ID provide deterministic tie-breaks.

Changing the visible view or sort never changes the immutable preview, its snapshot token, or the update set. Confirmation applies every calculated change, including changes hidden by the current view, skips unchanged rows, and records the combined four-column mutation as one undoable operation. Any stale preview, game-date, result, or existing-quota change aborts and rolls back the entire operation.

## Country aliases

Quota calculation uses the stored raw code for its ranking tie-break; canonical codes are display values. The app uses this case-insensitive alias map:

| Source | Canonical | Source | Canonical | Source | Canonical |
| --- | --- | --- | --- | --- | --- |
| `den` | `DNK` | `ned` | `NLD` | `ger` | `DEU` |
| `SWD` | `SWE` | `CRO` | `HRV` | `lat` | `LVA` |
| `swi` | `CHE` | `MAS` | `MYS` | `SER` | `SRB` |
| `BUL` | `BGR` | `GRE` | `GRC` | `CRC` | `CRI` |
| `ZIM` | `ZWE` | `BER` | `BMU` | `MOL` | `MDA` |
| `ROM` | `ROU` | `KOS` | `XK` | `SLO` | `SVN` |
| `POR` | `PRT` | `CHI` | `CHN` | `KUW` | `KWT` |
| `OMA` | `OMN` | `SAR` | `ZAF` | `UAE` | `ARE` |
| `URU` | `URY` |  |  |  |  |

Values not listed in the map fall back to trimmed uppercase; empty values become `UNK`. Aliases affect presentation only. The raw stored code continues to participate in quota ranking and review-sort tie-breaking; the country ID remains the database identity.
