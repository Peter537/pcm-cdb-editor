# PCM CDB Editor

[![Version: 0.1.0 prerelease](https://img.shields.io/badge/version-0.1.0%20prerelease-176B87)](docs/release-and-verification.md)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![WinUI 3](https://img.shields.io/badge/WinUI-3-0078D4?logo=windows11&logoColor=white)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?logo=windows11&logoColor=white)](docs/getting-started.md#requirements)

I built PCM CDB Editor as a Windows desktop editor for Pro Cycling Manager CDB save files. The app works on an isolated copy, provides schema-aware browsing and typed SQLite editing, and creates staged CDB output through the bundled SQLiteExporter tool.

> **Version 0.1.0 is an unsigned prerelease for building and testing from source.** I have not published a supported binary. Real-world testing is still limited, so expect bugs, untested edge cases, and compatibility problems with some PCM releases or CDB files.

## Capabilities

- Copy-first open, save, backup, and recovery workflows
- Bounded table browsing with search, filters, multi-column sorting, and lazy counts
- Typed inline and full-row editing for safely identified rows, with inline mutations deferred until TableView finishes its commit lifecycle
- Disk-backed undo and redo across row edits and confirmed maintenance operations
- Schema-aware foreign-key display and persisted table layouts
- A dedicated, preview-first **Create Rider** wizard, plus **Rider recovery preset**, **January 1 season-stage repair**, and **World and European country quotas** when their schema and date requirements are met

Rider recovery accepts an entire current team roster or manually entered rider IDs; selecting table rows is an explicit convenience rather than a requirement. Create Rider guides identity, profile, ordered favorite races, all 14 Current/Limit ability pairs, potential, contract data, advanced fields, and review. Its game display name follows `Last name F.` until it is manually overridden. The workflow starts from schema-gated clean defaults and inserts one `DYN_contract_cyclist` row and one `DYN_cyclist` row as a guarded, undoable operation.

## Quick start

Use Windows x64 and the .NET SDK selected by `global.json`. The first command must report `10.0.303`.

```powershell
dotnet --version
dotnet restore PcmCdbEditor.slnx --locked-mode --configfile NuGet.Config --disable-parallel --disable-build-servers -p:RestoreUseStaticGraphEvaluation=false -p:BuildInParallel=false -m:1
dotnet run --project src/PcmCdbEditor.App/PcmCdbEditor.App.csproj --configuration Release --no-restore
```

Read [Getting started](docs/getting-started.md) for the complete development workflow and [Testing](docs/testing.md) for verification commands and test boundaries.

## Safety

- SQLiteExporter never receives the original CDB; editing happens inside `%LOCALAPPDATA%\PcmCdbEditor\Sessions`.
- Save requires the staged CDB to exist and be non-empty, then backs up an existing non-empty destination before replacement.
- Views and virtual tables are always read-only; ordinary tables without a verified declared primary key or safe `rowid` are also read-only.
- Sessions and backups can contain sensitive game data. Keep an independent backup and do not attach them to public issues.

See [Safety and recovery](docs/safety-and-recovery.md) for lifecycle, failure, and recovery boundaries.

## Documentation

- [Getting started](docs/getting-started.md) — requirements, build, launch, and first use
- [Architecture](docs/architecture.md) — project boundaries and runtime composition
- [Safety and recovery](docs/safety-and-recovery.md) — open, save, backup, and recovery invariants
- [Maintenance tools](docs/specialized-tools.md) — schema gates, previews, and calculation rules
- [Testing](docs/testing.md) — test suites, commands, fixtures, and validation scope
- [Release and verification](docs/release-and-verification.md) — local release builds and verification

## Licensing

I release the first-party code and documentation under the [MIT License](LICENSE). The bundled SQLiteExporter executable and PDF are separate third-party artifacts and are not covered by that license. See [Third-party notices](THIRD_PARTY_NOTICES.md).
