# Third-party notices

PCM CDB Editor 0.1.0 includes the third-party files and packages listed below. The repository's MIT license covers the first-party code and documentation only; it does not license these separate materials.

Package authors' license files and terms control. This notice records what is included and what I currently understand about it; it is not a legal conclusion.

## SQLiteExporter

The repository contains these third-party files, and the release script stages them without modification:

| File | SHA-256 |
| --- | --- |
| `third_party/SQLiteExporter/SQLiteExporter.exe` | `5F1F3F72C5BF5537E74DE1A0FC732B1CBDF191E6CA3A3E9A12F18BBE15C1951D` |
| `third_party/SQLiteExporter/Readme.pdf` | `CF05919B2BDB7873F4CAFAA98126F2143195BFF1D0A937EF55B00E164EF7DED6` |

According to my record of their origin, a PCM employee publicly shared these exact files in the official Pro Cycling Manager Discord. `Readme.pdf` is the three-page usage guide supplied with the executable, and I found no visible redistribution terms in it. I understand where the files came from, but I have not found written permission that clearly allows me to redistribute either one. Their presence and hashes here do not claim that redistribution is permitted.

As a project policy, I keep these files unchanged and do not decompile, recompile, or substitute them. A hash mismatch blocks packaging and requires me to review the files again. `SqliteExporterUI.exe` is not included and is not allowed in the payload.

## Resolved NuGet inventory

The six project-local `packages.lock.json` files define the exact dependency graph. The classifications below come from the resolved packages' `.nuspec` license expressions or files.

### MIT expression declared

| Package(s) | Resolved version |
| --- | --- |
| `Microsoft.ApplicationInsights` | 2.23.0 |
| `Microsoft.CodeCoverage`, `Microsoft.NET.Test.Sdk`, `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TestHost` | 18.8.1 |
| `Microsoft.Data.Sqlite`, `Microsoft.Data.Sqlite.Core` | 10.0.11 |
| `Microsoft.Testing.Extensions.Telemetry`, `Microsoft.Testing.Extensions.TrxReport.Abstractions`, `Microsoft.Testing.Extensions.VSTestBridge`, `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild` | 2.3.3 |
| `MSTest.Analyzers`, `MSTest.TestAdapter`, `MSTest.TestFramework` | 4.3.3 |
| `System.Numerics.Tensors` | 9.0.0 |
| `WinUI.TableView` | 1.4.1 |

### Apache-2.0 expression declared

| Package(s) | Resolved version |
| --- | --- |
| `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.core`, `SQLitePCLRaw.lib.e_sqlite3`, `SQLitePCLRaw.provider.e_sqlite3` | 2.1.12 |

`SQLitePCLRaw.lib.e_sqlite3` supplies the native SQLite library used in the Windows payload. The SQLite project describes SQLite source as dedicated to the public domain in its [official copyright statement](https://www.sqlite.org/copyright.html). That statement and SQLitePCLRaw's Apache-2.0 package terms apply at different layers, so both need review before a packaged release.

### Microsoft package-specific license files or SDK terms

These packages do not declare the MIT/Apache expressions above. Their resolved packages carry package-specific `license.txt`, `LICENSE.txt`, `sdk_license.txt`, NOTICE/third-party files, or an SDK license URL:

| Package(s) | Resolved version | Package evidence |
| --- | --- | --- |
| `Microsoft.Web.WebView2` | 1.0.3719.77 | `LICENSE.txt`, `NOTICE.txt` |
| `Microsoft.Windows.AI.MachineLearning` | 2.1.74 | `license.txt`, `ThirdPartyNotices.txt` |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2526 | NuSpec Windows SDK license URL; build-only/private asset |
| `Microsoft.Windows.SDK.BuildTools.MSIX` | 1.7.251221100 | `sdk_license.txt`, `NOTICE.txt` |
| `Microsoft.WindowsAppSDK` | 2.3.1 | `license.txt`, `NOTICE.txt` |
| `Microsoft.WindowsAppSDK.AI` | 2.3.4 | `license.txt` |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 | `license.txt`, `NOTICE.txt` |
| `Microsoft.WindowsAppSDK.DWrite` | 2.1.0 | `license.txt` |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.5 | `license.txt` |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.3 | `license.txt` |
| `Microsoft.WindowsAppSDK.ML` | 2.1.74 | `license.txt`, `ThirdPartyNotices.txt` |
| `Microsoft.WindowsAppSDK.Runtime` | 2.3.1 | `license.txt`, `NOTICE.txt` |
| `Microsoft.WindowsAppSDK.Widgets` | 2.0.5 | `license.txt` |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.0 | `license.txt`, `NOTICE.txt` |

## Build-only tools

- `Microsoft.Sbom.DotNetTool` 4.1.5 is pinned in the local tool manifest and used to generate the SPDX SBOM. It is not part of the application payload; its own package license and notices still apply when the tool is restored.
- Inno Setup 7.1.0 is downloaded only by the release script from its immutable official release, then verified by SHA-256, release attestation, and Authenticode publisher before execution. It remains under its own license and is not redistributed with the application.

The release build generates an SPDX SBOM and dependency inventory for the final payload. They support, but do not replace, review of package license and notice files. I do not treat the SBOM or any release artifact as complete until the full release build and verifier succeed.
