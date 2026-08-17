# Architecture

PCM CDB Editor separates product models and orchestration from its Windows, SQLite, filesystem, process, registry, and UI integrations. Four projects enforce those boundaries.

```mermaid
flowchart LR
    User["Windows user"] -.-> App["PcmCdbEditor.App<br/>WinUI shell and TableView adapter"]
    App --> Application["PcmCdbEditor.Application<br/>ports and virtual-query coordination"]
    App --> Infrastructure["PcmCdbEditor.Infrastructure<br/>SQLite, workspace, history, settings, process, registry"]
    Infrastructure --> Application
    Application --> Domain["PcmCdbEditor.Domain<br/>immutable schema, query, edit, session models"]
    Infrastructure --> Domain
    Infrastructure -.-> Exporter["SQLiteExporter.exe<br/>opaque non-shell process boundary"]
    Infrastructure -.-> Session["isolated local app data<br/>working CDB, SQLite, metadata, history"]
```

Solid arrows between projects show compile-time dependencies. `PcmCdbEditor.App` references Infrastructure to construct Application port implementations at startup; product behavior otherwise crosses Application contracts. Dotted arrows show user or runtime interactions. `WinUI.TableView` types remain inside `TableGridAdapterControl`.

## Project responsibilities

### Domain

`PcmCdbEditor.Domain` defines immutable SQLite storage values, row identities and revisions, runtime schema metadata, page/filter/sort requests, row-count state, view preferences, lifecycle models, reversible edit operations, replay guards, and maintenance previews. It has no WinUI, SQLite-package, process, registry, or filesystem dependency.

### Application

`PcmCdbEditor.Application` owns ports for conversion, workspaces, schema discovery, table data, edit history and replay, settings, file association, the grid adapter, and maintenance. It also owns:

- identifier validation against the current schema catalog;
- the numbered `AND`/`OR` advanced-filter parser;
- stable query models;
- operation gates that prevent conflicting work and suppress stale asynchronous completions; and
- bounded virtual-query coordination with independently cancellable row and count work.

No package-specific UI event or SQLite connection type crosses these contracts.

### Infrastructure

`PcmCdbEditor.Infrastructure` implements the edge ports:

- non-shell exporter execution with literal argument lists, bounded sanitized diagnostics, cancellation, timeout, and process-tree termination;
- copy-first sessions, staged import, backup-before-replacement for existing non-empty destinations, guarded fallback, and recovery enumeration;
- runtime table/view/column/key/FK discovery, conservative relationship inference, signatures, and safe edit-capability classification;
- an internal SQLite operation runner that moves UI-reachable catalog, query/count, edit/replay, workspace validation, and maintenance work off the WinUI dispatcher;
- native cancellation through `sqlite3_interrupt`, with the interrupt registration disposed before its connection and a canceled `SQLITE_INTERRUPT` translated to `OperationCanceledException`;
- bounded parameterized queries, counts, and searches, plus transactional typed insert, update, and delete operations;
- batched foreign-key display resolution and stable database-side sorting;
- atomic settings and schema-signature-keyed view state;
- disk-backed session history and guarded replay for updates, inserts, deletes, and maintenance commands; and
- per-user Windows file association plus the three schema-gated maintenance services.

History snapshots live inside the active session because undoing deletes and maintenance requires complete typed rows. Settings and view state do not contain database row values, but recent-file settings can contain local filesystem paths.

### App

`PcmCdbEditor.App` provides the high-density WinUI 3 shell, responsive navigation, command/status surfaces, multiple table tabs, row inspector, recovery and confirmation dialogs, settings, and maintenance previews. It composes:

- copy-first picker, command-line, recent-file, and association activation;
- target-named table loading in which only the current request may bind rows, change status, or dismiss the loading surface;
- cancellation that restores the previous completed tab and focus, plus lazy per-tab counts that update count text without rebinding rows;
- bounded virtual browsing and lazy counts;
- raw, resolved-name, and `raw | name` FK display modes;
- typed inline edits that preserve the existing SQLite storage class;
- a full-row editor with explicit Integer, Real, Text, and NULL choices while treating BLOBs as read-only metadata;
- disk-backed undo/redo with saved-baseline tracking; and
- theme, density, page size, recent files, view state, and file-association controls.

Inline commits use the row identity and revision captured with the visible page, then pass through the same transactional edit, dirty-state, history, refresh, and error path as row-inspector edits. The TableView adapter prepares a completed page before replacing its source and reuses compatible columns, selection, and viewport state.

## Runtime data boundaries

- **Original CDB:** user-owned input; validated and copied, never passed directly to the exporter.
- **Session:** `%LOCALAPPDATA%\PcmCdbEditor\Sessions\<session-id>`; copied CDB, working SQLite, lifecycle metadata, and edit history.
- **Backup:** `%LOCALAPPDATA%\PcmCdbEditor\Backups`; retained separately from session cleanup.
- **Settings:** theme, density, page size, FK mode, recent CDB paths, and schema-keyed table layout; no database row values.
- **Converter:** the bundled `SQLiteExporter.exe`; an opaque third-party process that receives only session copies and staged output paths.
- **Release payload:** one staged self-contained `win-x64` directory intended to be used unchanged for ZIP and installer input.

Lifecycle guarantees are documented in [Safety and recovery](safety-and-recovery.md). Packaging boundaries are documented in [Release and verification](release-and-verification.md).
