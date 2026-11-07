# Testing

The automated suites use synthetic fixtures by default, so they do not require or disclose a real Pro Cycling Manager database. Real-CDB compatibility testing has covered only Pro Cycling Manager 2025 (PCM 25); other releases remain untested. Test totals will change as coverage grows; use the commands below and check that they pass.

## Prerequisites

Complete the locked restore and Release/x64 build in [Getting started](getting-started.md) before using the `--no-build --no-restore` commands below.

For live checks, launch the editor with `PCM_CDB_EDITOR_DATA_ROOT` set in that process's environment to an absolute directory under the disposable run. The editor puts its settings, sessions, and backups there. Pass a run-owned CDB copy on the command line. A regular launch uses the existing local application-data location.

## Unit tests

```powershell
dotnet test tests/PcmCdbEditor.UnitTests/PcmCdbEditor.UnitTests.csproj --configuration Release --no-build --no-restore --disable-build-servers -m:1
```

The unit suite covers domain and view-model state, typed SQLite values, filter and query models, paging coordination, finite preferences, sort descriptors, edit history, write-ahead state, and maintenance projections. Source-level checks cover the shell's per-tab **All rows** wiring, wizard commands, save-state presentation, accessibility metadata, and TableView adapter contracts. Use the native checks below for the resulting UI behavior.

The source-level TableView checks verify event deferral, committed primary-indicator wiring, bind-generation guards, the selection-scroll completion barrier, and viewport restoration through `TableView.HorizontalOffsetProperty`. They do not run the WinUI layout engine. A passing unit suite therefore does not prove rendered header and row alignment or crash-free header sorting.

## Integration tests

```powershell
dotnet test tests/PcmCdbEditor.IntegrationTests/PcmCdbEditor.IntegrationTests.csproj --configuration Release --no-build --no-restore --disable-build-servers -m:1
```

The integration suite uses generated SQLite databases and fake converter processes to exercise schema discovery, relationship lookup and typed keys, guarded mutation and replay, history and layout persistence, save conflicts and backup handoff, recovery ownership and cleanup, converter bounds and cancellation, maintenance, and paging responsiveness.

Paging fixtures cover bounded `PageRequest` behavior, 100/250/500 persistence, complete all-row results at 0, 499, 500, 501, 1,201, and more than 10,000 rows, search, filters, database sorting, views, and every foreign-key display mode. Caller cancellation, reset, invalidation, disposal, and late-result tests verify that a failed, cancelled, or superseded all-row load never partially binds or replaces the previous completed view. Million-row tests retain bounded finite-paging performance coverage and check prompt all-row cancellation rather than full materialization.

Create Rider fixtures cover clean draft preparation, bounded team/type/region/preference/race lookups, country and race-class context, all 14 ability pairs, game display names, REAL potential values, exact empty and ordered favorite-list encoding, duplicate and missing race IDs, changed race revisions, deterministic defaults, nullable Limits, locked cross-links, checked `MAX + 1`, stale lookup rows and maxima, overflow, atomic rollback, readable lookup views, mutation-view and unknown-column rejection, BLOB restrictions, cancellation, and two-row Undo/Redo. Recovery fixtures cover teams, explicit and duplicate IDs, missing fitness rows, optional team schema, changed rosters, and no-op behavior.

## Optional local CDB round-trip test

`AuthorizedCdbSmokeTests.AuthorizedCdbsRoundTripOnlyThroughCopyFirstSessions` is part of the integration test assembly and is discovered during an ordinary integration run. When `PCM_CDB_AUTHORIZED_SMOKE` is not exactly `1`, the method returns immediately. A normal passing run therefore does not include a real-CDB round trip.

This opt-in test requires two local CDBs that you own or have permission to test. It hashes each source before and after, performs copy-first round trips in isolated sessions, and verifies that the sources remain unchanged. Do not use any database without its owner's permission.

```powershell
$env:PCM_CDB_AUTHORIZED_SMOKE = '1'
$env:PCM_CDB_SMOKE_1 = 'C:\path\to\first-authorized.cdb'
$env:PCM_CDB_SMOKE_2 = 'C:\path\to\second-authorized.cdb'

dotnet test tests/PcmCdbEditor.IntegrationTests/PcmCdbEditor.IntegrationTests.csproj --configuration Release --no-build --no-restore --disable-build-servers -m:1 --filter TestCategory=AuthorizedLocalSmoke

Remove-Item Env:PCM_CDB_AUTHORIZED_SMOKE
Remove-Item Env:PCM_CDB_SMOKE_1
Remove-Item Env:PCM_CDB_SMOKE_2
```

The opt-in test writes ignored output under `local-smoke/proof-<id>`. That directory can contain source paths, hashes, copied databases, and round-trip outputs. Keep it local, do not attach it to public issues or other shared artifacts, and remove it when you no longer need it.

## Manual Windows and accessibility checks

Source tests can check XAML names, automation IDs, and focus-management code, but they cannot check the rendered WinUI experience. Use the existing screen configuration and an isolated app data root for disposable preference checks. Before treating a local build as a release candidate, manually check at least:

- keyboard-only navigation through the shell, table tabs, filters, row editor, maintenance dialogs, preferences, and save prompts;
- visible focus and focus restoration after completed, cancelled, superseded, and failed operations;
- accessible names, help text, and control states for navigation, toolbar commands, loading and cancellation, dialogs, tables, and validation errors;
- **100 rows**, **250 rows**, **500 rows**, and **All rows (slower)** in that picker order;
- the **Rows to show** accessible description, including the warning that **All rows** loads every matching row and may require substantial time and memory, while retaining the `TablePageSize` automation ID;
- finite-choice persistence and 500-row round-tripping; **All rows** retention across search, filters, sorting, edits, Undo/Redo, refreshes, foreign-key display changes, and tab switching; close/reopen and new-editor reset to the last finite preference; unrelated preference saves; and an explicit finite-preference change on the selected tab;
- **All rows** status text and exact count, visible disabled **Previous** and **Next** controls, cancellation and failure rollback, and responsive behavior on a large disposable table;
- the completed query summary, **Refresh**, **Reset query**, and **Reset column layout**, including cancellation and the distinction between an empty table and zero query matches;
- current-cell inspection with a different row checked, typed values and long text in **Row details**, identity-less view and nullable-key rows across tabs, and clamping after the final finite page loses its last match;
- normal successful open at wide and narrow window widths, including persistent **Saved** or **Unsaved changes** status, incomplete history help, selectable paths in **Save details**, command overflow, and wrapped operation, recovery, and error messages;
- multi-field Edit/Insert drafts after invalid numbers, NULL restrictions, and SQLite constraint rejection; error focus and correction, exact single commit/Undo, separate uncreated rider-draft decisions after Save and cancelled close/replacement, and incomplete history remaining unsaved after recovery and subsequent Undo;
- open, edit, Undo/Redo, **Save**, **Save as**, backup, dirty-close, and interrupted-session recovery flows on disposable copies;
- **Changes** before and after a typed edit, Undo, Redo, and Save. Compare the field's storage class and values with the working row. Verify the next actions and saved baseline when undoing past a save or abandoning a redo branch;
- external destination changes on a run-owned CDB. Verify that **Cancel** preserves both the destination bytes and the unsaved working copy. Exercise **Save as** and explicit overwrite. Verify both paths in **Save details** and compare the backup with the displaced file;
- restart with multiple recoverable sessions. Select by path and timestamp, cancel a discard, resume the intended session, and verify incomplete history remains visible until an explicit save. Confirm clean-session cleanup preserves recoverable sessions and backups. Check **Recent databases** in the empty workspace and command overflow;
- double-click, Enter, and F2 inline edits for Integer, Real, and Text cells; click-away commit, Escape cancellation, invalid input, rapid repeated edits, Undo/Redo, multi-selection, current-column focus, and viewport stability;
- immediately after F2 → Enter commits and refreshes a row, select a row by its selector and with Ctrl+A without first pressing Escape; repeat after Undo/Redo and table switching. Selection and the inspector must remain usable, with no hidden next-cell editor left active by the control's Enter continuation;
- header sorting on wide and horizontally scrolled tables, including first activation descending, repeated activation toggling, preserved secondary priorities, **Sort** dialog parity and clearing, database-wide order across pages, per-tab retention, fresh-table and fresh-editor reset, rollback on cancellation or failure, busy-state disabling, keyboard access through **Sort**, a single primary arrow, and stable header, row, and viewport alignment;
- related-name search on different copied careers, including finite-page and **All rows** count agreement, stored-key and related-name filters, numeric key and alphabetical name sorts, and committed sort retention across display modes. Use synthetic schemas for missing targets, ambiguous keys, unavailable labels, and composite relationships;
- relationship selection from a grid cell and **Edit row**, including keyboard search, choosing between duplicate labels, cancelling with Escape, raw entry, nullable keys, and typed-key Undo and Redo. Use synthetic schemas for unusual keys, stale targets, and competing relationships. Verify that a refused change retains the draft;
- **Open referenced row** and **Return to source**, including a target already open in a tab, a reference to the same table, a read-only source row, and restoration of the source filter, sort, page, selection, and viewport;
- rider recovery with a long team name, an empty team, manually entered IDs, missing IDs, and **Use selected rows** without automatic input replacement;
- At narrow and wide widths, navigate away during Create Rider preparation and change a draft during preview. Verify that returning retains the draft and that only the current draft can publish a review or move focus.
- Walk all six Create Rider steps. Exercise game-name override/reset, keyboard favorite-race selection, removal and reordering, observed profile guidance, the 14-row ability matrix, bulk Current and Limit values, Copy Current to Limits, Clear Limits, and Current ability override/reset. Check blank-Limit acknowledgement, invalid-field focus, collapsed Advanced groups, role codes, generated IDs, and reviewed profile values.
- After creating, inspect both generated rows from the receipt. Check that Undo disables the receipt actions and Redo restores them. Save and reopen the copied career.
- keyboard-only operation and focus restoration after Create Rider and maintenance dialogs.

Run the nonzero-offset header-sort regression exactly as follows:

1. Open a table wide enough to require horizontal scrolling, make a visible middle-column cell current, then scroll about 60 device-independent pixels away from the left edge so the horizontal offset is clearly nonzero.
2. Without returning to the left edge, click a visible column header and wait for the descending database query and row bind to finish.
3. Confirm that the app did not crash, the horizontal position was preserved, every visible header boundary still aligns with its row-cell boundary, and exactly one descending primary-sort arrow is visible.
4. Click the same header again, wait for the ascending reload, and repeat the alignment, position, arrow, and no-crash checks.
5. Switch to a narrower table tab and back. Confirm that the exact saved position, header and row alignment, current cell, and ascending primary-sort arrow are restored.

The regression covered by this sequence shifted the header row left by roughly the saved horizontal offset while the column widths remained unchanged. A passing check must not show that translation at any step.

Verify sort lifetime separately: sort two open table tabs and switch between them to confirm that each retains its own sort. Close and reopen one table, then confirm that **Sort** has no count, no header arrow is visible, and the query starts unsorted. Repeat that unsorted check after replacing or opening the CDB and after restarting the application. Confirm throughout that the saved column widths, order, visibility, and frozen-column state still restore.

Record only non-sensitive observations. Do not include real database contents, local absolute paths, session metadata, screenshots with private rows, or `local-smoke` evidence in a public report.

## Local release verification

Routine unit and integration tests do not create or validate a distributable package. `eng/Build-Release.ps1` performs the locked build, tests, self-contained publish, dependency and content checks, SBOM creation, ZIP and installer production, and checksum generation. It downloads and runs a pinned Inno Setup build tool and replaces the existing `artifacts/release/<version>` directory, so read [Release and verification](release-and-verification.md) before running it.

After the script creates a local candidate, verify it with:

```powershell
./eng/Verify-Release.ps1 -Version 0.1.0
```

The verifier checks the generated artifacts. It does not sign or publish them, establish SmartScreen reputation, replace an installed-app accessibility pass, or establish compatibility with every Pro Cycling Manager database version.

The standalone verifier is not read-only: it rewrites the content and dependency allow/deny records plus `SHA256SUMS.txt`. It also requires the current Git dirty-state flag and status-entry count to match the state recorded for the candidate. Run it only against the intended candidate, without changing the source tree after creating that candidate.
