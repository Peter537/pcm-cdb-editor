# Maintenance tools

The app's maintenance tools are an optional layer above generic schema-agnostic editing. This page describes the visible tools, their schema requirements, and their SQL-level rules. See [Testing](testing.md) for verification guidance.

Each tool checks its schema and date requirements and shows what it will change. Applying changes requires explicit confirmation. Apply recomputes a snapshot fingerprint inside one transaction and attempts rollback on failure or cancellation. If the final transaction result is unknown after the UI prepares the mutation state, the app leaves the isolated session dirty for review because it cannot determine whether a late commit occurred. An empty input or already-matching target is a clean no-op and creates no history command.

## Shared rules

- The identifiers listed below are required. The app compares them case-insensitively against discovered schema metadata. Unknown or stale identifiers stop the tool.
- SQL values are parameters. Identifiers are emitted only through the dedicated SQLite quoting helper.
- `GAM_config` date-gated tools require exactly one row and one non-null `gene_i_date` value in `yyyyMMdd` form.
- A preview token covers the relevant input rows and lookup/ranking data. Apply rejects a token that no longer matches.
- Applying changes also requires the mutation target to be an ordinary table that the catalog marks editable from a stable declared-primary-key or `rowid` identity, so the app can capture complete target-row Undo history. A same-named view may pass the initial name/column check, but the tool cannot apply changes to it.
- Empty selected input, no matching rows, or an already-matching update set is a reported no-op.
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

There is no date gate. The selected grid rows yield distinct cyclist IDs by looking first for `IDcyclist`, then `fkIDcyclist`. IDs not present in `DYN_cyclist_fitness.IDcyclist` are omitted from the change list and are not updated.

### Preview and update

The **Rider recovery preset** summary shows how many distinct cyclist IDs were selected and lists old and new values only for found riders whose values would change. The preset sets:

| Column | Value |
| --- | ---: |
| `value_f_FIT` | 99 |
| `value_f_injury` | 0 |
| `value_i_injury_num_days` | 0 |
| `value_f_fat_phy` | 0 |
| `value_f_freshness` | 100 |
| `value_f_prepa` | 99 |

An empty selection, no found cyclists, or rows already at the preset is a no-op. The app applies the exact previewed IDs in one transaction.

## January 1 season-stage repair

### Required schema and gate

- `GAM_config(gene_i_date)` containing exactly one valid game date
- a catalog-editable ordinary `DYN_result_season_stage` table that exposes SQLite `rowid`

The tool is enabled only on January 1 of the parsed game year.

### Preview and update

The **January 1 season-stage repair** summary shows the parsed database date and the total number of rows that will be deleted from `DYN_result_season_stage`; it does not display every row. The snapshot token nevertheless covers all target rows. After confirmation, the app deletes every row in the table in one transaction. An empty table is a no-op. A changed snapshot token blocks apply; partial deletion is forbidden.

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
