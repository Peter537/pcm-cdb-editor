# Getting started

PCM CDB Editor 0.1.0 is an unreleased Windows x64 app for inspecting and editing Pro Cycling Manager CDB files through the bundled SQLiteExporter. Keep an independent backup of any valuable database.

I do not claim compatibility with any specific Pro Cycling Manager release. A file works only when the bundled exporter can convert it, the app can open the resulting SQLite database, and the required schema is present. Generic browsing and editing adapt to the discovered schema. The optional maintenance tools have stricter schema and date requirements.

## Requirements

- Windows 10 or 11 x64, build 19041 or later
- Git and PowerShell
- .NET SDK 10.0.303 exactly, selected by `global.json`
- network access for the first restore from the single NuGet.org source in `NuGet.Config`
- the hash-pinned third-party files under `third_party/SQLiteExporter`, matching [Third-party notices](../THIRD_PARTY_NOTICES.md)

Visual Studio is optional. The documented build uses the .NET CLI.

## Restore and build

From the repository root:

```powershell
dotnet --version
dotnet restore PcmCdbEditor.slnx --locked-mode --configfile NuGet.Config --disable-parallel --disable-build-servers -p:RestoreUseStaticGraphEvaluation=false -p:BuildInParallel=false -m:1
dotnet build PcmCdbEditor.slnx --configuration Release --no-restore -p:Platform=x64 --disable-build-servers -m:1
```

The first command must print `10.0.303`. Every project has a `packages.lock.json`. Serialized non-static-graph evaluation avoids an SDK/WinUI `.slnx` restore failure while retaining locked mode. Do not add another feed or switch to unlocked restore to work around a restore failure.

Run the automated suites separately as described in [Testing](testing.md).

## Launch

After a successful build, run the app and use its file picker:

```powershell
dotnet run --project src/PcmCdbEditor.App/PcmCdbEditor.App.csproj --configuration Release --no-restore
```

You can also pass a CDB path after `--`:

```powershell
dotnet run --project src/PcmCdbEditor.App/PcmCdbEditor.App.csproj --configuration Release --no-restore -- "C:\path\to\database.cdb"
```

The app scans its arguments in order and opens the first value whose name ends in `.cdb`, case-insensitively. That value does not have to be the first application argument. The file picker, command-line argument, recent-file list, and file association all use the same copy-first workspace flow.

## First use

1. Open a non-empty CDB. The app creates a session under `%LOCALAPPDATA%\PcmCdbEditor\Sessions`, copies the source into it, and gives only that copy to SQLiteExporter. Opening a file does not change the original.
2. Choose a table or view. The catalog exposes discovered columns, keys, foreign keys, and edit capability. Views, virtual tables, and tables without a safe primary-key or `rowid` identity remain read-only.
3. Browse, search, filter, and sort the isolated SQLite working copy. Row counts begin as **Unknown** while they are calculated independently.
4. Edit identified table rows, then use Undo or Redo if needed. Closing a table tab does not discard database changes.
5. Choose **Save** to replace the current CDB destination, or **Save as** to choose another destination. Saving exports to a same-directory staging file, requires a non-empty output, backs up an existing non-empty destination, and then replaces it. A new or zero-byte destination has no prior content to back up.

The staged-output check confirms only that the exporter produced a non-empty file. It does not guarantee that every Pro Cycling Manager release will accept the result.

A failure before destination replacement preserves the session's prior dirty state and leaves the destination unchanged; an already-dirty session remains recoverable. If replacement succeeds but the app cannot finalize the session metadata, it reports that the destination was saved and tells you to reopen it. The app prompts before replacing the open database or closing while the session is dirty.

## Browse, filter, and page

- Search applies to the current table. `%`, `_`, and `\` are treated as literal substring characters rather than SQLite wildcard syntax.
- Quick filters use the selected column's discovered type. Unsupported numeric conversions are rejected rather than silently coerced.
- Advanced filters combine the positive rule numbers shown in the filter editor with `AND`, `OR`, and parentheses. `AND` binds more tightly than `OR`; for example, `1 AND (2 OR 3)`. Unknown rule numbers, malformed operators, and unbalanced parentheses are rejected.
- Ordered multi-column sorting and foreign-key display modes use the discovered schema. Displaying a referenced name does not change the stored key.
- Preferences offer page sizes of 100 or 250 rows. Loading, filtering, sorting, and counting remain cancellable; a superseded request cannot replace the newer result.

## Edit safely

- Inline editing preserves the cell's current Integer, Real, or Text SQLite storage class.
- Inline values are validated before TableView commits, but the database mutation and grid refresh begin only after the control reports that editing has ended. Double-click, Enter, F2, click-away, and Escape therefore stay within the native cell-edit lifecycle.
- **Edit row** can explicitly write Integer, Real, Text, or NULL values.
- All mutations use transactions. Updates, deletes, Undo, and Redo use typed row-revision or row-presence guards; maintenance uses snapshot fingerprints; inserts validate their assigned or returned identity. Stale state is rejected instead of overwriting newer data.
- BLOB values show their type and size but cannot be edited. Session history may still contain complete typed row snapshots, including BLOB data required for Undo.
- Inserts must provide an identity when SQLite cannot safely return one; generated composite or non-integer identities are not guessed.
- Deletion is refused when the target has a `DELETE` trigger or an inbound foreign key using `CASCADE`, `SET NULL`, or `SET DEFAULT`. Those side effects cannot be represented safely by the row-level Undo record.

## Preferences

Preferences include system, light, and dark themes; compact or comfortable density; 100- or 250-row pages; foreign-key display mode; recent files; and per-user `.cdb` registration. Table widths, order, visibility, sorting, and frozen-column state are keyed by schema signature.

`settings.json` does not store database row contents. It can store up to 12 absolute recent-file paths, so use **Clear recent files** before sharing a settings file or screenshot when those paths are sensitive.

## Local data, recovery, and retention

The app stores per-user data under `%LOCALAPPDATA%\PcmCdbEditor`:

- `settings.json` contains preferences, recent absolute paths, and saved table layouts;
- `Sessions` contains isolated CDB and SQLite working files, metadata with source and destination paths, and `edit-history.json`;
- `Backups` contains copies made before replacing existing non-empty destinations.

Undo history contains complete typed row snapshots and can encode BLOB values. Treat the entire directory as sensitive: do not commit it or attach it to an issue. At startup, the app offers interrupted dirty sessions with **Resume**, **Discard**, and **Not now**.

Normal close and discard paths clean the active session, but there is no age- or size-based retention limit for history, sessions, or backups, and uninstall preserves this directory. Backups are not removed automatically. With the application closed, you may remove backups and stale sessions only after confirming that none is needed for recovery. Deleting the whole directory also removes preferences, recents, layouts, recovery data, and backups.

## Maintenance tools

The primary navigation includes a dedicated **Create Rider** wizard. **Maintenance** contains **Rider recovery preset**, **January 1 season-stage repair**, and **World and European country quotas**. Each workflow checks the current database, shows what it will change, requires confirmation, and rejects a stale snapshot before applying a transaction.

For rider recovery, choose **Entire team** to resolve the current `DYN_cyclist.fkIDteam` roster, or **Rider IDs** to enter positive IDs separated by commas, semicolons, or whitespace. **Use selected rows** copies suitable IDs from the table grid when convenient; ordinary row selection does not overwrite manual input. Team lookup is optional for recovery, so manual IDs remain usable when `DYN_team` is absent.

Create Rider has six steps: Identity, Profile, Abilities, Contract, Advanced, and Review. Name/ID pickers resolve teams, regions with country context, rider types, favorite races, and other unambiguous relationships. The game display name updates as `Last name F.` until you edit it manually; **Reset to generated** resumes that rule. Favorite races are optional and retain their selected order. Enter all 14 Current abilities from 50 through 85; Limits use the same range but may be blank. Potential accepts half-point values from 0.5 through 6.0. A blank Limit becomes SQLite `NULL` and requires an explicit acknowledgement because in-game generation is unverified. Review shows generated IDs, warnings, the game display name, potential, favorite races, and both complete typed insert maps before the final confirmation. The wizard creates only the core `DYN_cyclist` and `DYN_contract_cyclist` rows; it does not synthesize fitness, season, result, ranking, transfer, or related records. See [Maintenance tools](specialized-tools.md) for the exact schema, defaults, role codes, and transaction rules.

## Troubleshooting

### The SDK version is rejected

Run `dotnet --version` from the repository root. Install SDK 10.0.303 if another version is selected, and keep `global.json` in place.

### Locked restore fails

Confirm that NuGet.org is reachable and rerun the exact restore command above. A lock-file mismatch is a dependency change to review; do not delete lock files or use unlocked restore as a workaround.

### A CDB does not open or convert

Confirm that the input exists, is non-empty, and ends in `.cdb`. Verify the bundled exporter files against [Third-party notices](../THIRD_PARTY_NOTICES.md). Conversion failure can also mean that the file's game version or schema is not compatible with the bundled exporter; I do not claim support for every CDB version.

### A table or operation is read-only

Views, virtual tables, and tables without a safe identity are browse-only. BLOB cells cannot be edited. A delete may also be blocked by a `DELETE` trigger or a cascading or value-setting inbound foreign key. The app reports the applicable reason rather than bypassing the guard.

### A maintenance tool is unavailable

Open the destination for the affected workflow and review its reported missing table, column, identity, trigger-safety, lookup, or date gate. Create Rider reports its capability state in its own navigation destination. Rider recovery can still use manually entered IDs when only its optional team lookup is unavailable. Generic browsing remains available even when the exact workflow schema is absent.

### Saving fails

The destination is replaced only after the exporter produces a non-empty staged file. Keep the application open, correct the reported path or conversion problem, and retry **Save** or **Save as**. If the message says the destination was saved but session metadata could not be synchronized, reopen the saved CDB before making more edits. If the process was interrupted before replacement, use the recovery choice offered on the next launch.
