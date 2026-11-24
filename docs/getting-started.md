# Getting started

PCM CDB Editor 0.1.0 is an unreleased Windows x64 app for inspecting and editing Pro Cycling Manager CDB files through the bundled SQLiteExporter. Keep an independent backup of any valuable database.

Compatibility testing has covered only Pro Cycling Manager 2025 (PCM 25). Other releases may work, but I have not tested them and do not claim compatibility with them. A file works only when the bundled exporter can convert it, the app can open the resulting SQLite database, and the required schema is present. Generic browsing and editing adapt to the discovered schema. The optional maintenance tools have stricter schema and date requirements.

## Requirements

- Windows 10 or 11 x64, build 19041 or later
- Git and PowerShell
- .NET SDK 10.0.403 exactly, selected by `global.json`
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

The first command must print `10.0.403`. Every project has a `packages.lock.json`. Serialized non-static-graph evaluation avoids an SDK/WinUI `.slnx` restore failure while retaining locked mode. Do not add another feed or switch to unlocked restore to work around a restore failure.

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
3. Browse, search, filter, and sort the isolated SQLite working copy. With a finite row choice, counts begin as **Unknown** while they are calculated independently. **All rows** reports the exact count after its complete result has loaded.
4. Edit identified table rows, then use Undo or Redo if needed. Closing a table tab does not discard database changes, but it does discard that tab's in-memory sort.
5. Choose **Save** to replace the current CDB destination, or **Save as** to choose another destination. The editor checks the destination, stages a non-empty CDB in the same directory, and preserves the displaced file when it replaces an existing destination. A new file or an unchanged zero-byte picker placeholder has no prior content to back up.

The staged-output check confirms only that the exporter produced a non-empty file. It does not guarantee that every Pro Cycling Manager release will accept the result.

A failure before destination replacement preserves the session's prior dirty state and leaves the destination unchanged; an already-dirty session remains recoverable. If replacement succeeds but the app cannot finalize the session metadata, it reports that the destination was saved and tells you to reopen it. The app prompts before replacing the open database or closing while the session is dirty.

## Browse, filter, and page

- Search applies to the current table. `%`, `_`, and `\` are treated as literal substring characters rather than SQLite wildcard syntax.
- Quick filters use the selected column's discovered type. Unsupported numeric conversions are rejected rather than silently coerced.
- Advanced filters combine the positive rule numbers shown in the filter editor with `AND`, `OR`, and parentheses. `AND` binds more tightly than `OR`; for example, `1 AND (2 OR 3)`. Unknown rule numbers, malformed operators, and unbalanced parentheses are rejected.
- Use **Sort** to edit or clear the ordered multi-column sort. Click or tap a visible column header to make its sort target priority 1. A newly promoted target starts descending, and repeating the same primary sort target toggles direction. Existing secondary priorities keep their relative order, and the single header arrow shows priority 1.
- Header and dialog sorting run against the database, return finite paging to the first page, and retain the current search, filters, and row choice. The result is ordered across pages, or across the complete materialized result in **All rows**, rather than only within the rows previously loaded.
- Sorting remains in memory for each open table tab. Switching among open tabs retains their individual sorts. Closing and reopening a table, opening or replacing the CDB, or restarting the application starts the table unsorted.
- **Preferences** offers **IDs only**, **Names only**, and **Names and IDs**. Combined cells show the name followed by the stored key. Hover over a clipped cell to see the full value. In either name mode, an unresolved relationship shows the stored value and its tooltip explains why.
- Composite or ambiguous relationships keep the stored key and use the ordinary typed editor on editable rows. A recognized single-column relationship can still offer raw entry when a particular key lacks a matching record or readable label.
- Search matches both stored values and supported related names. Digits are matched as text. **Filters** and **Sort** offer separate **stored value** and **related name** choices for supported relationships.
- A relationship header uses the related name in either name mode and the stored key in **IDs only**. A display change retains any sort already applied. The **Sort** button's description identifies the active targets and directions.
- **Rows to show** offers **100 rows**, **250 rows**, **500 rows**, and **All rows (slower)**. A successfully loaded finite choice uses bounded paging and becomes the global default when the preference is saved.
- **All rows** runs one query without a row cap and materializes every row matching the current search and filters in the current database sort order. It can require substantial time and memory. Loading is cancellable, and rows are replaced only after the complete result succeeds; cancellation, failure, or a superseding request leaves the previous completed view intact.
- In **All rows**, the status area shows **All rows**, the exact count comes from the completed result, and **Previous** and **Next** remain visible but disabled. Search, filters, sorting, edits, refreshes, foreign-key display changes, and tab switching retain the choice.
- **All rows** belongs only to the currently open table tab. Closing and reopening that tab, or closing the editor, discards it; new tabs use the last finite preference. Loading, filtering, sorting, and finite-page counting remain cancellable; a superseded request cannot replace the newer result.
- The query summary shows the completed search, applied filter, and sort. **Reset query** clears these and returns to the first page. **Refresh** reruns the current query against the working database. **Reset column layout** in the command menu restores default widths and order for that table.
- Move to a cell to inspect the current row's stored values, SQLite storage classes, and available related names. In a narrow window, open **Row details** in the command menu. **Edit row** and **Delete row** identify the one current row they affect. Checked rows are counted separately. Rows without stable keys can be inspected, and their edit and delete actions are disabled.

## Edit safely

- Inline editing preserves the cell's current Integer, Real, or Text SQLite storage class. The app validates the value before TableView commits and begins the database operation after the control reports that editing has ended.
- On a supported relationship cell, press **Enter**, **F2**, or double-click to search for a referenced value. Results show a name when available, the typed key, and identifying context. Choose **Apply selected key**, or use **Enter raw key** to type a value or set **NULL** where allowed. **Escape** cancels the picker.
- **Edit row** can explicitly write Integer, Real, Text, or NULL values.
- In **Edit row**, use **Choose referenced value** next to a supported field. **Use selected key** fills the field in the row draft. **Update** applies the draft, and the editor checks that the chosen reference still resolves before committing. Raw entries continue to follow the database's constraints.
- Move to a relationship cell and choose **Open referenced row** in the command menu. This also works from a read-only source row when the key identifies a supported target. If the current cell has no supported reference, the editor asks which relationship to open from the current row. **Return to source** restores the original table, query, and position.
- Edit and Insert keep all draft fields open when a typed value or database constraint is rejected. Review the message, correct the draft, and retry, or cancel without applying it.
- All mutations use transactions. Updates, deletes, Undo, and Redo use typed row-revision or row-presence guards; maintenance uses snapshot fingerprints; inserts validate their assigned or returned identity. Stale state is rejected instead of overwriting newer data.
- BLOB values show their type and size but cannot be edited. Session history may still contain complete typed row snapshots, including BLOB data required for Undo.
- Inserts read the actual typed identity returned by SQLite; declared keys are never guessed from an internal row ID. NULL or conflicting identities are rejected before commit.
- Insert and Delete require both actions to be reversible. The app refuses INSERT/DELETE triggers, `AUTOINCREMENT` counters, and inbound `ON DELETE CASCADE`, `SET NULL`, or `SET DEFAULT` actions that row history cannot restore. Updates refuse UPDATE triggers and side-effecting `ON UPDATE` actions on changed referenced keys. Undo and Redo apply the same checks.

The command row keeps **Saved**, **Unsaved changes**, or **Unsaved changes · incomplete history** visible while other operations run. Open **Changes** to inspect recorded fields, before and after values, and the next Undo and Redo. Open **Save details** for the destination, the last backup path, and the backup folder. If the destination changed outside the editor, choose **Save as**, explicitly overwrite the observed version, or cancel. An uncreated rider draft has a separate **Keep editing** or **Discard rider draft** decision when closing or replacing the session.

## Preferences

Preferences include system, light, and dark themes; compact or comfortable density; 100-, 250-, or 500-row pages; foreign-key display mode; recent files; and per-user `.cdb` registration. Invalid or obsolete saved page-size values fall back to 100. Table widths, order, visibility, and frozen-column state persist by schema signature. Sorts and **All rows** are not stored in preferences or `settings.json`.

Saving another preference does not switch a tab out of **All rows**. Explicitly changing the finite page-size preference may apply that finite choice to the selected tab.

`settings.json` does not store database row contents. It can store up to 12 absolute recent-file paths. To remove them from the file, choose **Clear recent files** and then **Save preferences**.

## Local data, recovery, and retention

The app stores per-user data under `%LOCALAPPDATA%\PcmCdbEditor`:

- `settings.json` contains preferences, recent absolute paths, and saved column layouts;
- `Sessions` contains isolated CDB and SQLite working files, metadata with source and destination paths, and `edit-history.json`;
- `Backups` normally contains CDBs displaced by an atomic replacement. **Save details** shows the actual path if a backup had to stay beside the destination.

Undo history contains complete typed row snapshots and can encode BLOB values. Keep this directory out of commits and issue attachments. **Recovery** lists available working sessions with their full paths and update times. Select the intended session and choose **Resume selected** or **Discard selected**. **Recent databases** is in command overflow and appears in the empty workspace.

Normal close and discard clean the active session. **Clean completed sessions** in Recovery removes eligible completed sessions after confirmation. The app keeps recoverable sessions and backups without age- or size-based expiration. Uninstall preserves the data directory.

## Maintenance tools

The primary navigation includes a dedicated **Create Rider** wizard. **Maintenance** contains **Rider recovery preset**, **January 1 season-stage repair**, and **World and European country quotas**. Each workflow checks the current database, shows what it will change, requires confirmation, and rejects a stale snapshot before applying a transaction.

For rider recovery, choose **Entire team** to resolve the current `DYN_cyclist.fkIDteam` roster, or **Rider IDs** to enter positive IDs separated by commas, semicolons, or whitespace. **Use selected rows** copies suitable IDs from the table grid when convenient; ordinary row selection does not overwrite manual input. Team lookup is optional for recovery, so manual IDs remain usable when `DYN_team` is absent.

Create Rider has six steps: Identity, Profile, Abilities, Contract, Advanced, and Review. Name/ID pickers resolve teams, regions with country context, rider types, favorite races, and other unambiguous relationships. The game display name updates as `Last name F.` until you edit it manually. **Reset to generated** resumes that rule. Favorite races are optional and retain their selected order. Enter all 14 Current abilities from 50 through 85. Limits use the same range but may be blank. A blank Limit becomes SQLite `NULL` in the working copy. The bundled converter can reopen it as zero after saving to CDB, so review requires an explicit acknowledgement. Potential accepts half-point values from 0.5 through 6.0. Review shows the generated IDs, profile, ability values, favorite races, warnings, and both complete typed insert maps. The creation receipt opens the generated rider and contract and provides a **Save CDB** action. The wizard creates only the core `DYN_cyclist` and `DYN_contract_cyclist` rows. See [Maintenance tools](specialized-tools.md) for the exact schema, defaults, role codes, and transaction rules.

## Troubleshooting

### The SDK version is rejected

Run `dotnet --version` from the repository root. Install SDK 10.0.403 if another version is selected, and keep `global.json` in place.

### Locked restore fails

Confirm that NuGet.org is reachable and rerun the exact restore command above. A lock-file mismatch is a dependency change to review; do not delete lock files or use unlocked restore as a workaround.

### A CDB does not open or convert

Confirm that the input exists, is non-empty, and ends in `.cdb`. Verify the bundled exporter files against [Third-party notices](../THIRD_PARTY_NOTICES.md). Conversion failure can also mean that the file's game version or schema is not compatible with the bundled exporter; I do not claim support for every CDB version.

### A table or operation is read-only

Views, virtual tables, and tables without a safe identity are browse-only. BLOB cells cannot be edited. Writes may also be blocked by triggers, allocation counters, or cascading or value-setting inbound foreign keys whose effects Undo cannot restore. The app reports the applicable reason rather than bypassing the guard.

### A maintenance tool is unavailable

Open the destination for the affected workflow and review its reported missing table, column, identity, trigger-safety, lookup, or date gate. Create Rider reports its capability state in its own navigation destination. Rider recovery can still use manually entered IDs when only its optional team lookup is unavailable. Generic browsing remains available even when the exact workflow schema is absent.

### Saving fails

The destination is replaced only after the exporter produces a non-empty staged file. Keep the application open, correct the reported path or conversion problem, and retry **Save** or **Save as**. If the message says the destination was saved but session metadata could not be synchronized, reopen the saved CDB before making more edits. If the process was interrupted before replacement, use the recovery choice offered on the next launch.
