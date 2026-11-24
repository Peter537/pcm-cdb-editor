# Release and verification

The local release script creates an **unsigned verification candidate**. It does not create a tag, GitHub Release, public asset, or supported product release. Candidates remain prerelease artifacts until the publication checks in this document are complete.

## Authoritative inputs

Release identities and policy live in the files that the build and verifier consume:

- [global.json](../global.json) selects the .NET SDK.
- [Directory.Build.props](../Directory.Build.props) defines product, version, target framework, runtime identifier, and compiler policy.
- [PcmCdbEditor.App.csproj](../src/PcmCdbEditor.App/PcmCdbEditor.App.csproj) defines the Windows target, self-contained runtime shape, and application dependencies.
- [Directory.Packages.props](../Directory.Packages.props), the project lockfiles, and [NuGet.Config](../NuGet.Config) define the approved dependency graph and restore sources.
- [.config/dotnet-tools.json](../.config/dotnet-tools.json) and its adjacent integrity file define the repository-local SBOM tool.
- [Build-Release.ps1](../eng/Build-Release.ps1) pins and validates external build inputs, including the approved SQLiteExporter files and Inno Setup.
- [Verify-Release.ps1](../eng/Verify-Release.ps1) defines the candidate acceptance policy.
- [PcmCdbEditor.iss](../installer/PcmCdbEditor.iss) defines installer identity, scope, file association, and uninstall behavior.
- [Third-party notices](../THIRD_PARTY_NOTICES.md) records the licensing and redistribution boundary.

Review these sources when a version, package, tool, runtime, installer, or third-party artifact changes. Do not copy their complete pinned inventories into this guide.

## Build an unsigned candidate

Run the release build on Windows x64 with Git, GitHub CLI, network access, and the SDK selected by `global.json`:

```powershell
./eng/Build-Release.ps1 -Version 0.1.0
```

The script captures Git HEAD, the working-tree status, and the complete source file manifest before restoring packages. From a clean checkout it exports the recorded commit through a private Git index. A dirty checkout produces a local verification candidate from an isolated copy of its captured files. Use `-RequireCleanSource` to reject a dirty checkout before building.

Build, test, publish, and tool installation run in a unique scratch directory below `artifacts`. The script checks the captured inputs and live checkout again before writing provenance and promoting the verified candidate. Failed builds leave the current version in place. When a new candidate passes, the previous version is retained under `<version>.previous-<run-id>`. Review that directory after accepting the new version. The script prints its size and leaves its removal to the owner.

The script:

1. validates the SDK, NuGet policy, local tool manifest, and integrity records;
2. restores repository tools and all projects from the approved source in locked mode;
3. records a machine-readable direct and transitive dependency vulnerability audit and rejects findings;
4. builds the solution and runs both test projects in Release;
5. publishes one self-contained, unpackaged, untrimmed, non-AOT `win-x64` payload;
6. validates and stages the approved SQLiteExporter files together with the license and notices;
7. generates and validates an SPDX SBOM from the staged payload and dependency evidence;
8. creates a ZIP from that exact payload;
9. downloads the pinned Inno Setup asset, validates its digest, signature, and release attestation, and executes the compiler in portable, current-user mode;
10. builds the installer from the same payload used for the ZIP;
11. writes source, dependency, content, vulnerability, provenance, SBOM, and checksum evidence; and
12. invokes the independent release verifier against the generated candidate artifacts.

The command downloads and executes a validated external build tool. Source provenance records the initial commit, working-tree status digest, source manifest, snapshot mode, and local build environment. It records the serviced .NET runtime and app-host versions against Microsoft's runtime security metadata. The synthetic integration gate excludes owner-authorized real-CDB smoke tests.

## Candidate output and verification

The release directory has this stable shape:

```text
artifacts/release/<version>/
|-- payload/                         exact ZIP and installer input
|-- packages/
|   |-- PcmCdbEditor-<version>-win-x64.zip
|   `-- PcmCdbEditor-<version>-win-x64-setup.exe
|-- metadata/
|   |-- content-allow.json
|   |-- content-deny.json
|   |-- dependency-allow.json
|   |-- dependency-deny.json
|   |-- dependency-vulnerabilities.json
|   |-- release-provenance.json
|   |-- source-manifest.json
|   |-- resolved-restore-assets.json
|   |-- installed-payload.json       added after Windows Sandbox verification
|   |-- sbom-validation.json
|   `-- sbom/_manifest/spdx_2.2/manifest.spdx.json
`-- SHA256SUMS.txt
```

`Verify-Release.ps1` checks the following boundaries against the authoritative inputs:

- application identity, runtime shape, payload contents, and architecture;
- license, notices, SQLiteExporter integrity, and native SQLite payload shape;
- project lockfiles, restored package assets, allowed dependency versions, and the vulnerability report;
- the SPDX document and its required application and package components;
- source-manifest hashes, source-tree digest, commit, and dirty-tree provenance;
- exact equality between the staged payload and every ZIP entry;
- installer identity, unsigned status, and static per-user registration settings;
- local build-environment provenance, NuGet source policy, and release-script publication boundaries; and
- absence of source files, symbols, databases, logs, private paths, build trees, legacy components, and other denied payload content.

The standalone verifier reads candidate-specific resolved restore evidence after build scratch is removed. Use the captured checkout to reverify. `-RequireReleaseEvidence` fails if the candidate lacks a clean, committed snapshot or passing Windows Sandbox evidence bound to this installer.

## Installer behavior

The installer is per user and does not require elevation. It installs below `%LOCALAPPDATA%\Programs\PcmCdbEditor`, creates a Start Menu shortcut and optional desktop shortcut, and registers an HKCU **Open with** handler without taking over the current `.cdb` default. Uninstall removes application files and registration while preserving settings, sessions, and backups below `%LOCALAPPDATA%\PcmCdbEditor`.

Verify actual installation in Windows Sandbox on a compatible Windows x64 host with Windows Sandbox enabled:

```powershell
./eng/Test-InstallerPayload.ps1 -CandidateDirectory ./artifacts/release/0.1.0
./eng/Verify-Release.ps1 -Version 0.1.0 -RequireReleaseEvidence
```

The probe disables guest networking and maps only the read-only installer input and an isolated result directory. It compares every installed payload path, size, and SHA-256 against the ZIP-verified staging directory, allowing the two Inno-generated uninstall files. It verifies HKCU registration, the Start Menu shortcut, preservation of a seeded `.cdb` default, uninstall cleanup, and survival of synthetic user data. The release verifier checks the result against the current installer checksum and adds it to `SHA256SUMS.txt`.

Run `./eng/Test-ReleaseTooling.ps1` to check source capture, candidate promotion, and installer manifest rejection against synthetic fixtures. Windows Sandbox is needed for actual installation evidence. If a host or VM is forcibly terminated, close the task-owned process or Sandbox before removing only the scratch path printed for that run.

## Local candidate handling

Candidates are built and verified locally. The release script promotes the verified current candidate under `artifacts/release/<version>` by default and does not create a tag, GitHub Release, or public asset. Review the recorded local build environment and external-tool provenance before treating a candidate as publishable.

## Signing and publication

The current verifier requires the application and installer to remain unsigned. Adding signing requires a separately reviewed release-process change that defines the trusted publisher, signs the intended files, verifies signatures after signing, and regenerates checksums and provenance. Do not label an unsigned candidate as a supported release.

Before I publish a release, I will:

- review the generated dependency, vulnerability, SBOM, license, notice, content, and provenance evidence;
- build from a clean checkout and pass the installer evidence gate;
- complete the [manual Windows and accessibility checks](testing.md#manual-windows-and-accessibility-checks), including the nonzero-offset wide-table header-sort regression and session-only sort-lifetime checks; reject the candidate if headers and rows misalign, the application crashes, or a closed table's or previous editor's sort reappears;
- test the installed application, file association, uninstall behavior, and representative CDB round trips;
- review the recorded local build environment and external-tool provenance; and
- decide whether the separately licensed SQLiteExporter executable and PDF may be redistributed.

I have not found documented permission to redistribute the SQLiteExporter files. [Third-party notices](../THIRD_PARTY_NOTICES.md) records what I currently know; it is not legal advice.
