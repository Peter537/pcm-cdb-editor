# Testing

The automated suites use synthetic fixtures by default, so they do not require or disclose a real Pro Cycling Manager database. Test totals will change as coverage grows; use the commands below and check that they pass.

## Prerequisites

Complete the locked restore and Release/x64 build in [Getting started](getting-started.md) before using the `--no-build --no-restore` commands below.

## Unit tests

```powershell
dotnet test tests/PcmCdbEditor.UnitTests/PcmCdbEditor.UnitTests.csproj --configuration Release --no-build --no-restore --disable-build-servers -m:1
```

The unit suite covers domain and view-model state, row identity and typed SQLite values, filter parsing and query composition, bounded paging and request coordination, Undo/Redo models, maintenance review projections, rider-ID normalization, inline-edit staging and bind-generation invalidation, Create Rider input/range/role/lookup models, live Unicode-aware game-name generation and overrides, potential validation, ordered favorite-list validation and serialization, busy-to-ready command availability, architecture boundaries, mutation write-ahead behavior, and source-level wizard, UI, and accessibility contracts.

## Integration tests

```powershell
dotnet test tests/PcmCdbEditor.IntegrationTests/PcmCdbEditor.IntegrationTests.csproj --configuration Release --no-build --no-restore --disable-build-servers -m:1
```

The integration suite uses generated SQLite databases and fake converter processes to exercise schema discovery, typed querying and mutation, delete and trigger safety, history and settings persistence, workspace open/save/recovery behavior, converter bounds and cancellation, maintenance services, and paging responsiveness. Create Rider fixtures cover clean draft preparation, bounded team/type/region/preference/race lookups, country and race-class context, all 14 ability pairs, game display names, REAL potential values, exact empty and ordered favorite-list encoding, duplicate and missing race IDs, changed race revisions, deterministic defaults, nullable Limits, locked cross-links, checked `MAX + 1`, stale lookup rows and maxima, overflow, atomic rollback, readable lookup views, mutation-view and unknown-column rejection, BLOB restrictions, cancellation, and two-row Undo/Redo. Recovery fixtures cover teams, explicit and duplicate IDs, missing fitness rows, optional team schema, changed rosters, and no-op behavior.

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

Source tests can check XAML names, automation IDs, and focus-management code, but they cannot check the rendered WinUI experience. Before treating a local build as a release candidate, manually check at least:

- keyboard-only navigation through the shell, table tabs, filters, row editor, maintenance dialogs, preferences, and save prompts;
- visible focus and focus restoration after completed, cancelled, superseded, and failed operations;
- Narrator announcements and accessible names for navigation, toolbar commands, loading/cancellation state, dialogs, tables, and validation errors;
- system, light, and dark themes; compact and comfortable density; 100- and 250-row pages; display scaling; and high-contrast behavior;
- open, edit, Undo/Redo, **Save**, **Save as**, backup, dirty-close, and interrupted-session recovery flows on disposable copies.
- double-click, Enter, and F2 inline edits for Integer, Real, and Text cells; click-away commit, Escape cancellation, invalid input, rapid repeated edits, Undo/Redo, multi-selection, current-column focus, and viewport stability;
- rider recovery with a long team name, an empty team, manually entered IDs, missing IDs, and **Use selected rows** without automatic input replacement;
- Create Rider at narrow and wide window sizes: all six steps, long rider/team/region/race names, lookup loading and errors, live game-name generation plus override/reset, keyboard race selection/removal/reordering, optional-empty favorite warning, potential range and increments, observed height/weight guidance, the 14-row matrix and both bulk actions, Current-above-Limit warnings, blank-Limit acknowledgement, collapsed Advanced groups, BLOB omission and non-editability, role labels with codes, allocation review, the preview-busy-to-Ready button transition, stale/error states, apply, Undo, and Redo;
- Create Rider and the maintenance workflows at 200% text scaling in system, light, and dark themes, including keyboard-only operation, reduced motion, and focus restoration after dialogs.

Record only non-sensitive observations. Do not include real database contents, local absolute paths, session metadata, screenshots with private rows, or `local-smoke` evidence in a public report.

## Local release verification

Routine unit and integration tests do not create or validate a distributable package. `eng/Build-Release.ps1` performs the locked build, tests, self-contained publish, dependency and content checks, SBOM creation, ZIP and installer production, and checksum generation. It downloads and runs a pinned Inno Setup build tool and replaces the existing `artifacts/release/<version>` directory, so read [Release and verification](release-and-verification.md) before running it.

After the script creates a local candidate, verify it with:

```powershell
./eng/Verify-Release.ps1 -Version 0.1.0
```

The verifier checks the generated artifacts. It does not sign or publish them, establish SmartScreen reputation, replace an installed-app accessibility pass, or establish compatibility with every Pro Cycling Manager database version.

The standalone verifier is not read-only: it rewrites the content and dependency allow/deny records plus `SHA256SUMS.txt`. It also requires the current Git dirty-state flag and status-entry count to match the state recorded for the candidate. Run it only against the intended candidate, without changing the source tree after creating that candidate.
