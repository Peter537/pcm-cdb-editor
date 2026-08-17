[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('0.1.0')]
    [string] $Version,

    [string] $PayloadDirectory,

    [string] $ZipPath,

    [string] $InstallerPath,

    [string] $SbomPath,

    [string] $MetadataDirectory,

    [string] $ChecksumsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ExporterSha256 = '5F1F3F72C5BF5537E74DE1A0FC732B1CBDF191E6CA3A3E9A12F18BBE15C1951D'
$script:ExporterReadmeSha256 = 'CF05919B2BDB7873F4CAFAA98126F2143195BFF1D0A937EF55B00E164EF7DED6'

$releaseDirectory = Join-Path $script:RepositoryRoot "artifacts\release\$Version"
$packagesDirectory = Join-Path $releaseDirectory 'packages'
if ([string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    $PayloadDirectory = Join-Path $releaseDirectory 'payload'
}
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $packagesDirectory "PcmCdbEditor-$Version-win-x64.zip"
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $packagesDirectory "PcmCdbEditor-$Version-win-x64-setup.exe"
}
if ([string]::IsNullOrWhiteSpace($SbomPath)) {
    $SbomPath = Join-Path $releaseDirectory 'metadata\sbom\_manifest\spdx_2.2\manifest.spdx.json'
}
if ([string]::IsNullOrWhiteSpace($MetadataDirectory)) {
    $MetadataDirectory = Join-Path $releaseDirectory 'metadata'
}
if ([string]::IsNullOrWhiteSpace($ChecksumsPath)) {
    $ChecksumsPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
}

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet('File', 'Directory')]
        [string] $Kind
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $exists = if ($Kind -eq 'File') {
        [System.IO.File]::Exists($fullPath)
    }
    else {
        [System.IO.Directory]::Exists($fullPath)
    }

    if (-not $exists) {
        throw "Required $($Kind.ToLowerInvariant()) does not exist: $fullPath"
    }

    return $fullPath
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][System.IO.Stream] $Stream)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    [System.Uri] $baseUri = $baseFullPath
    [System.Uri] $targetUri = $targetFullPath
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [System.Uri]::UnescapeDataString($relativeUri.ToString()).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $encoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Content, $encoding)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Value,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [System.IO.Directory]::Exists($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    Write-Utf8NoBom -Path $Path -Content ($Value | ConvertTo-Json -Depth 12)
}

function Get-PayloadFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    return @(
        Get-ChildItem -LiteralPath $Root -Recurse -Force -File |
            ForEach-Object {
                [pscustomobject]@{
                    Path = (Get-RelativePath -BasePath $Root -TargetPath $_.FullName).Replace('\', '/')
                    Size = $_.Length
                    Sha256 = Get-Sha256 -Path $_.FullName
                }
            } |
            Sort-Object -Property Path
    )
}

function Get-LockedDependencies {
    $lockRoots = @(
        (Join-Path $script:RepositoryRoot 'src'),
        (Join-Path $script:RepositoryRoot 'tests')
    )

    $lockFiles = @(
        Get-ChildItem -LiteralPath $lockRoots -Recurse -Force -File -Filter 'packages.lock.json' |
            Sort-Object -Property FullName
    )

    $projectFiles = @(
        Get-ChildItem -LiteralPath $lockRoots -Recurse -Force -File -Filter '*.csproj' |
            Sort-Object -Property FullName
    )
    $missingProjectLocks = @(
        $projectFiles |
            Where-Object { -not [System.IO.File]::Exists((Join-Path $_.DirectoryName 'packages.lock.json')) } |
            ForEach-Object { Get-RelativePath -BasePath $script:RepositoryRoot -TargetPath $_.FullName }
    )
    if ($missingProjectLocks.Count -gt 0) {
        throw "Every project must have an adjacent packages.lock.json; missing for: $($missingProjectLocks -join ', ')"
    }

    if ($lockFiles.Count -eq 0) {
        throw 'No committed packages.lock.json files were found under src or tests.'
    }

    $dependencies = [System.Collections.Generic.List[object]]::new()
    foreach ($lockFile in $lockFiles) {
        $lock = Get-Content -Raw -LiteralPath $lockFile.FullName | ConvertFrom-Json
        if ($null -eq $lock.dependencies) {
            throw "Lock file has no dependencies object: $($lockFile.FullName)"
        }

        foreach ($target in $lock.dependencies.PSObject.Properties) {
            foreach ($package in $target.Value.PSObject.Properties) {
                $type = [string] $package.Value.type
                if ($type -eq 'Project') {
                    continue
                }

                $dependencies.Add([pscustomobject]@{
                    Project = (Get-RelativePath -BasePath $script:RepositoryRoot -TargetPath $lockFile.DirectoryName).Replace('\', '/')
                    Target = $target.Name
                    Id = $package.Name
                    Type = $type
                    Version = [string] $package.Value.resolved
                    ContentHash = [string] $package.Value.contentHash
                })
            }
        }
    }

    return @($dependencies | Sort-Object -Property Id, Version, Project, Target)
}

function Get-ResolvedRiskyAssets {
    $projectRoots = @(
        (Join-Path $script:RepositoryRoot 'src'),
        (Join-Path $script:RepositoryRoot 'tests')
    )
    $projectFiles = @(
        Get-ChildItem -LiteralPath $projectRoots -Recurse -Force -File -Filter '*.csproj' |
            Sort-Object -Property FullName
    )
    $riskyAssetNames = @{
        'native' = $true
        'build' = $true
        'buildmultitargeting' = $true
        'buildtransitive' = $true
        'runtimetargets' = $true
    }
    $resolvedAssets = [System.Collections.Generic.List[object]]::new()

    foreach ($projectFile in $projectFiles) {
        $assetsPath = Join-Path $projectFile.DirectoryName 'obj\project.assets.json'
        if (-not [System.IO.File]::Exists($assetsPath)) {
            throw "Restored target-asset evidence is missing for $($projectFile.FullName)."
        }

        $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
        if ($null -eq $assets.targets) {
            throw "Restored target-asset evidence has no targets object: $assetsPath"
        }

        foreach ($target in $assets.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                if ([string] $library.Value.type -ne 'package') {
                    continue
                }

                $assetKindSet = New-Object 'System.Collections.Generic.HashSet[string]' (
                    [System.StringComparer]::OrdinalIgnoreCase)
                foreach ($assetProperty in $library.Value.PSObject.Properties) {
                    if ($riskyAssetNames.ContainsKey($assetProperty.Name.ToLowerInvariant())) {
                        [void]$assetKindSet.Add($assetProperty.Name)
                    }

                    if ($assetProperty.Name.Equals('build', [System.StringComparison]::OrdinalIgnoreCase)) {
                        foreach ($buildAsset in $assetProperty.Value.PSObject.Properties) {
                            if ($buildAsset.Name -match '(?i)^buildTransitive[\\/]') {
                                [void]$assetKindSet.Add('buildTransitive')
                            }
                            elseif ($buildAsset.Name -match '(?i)^buildMultiTargeting[\\/]') {
                                [void]$assetKindSet.Add('buildMultiTargeting')
                            }
                        }
                    }
                }
                $assetKinds = @($assetKindSet | Sort-Object)
                if ($assetKinds.Count -eq 0) {
                    continue
                }

                $separator = $library.Name.LastIndexOf('/')
                if ($separator -le 0 -or $separator -eq $library.Name.Length - 1) {
                    throw "Unable to parse restored package identity '$($library.Name)' in $assetsPath."
                }

                $resolvedAssets.Add([pscustomobject]@{
                    Project = (Get-RelativePath -BasePath $script:RepositoryRoot -TargetPath $projectFile.FullName).Replace('\', '/')
                    Target = $target.Name
                    Id = $library.Name.Substring(0, $separator)
                    Version = $library.Name.Substring($separator + 1)
                    AssetKinds = $assetKinds
                })
            }
        }
    }

    return @(
        $resolvedAssets |
            Sort-Object -Property Id, Version, Project, Target
    )
}

function Assert-ZipMatchesPayload {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $PayloadFiles,

        [Parameter(Mandatory = $true)]
        [string] $ArchivePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $payloadMap = @{}
    foreach ($file in $PayloadFiles) {
        $payloadMap[$file.Path] = $file.Sha256
    }

    $zipMap = @{}
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }

            $entryPath = $entry.FullName.Replace('\', '/')
            if ($entryPath.StartsWith('/') -or $entryPath.Contains('../') -or $entryPath.Contains('/..')) {
                throw "ZIP contains an unsafe path: $entryPath"
            }

            if ($zipMap.ContainsKey($entryPath)) {
                throw "ZIP contains a duplicate path: $entryPath"
            }

            $stream = $entry.Open()
            try {
                $zipMap[$entryPath] = Get-StreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $missing = @($payloadMap.Keys | Where-Object { -not $zipMap.ContainsKey($_) } | Sort-Object)
    $extra = @($zipMap.Keys | Where-Object { -not $payloadMap.ContainsKey($_) } | Sort-Object)
    $different = @(
        $payloadMap.Keys |
            Where-Object { $zipMap.ContainsKey($_) -and $zipMap[$_] -ne $payloadMap[$_] } |
            Sort-Object
    )

    if ($missing.Count -gt 0 -or $extra.Count -gt 0 -or $different.Count -gt 0) {
        throw "ZIP/payload mismatch. Missing: [$($missing -join ', ')]; extra: [$($extra -join ', ')]; hash mismatch: [$($different -join ', ')]"
    }
}

$payloadRoot = Resolve-RequiredPath -Path $PayloadDirectory -Kind Directory
$zipFullPath = Resolve-RequiredPath -Path $ZipPath -Kind File
$installerFullPath = Resolve-RequiredPath -Path $InstallerPath -Kind File
$sbomFullPath = Resolve-RequiredPath -Path $SbomPath -Kind File
$metadataRoot = [System.IO.Path]::GetFullPath($MetadataDirectory)
[System.IO.Directory]::CreateDirectory($metadataRoot) | Out-Null
$checksumsFullPath = [System.IO.Path]::GetFullPath($ChecksumsPath)

$payloadFiles = @(Get-PayloadFiles -Root $payloadRoot)
if ($payloadFiles.Count -eq 0) {
    throw 'The staged release payload is empty.'
}

$requiredPayloadFiles = @(
    'App.xbf',
    'MainWindow.xbf',
    'CountryQuotaPreviewDialog.xbf',
    'Controls/TableGridAdapterControl.xbf',
    'PcmCdbEditor.pri',
    'PcmCdbEditor.exe',
    'PcmCdbEditor.dll',
    'PcmCdbEditor.deps.json',
    'PcmCdbEditor.runtimeconfig.json',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'third_party/SQLiteExporter/SQLiteExporter.exe',
    'third_party/SQLiteExporter/Readme.pdf'
)

$payloadPaths = @($payloadFiles.Path)
$missingRequired = @($requiredPayloadFiles | Where-Object { $_ -notin $payloadPaths })
if ($missingRequired.Count -gt 0) {
    throw "Payload is missing required files: $($missingRequired -join ', ')"
}

$nativeSqliteFiles = @($payloadFiles | Where-Object { [System.IO.Path]::GetFileName($_.Path) -ieq 'e_sqlite3.dll' })
if ($nativeSqliteFiles.Count -ne 1) {
    throw "Payload must contain exactly one e_sqlite3.dll; found $($nativeSqliteFiles.Count)."
}

$runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $payloadRoot 'PcmCdbEditor.runtimeconfig.json')
if ($runtimeConfig -notmatch '"version"\s*:\s*"10\.0\.11"') {
    throw 'The self-contained runtime configuration does not record runtime 10.0.11.'
}

$publishedDependencies = Get-Content -Raw -LiteralPath (Join-Path $payloadRoot 'PcmCdbEditor.deps.json') | ConvertFrom-Json
$publishedRuntimePacks = @(
    $publishedDependencies.libraries.PSObject.Properties |
        Where-Object { $_.Name -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' }
)
if ($publishedRuntimePacks.Count -ne 1 -or
    $publishedRuntimePacks[0].Name -ne 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.11') {
    throw 'The staged self-contained payload does not resolve the win-x64 .NET runtime pack to 10.0.11 exactly.'
}

$appProjectPath = Join-Path $script:RepositoryRoot 'src\PcmCdbEditor.App\PcmCdbEditor.App.csproj'
[xml] $appProject = Get-Content -Raw -LiteralPath $appProjectPath
$buildPropsPath = Join-Path $script:RepositoryRoot 'Directory.Build.props'
[xml] $buildProps = Get-Content -Raw -LiteralPath $buildPropsPath
$productVersionProperties = @($buildProps.SelectNodes('/Project/PropertyGroup[Version]'))
if ($productVersionProperties.Count -ne 1 -or
    [string] $productVersionProperties[0].Product -cne 'PCM CDB Editor' -or
    [string] $productVersionProperties[0].Company -cne 'Peter537' -or
    [string] $productVersionProperties[0].VersionPrefix -cne '0.1.0' -or
    [string] $productVersionProperties[0].Version -cne '0.1.0' -or
    [string] $productVersionProperties[0].AssemblyVersion -cne '0.1.0.0' -or
    [string] $productVersionProperties[0].FileVersion -cne '0.1.0.0' -or
    [string] $productVersionProperties[0].InformationalVersion -cne '0.1.0-unreleased' -or
    [string] $productVersionProperties[0].PackageVersion -cne '0.1.0') {
    throw 'Central product, publisher, package, assembly, file, or unreleased version metadata has drifted from 0.1.0.'
}
$runtimeFrameworkOverride = @($appProject.SelectNodes(
    "/Project/ItemGroup/KnownFrameworkReference[@Update='Microsoft.NETCore.App']"))
$appHostOverride = @($appProject.SelectNodes(
    "/Project/ItemGroup/KnownAppHostPack[@Update='Microsoft.NETCore.App']"))
$latestPatchProperties = @($appProject.SelectNodes(
    '/Project/PropertyGroup[TargetLatestRuntimePatch]'))
if ($runtimeFrameworkOverride.Count -ne 1 -or
    [string] $runtimeFrameworkOverride[0].LatestRuntimeFrameworkVersion -ne '10.0.11' -or
    $appHostOverride.Count -ne 1 -or
    [string] $appHostOverride[0].AppHostPackVersion -ne '10.0.11' -or
    $latestPatchProperties.Count -ne 1 -or
    [string] $latestPatchProperties[0].TargetLatestRuntimePatch -ne 'true') {
    throw 'The app project does not pin both the .NET runtime and apphost packs to 10.0.11 with TargetLatestRuntimePatch=true.'
}

if ((Get-Content -Raw -LiteralPath $appProjectPath) -match '<RuntimeFrameworkVersion>') {
    throw 'RuntimeFrameworkVersion must not be used on the Windows TFM; it also overrides Microsoft.Windows.SDK.NET.Ref.'
}

$buildScriptText = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Build-Release.ps1')
if ($buildScriptText -match '(?i)-p:RuntimeFrameworkVersion') {
    throw 'Build-Release.ps1 must not pass RuntimeFrameworkVersion; it also overrides Microsoft.Windows.SDK.NET.Ref.'
}

$projectAssetsPath = Join-Path $script:RepositoryRoot 'src\PcmCdbEditor.App\obj\project.assets.json'
if (-not [System.IO.File]::Exists($projectAssetsPath)) {
    throw 'The restored app project.assets.json is required to verify the exact runtime and apphost packs.'
}

$projectAssets = Get-Content -Raw -LiteralPath $projectAssetsPath | ConvertFrom-Json
$downloadDependencies = @(
    $projectAssets.project.frameworks.PSObject.Properties |
        ForEach-Object { @($_.Value.downloadDependencies) }
)
$runtimePackId = 'Microsoft.NETCore.App.Runtime.win-x64'
$resolvedRuntimePacks = @($downloadDependencies | Where-Object { $_.name -eq $runtimePackId })
if ($resolvedRuntimePacks.Count -ne 1 -or [string] $resolvedRuntimePacks[0].version -ne '[10.0.11, 10.0.11]') {
    throw "The restored $runtimePackId download dependency is not pinned to 10.0.11 exactly."
}

$appHostPackId = 'Microsoft.NETCore.App.Host.win-x64'
$resolvedAppHostPacks = @($downloadDependencies | Where-Object { $_.name -eq $appHostPackId })
if ($resolvedAppHostPacks.Count -gt 0) {
    if ($resolvedAppHostPacks.Count -ne 1 -or [string] $resolvedAppHostPacks[0].version -ne '[10.0.11, 10.0.11]') {
        throw "The restored $appHostPackId download dependency is not pinned to 10.0.11 exactly."
    }
}
else {
    # Installed SDK packs are omitted from project.assets.json downloadDependencies.
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop
    $dotnetRoot = Split-Path -Parent $dotnetCommand.Source
    $installedAppHost = Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64\10.0.11\runtimes\win-x64\native\apphost.exe'
    if (-not [System.IO.File]::Exists($installedAppHost)) {
        throw "The restored $appHostPackId is neither an exact download dependency nor installed at version 10.0.11."
    }
}

$tableViewAssetPath = 'lib/net10.0-windows10.0.19041/WinUI.TableView.dll'
$tableViewTargets = @(
    foreach ($target in $projectAssets.targets.PSObject.Properties) {
        $tableView = @($target.Value.PSObject.Properties | Where-Object { $_.Name -ceq 'WinUI.TableView/1.4.1' })
        foreach ($entry in $tableView) {
            [pscustomobject]@{
                Target = $target.Name
                Compile = @($entry.Value.compile.PSObject.Properties.Name)
                Runtime = @($entry.Value.runtime.PSObject.Properties.Name)
                Serialized = ($entry.Value | ConvertTo-Json -Depth 20 -Compress)
            }
        }
    }
)
$expectedTableViewTargets = @(
    'net10.0-windows10.0.19041.0',
    'net10.0-windows10.0.19041.0/win-x64'
)
if ($tableViewTargets.Count -ne $expectedTableViewTargets.Count -or
    @($expectedTableViewTargets | Where-Object { $_ -notin $tableViewTargets.Target }).Count -gt 0) {
    throw 'WinUI.TableView 1.4.1 was not selected for exactly the expected Windows framework and win-x64 targets.'
}
foreach ($target in $tableViewTargets) {
    if ($target.Compile.Count -ne 1 -or
        $target.Compile[0] -cne $tableViewAssetPath -or
        $target.Runtime.Count -ne 1 -or
        $target.Runtime[0] -cne $tableViewAssetPath -or
        $target.Serialized -match '(?i)Uno') {
        throw "WinUI.TableView selected an unapproved or Uno compile/runtime asset for target $($target.Target)."
    }
}

$nugetConfigPath = Join-Path $script:RepositoryRoot 'NuGet.Config'
[xml] $nugetConfig = Get-Content -Raw -LiteralPath $nugetConfigPath
$configurationElements = @(
    $nugetConfig.configuration.ChildNodes |
        Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element } |
        ForEach-Object { $_.LocalName }
)
$expectedConfigurationElements = @('packageSources', 'auditSources', 'config')
$packageSources = @($nugetConfig.configuration.packageSources.add)
$auditSources = @($nugetConfig.configuration.auditSources.add)
$configEntries = @($nugetConfig.configuration.config.add)
if ($configurationElements.Count -ne $expectedConfigurationElements.Count -or
    @($expectedConfigurationElements | Where-Object { $_ -cnotin $configurationElements }).Count -gt 0 -or
    $null -eq $nugetConfig.SelectSingleNode('/configuration/packageSources/clear') -or
    $packageSources.Count -ne 1 -or
    [string] $packageSources[0].key -cne 'nuget.org' -or
    [string] $packageSources[0].value -cne 'https://api.nuget.org/v3/index.json' -or
    [string] $packageSources[0].protocolVersion -cne '3' -or
    $null -eq $nugetConfig.SelectSingleNode('/configuration/auditSources/clear') -or
    $auditSources.Count -ne 1 -or
    [string] $auditSources[0].key -cne 'nuget.org' -or
    [string] $auditSources[0].value -cne 'https://api.nuget.org/v3/index.json' -or
    [string] $auditSources[0].protocolVersion -cne '3' -or
    $configEntries.Count -ne 1 -or
    [string] $configEntries[0].key -cne 'globalPackagesFolder' -or
    [string] $configEntries[0].value -cne '.nuget/packages') {
    throw 'NuGet.Config must contain only the exact NuGet.org package/audit sources and local package cache setting.'
}

$exporterPath = Join-Path $payloadRoot 'third_party\SQLiteExporter\SQLiteExporter.exe'
$exporterReadmePath = Join-Path $payloadRoot 'third_party\SQLiteExporter\Readme.pdf'
if ((Get-Sha256 -Path $exporterPath) -ne $script:ExporterSha256) {
    throw 'Staged SQLiteExporter.exe does not match the approved SHA-256.'
}

if ((Get-Sha256 -Path $exporterReadmePath) -ne $script:ExporterReadmeSha256) {
    throw 'Staged SQLiteExporter Readme.pdf does not match the approved SHA-256.'
}

$denyRules = @(
    [pscustomobject]@{ Id = 'debug-symbol'; Pattern = '(?i)\.pdb$' },
    [pscustomobject]@{ Id = 'source-file'; Pattern = '(?i)\.(cs|xaml|csproj|sln|slnx)$' },
    [pscustomobject]@{ Id = 'development-configuration'; Pattern = '(?i)(^|/)appsettings(\.[^/]+)?\.json$' },
    [pscustomobject]@{ Id = 'package-or-archive'; Pattern = '(?i)\.(nupkg|snupkg|zip|7z)$' },
    [pscustomobject]@{ Id = 'database-or-log'; Pattern = '(?i)\.(cdb|sqlite|sqlite3|db|log)$' },
    [pscustomobject]@{ Id = 'agent-metadata'; Pattern = '(?i)(^|/)\.agents(/|$)' },
    [pscustomobject]@{ Id = 'build-tree'; Pattern = '(?i)(^|/)(bin|obj|artifacts|tests|eng|installer)(/|$)' },
    [pscustomobject]@{ Id = 'legacy-exporter-ui'; Pattern = '(?i)(^|/)SqliteExporterUI\.exe$' },
    [pscustomobject]@{ Id = 'legacy-assembly-name'; Pattern = '(?i)(^|/)PCM\.CdbEditor\.' },
    [pscustomobject]@{ Id = 'wrong-native-architecture'; Pattern = '(?i)(^|/)(win-x86|win-arm|win-arm64|x86|arm|arm64)(/|$)' }
)

$contentDenyMatches = [System.Collections.Generic.List[object]]::new()
foreach ($file in $payloadFiles) {
    foreach ($rule in $denyRules) {
        if ($file.Path -match $rule.Pattern) {
            $contentDenyMatches.Add([pscustomobject]@{ Rule = $rule.Id; Path = $file.Path })
        }
    }
}

$contentAllowPath = Join-Path $metadataRoot 'content-allow.json'
$contentDenyPath = Join-Path $metadataRoot 'content-deny.json'
Write-JsonFile -Path $contentAllowPath -Value ([ordered]@{
    SchemaVersion = 1
    Product = 'PCM CDB Editor'
    Version = $Version
    RuntimeIdentifier = 'win-x64'
    Files = $payloadFiles
})
Write-JsonFile -Path $contentDenyPath -Value ([ordered]@{
    SchemaVersion = 1
    Rules = $denyRules
    Matches = @($contentDenyMatches)
})

if ($contentDenyMatches.Count -gt 0) {
    throw "Forbidden release content was found. See $contentDenyPath."
}

$dependencies = @(Get-LockedDependencies)
$expectedPackages = [ordered]@{
    'Microsoft.WindowsAppSDK' = '2.3.1'
    'Microsoft.Windows.SDK.BuildTools' = '10.0.28000.2526'
    'WinUI.TableView' = '1.4.1'
    'Microsoft.Data.Sqlite' = '10.0.11'
    'Microsoft.Data.Sqlite.Core' = '10.0.11'
    'SQLitePCLRaw.bundle_e_sqlite3' = '2.1.12'
    'SQLitePCLRaw.core' = '2.1.12'
    'SQLitePCLRaw.provider.e_sqlite3' = '2.1.12'
    'SQLitePCLRaw.lib.e_sqlite3' = '2.1.12'
    'Microsoft.NET.Test.Sdk' = '18.8.1'
    'MSTest.TestFramework' = '4.3.3'
    'MSTest.TestAdapter' = '4.3.3'
}
$expectedRiskyAssetPackages = [ordered]@{
    'Microsoft.CodeCoverage' = '18.8.1'
    'Microsoft.NET.Test.Sdk' = '18.8.1'
    'Microsoft.Testing.Extensions.Telemetry' = '2.3.3'
    'Microsoft.Testing.Platform' = '2.3.3'
    'Microsoft.Testing.Platform.MSBuild' = '2.3.3'
    'Microsoft.TestPlatform.TestHost' = '18.8.1'
    'Microsoft.Web.WebView2' = '1.0.3719.77'
    'Microsoft.Windows.AI.MachineLearning' = '2.1.74'
    'Microsoft.Windows.SDK.BuildTools' = '10.0.28000.2526'
    'Microsoft.Windows.SDK.BuildTools.MSIX' = '1.7.251221100'
    'Microsoft.WindowsAppSDK' = '2.3.1'
    'Microsoft.WindowsAppSDK.AI' = '2.3.4'
    'Microsoft.WindowsAppSDK.Base' = '2.0.4'
    'Microsoft.WindowsAppSDK.DWrite' = '2.1.0'
    'Microsoft.WindowsAppSDK.Foundation' = '2.3.5'
    'Microsoft.WindowsAppSDK.InteractiveExperiences' = '2.1.3'
    'Microsoft.WindowsAppSDK.ML' = '2.1.74'
    'Microsoft.WindowsAppSDK.Runtime' = '2.3.1'
    'Microsoft.WindowsAppSDK.Widgets' = '2.0.5'
    'Microsoft.WindowsAppSDK.WinUI' = '2.3.0'
    'MSTest.Analyzers' = '4.3.3'
    'MSTest.TestAdapter' = '4.3.3'
    'MSTest.TestFramework' = '4.3.3'
    'SQLitePCLRaw.lib.e_sqlite3' = '2.1.12'
    'System.Numerics.Tensors' = '9.0.0'
}
$resolvedRiskyAssets = @(Get-ResolvedRiskyAssets)

$dependencyDenyMatches = [System.Collections.Generic.List[object]]::new()
foreach ($expected in $expectedPackages.GetEnumerator()) {
    $matches = @($dependencies | Where-Object { $_.Id -ieq $expected.Key })
    if ($matches.Count -eq 0) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'required-package-missing'
            Id = $expected.Key
            ExpectedVersion = $expected.Value
            ActualVersion = $null
        })
        continue
    }

    foreach ($match in $matches) {
        if ($match.Version -ne $expected.Value) {
            $dependencyDenyMatches.Add([pscustomobject]@{
                Rule = 'approved-version-mismatch'
                Id = $match.Id
                ExpectedVersion = $expected.Value
                ActualVersion = $match.Version
            })
        }
    }
}

foreach ($expectedAssetPackage in $expectedRiskyAssetPackages.GetEnumerator()) {
    $assetMatches = @(
        $resolvedRiskyAssets |
            Where-Object { $_.Id -ieq $expectedAssetPackage.Key }
    )
    if ($assetMatches.Count -eq 0) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'required-risky-asset-package-missing'
            Id = $expectedAssetPackage.Key
            ExpectedVersion = $expectedAssetPackage.Value
            ActualVersion = $null
        })
    }
}

foreach ($assetPackage in $resolvedRiskyAssets) {
    if (-not $expectedRiskyAssetPackages.Contains($assetPackage.Id)) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'unexpected-resolved-risky-asset-package'
            Id = $assetPackage.Id
            ExpectedVersion = $null
            ActualVersion = $assetPackage.Version
            Project = $assetPackage.Project
            Target = $assetPackage.Target
            AssetKinds = $assetPackage.AssetKinds
        })
        continue
    }

    if ($assetPackage.Version -ne $expectedRiskyAssetPackages[$assetPackage.Id]) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'resolved-risky-asset-version-mismatch'
            Id = $assetPackage.Id
            ExpectedVersion = $expectedRiskyAssetPackages[$assetPackage.Id]
            ActualVersion = $assetPackage.Version
            Project = $assetPackage.Project
            Target = $assetPackage.Target
            AssetKinds = $assetPackage.AssetKinds
        })
    }
}

$runtimePackEntries = @(
    $dependencies |
        Where-Object {
            $_.Id -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.NETCore.App.Host.win-x64')
        }
)
foreach ($runtimePackId in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.NETCore.App.Host.win-x64')) {
    $packMatches = @($runtimePackEntries | Where-Object { $_.Id -eq $runtimePackId })
    if ($packMatches.Count -gt 0 -and @($packMatches | Where-Object { $_.Version -ne '10.0.11' }).Count -gt 0) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'runtime-pack-version-mismatch'
            Id = $runtimePackId
            ExpectedVersion = '10.0.11'
            ActualVersion = (@($packMatches.Version | Sort-Object -Unique) -join ', ')
        })
    }
}

foreach ($dependency in $dependencies) {
    if ([string]::IsNullOrWhiteSpace($dependency.ContentHash)) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'missing-lock-content-hash'
            Id = $dependency.Id
            ExpectedVersion = $dependency.Version
            ActualVersion = $dependency.Version
        })
    }

    if ($dependency.Id -like 'Uno.*') {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'uno-asset-forbidden'
            Id = $dependency.Id
            ExpectedVersion = $null
            ActualVersion = $dependency.Version
        })
    }

    if ($dependency.Type -in @('Direct', 'CentralTransitive') -and -not $expectedPackages.Contains($dependency.Id)) {
        $dependencyDenyMatches.Add([pscustomobject]@{
            Rule = 'unapproved-direct-package'
            Id = $dependency.Id
            ExpectedVersion = $null
            ActualVersion = $dependency.Version
        })
    }

    if ($dependency.Id -like 'SQLitePCLRaw.*') {
        try {
            if ([version] $dependency.Version -le [version] '2.1.11') {
                $dependencyDenyMatches.Add([pscustomobject]@{
                    Rule = 'vulnerable-sqlitepclraw-version'
                    Id = $dependency.Id
                    ExpectedVersion = '>=2.1.12'
                    ActualVersion = $dependency.Version
                })
            }
        }
        catch {
            $dependencyDenyMatches.Add([pscustomobject]@{
                Rule = 'unparseable-sqlitepclraw-version'
                Id = $dependency.Id
                ExpectedVersion = '>=2.1.12'
                ActualVersion = $dependency.Version
            })
        }
    }
}

$dependencyAllowPath = Join-Path $metadataRoot 'dependency-allow.json'
$dependencyDenyPath = Join-Path $metadataRoot 'dependency-deny.json'
Write-JsonFile -Path $dependencyAllowPath -Value ([ordered]@{
    SchemaVersion = 1
    RestorePolicy = 'Committed NuGet lock files; locked mode; NuGet.org only'
    Dependencies = $dependencies
    ResolvedRiskyAssets = $resolvedRiskyAssets
})
Write-JsonFile -Path $dependencyDenyPath -Value ([ordered]@{
    SchemaVersion = 1
    Rules = @(
        'All approved packages must resolve at the expected exact version.',
        'Every package entry must carry a NuGet lock content hash.',
        'No unapproved direct or centrally promoted package is allowed.',
        'Only exact approved packages may contribute native, build, buildMultiTargeting, buildTransitive, or runtimeTargets assets.',
        'Uno assets are forbidden for the Windows-specific TableView target.',
        'SQLitePCLRaw versions 2.1.11 and earlier are forbidden.'
    )
    Matches = @($dependencyDenyMatches)
})

if ($dependencyDenyMatches.Count -gt 0) {
    throw "The locked dependency graph violates release policy. See $dependencyDenyPath."
}

Assert-ZipMatchesPayload -PayloadFiles $payloadFiles -ArchivePath $zipFullPath

$installerSignature = Get-AuthenticodeSignature -LiteralPath $installerFullPath
if ($installerSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "This 0.1.0 delivery must remain unsigned; installer signature status is $($installerSignature.Status)."
}
$installerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installerFullPath)
if ($installerVersion.FileMajorPart -ne 0 -or
    $installerVersion.FileMinorPart -ne 1 -or
    $installerVersion.FileBuildPart -ne 0 -or
    $installerVersion.FilePrivatePart -ne 0 -or
    $installerVersion.ProductMajorPart -ne 0 -or
    $installerVersion.ProductMinorPart -ne 1 -or
    $installerVersion.ProductBuildPart -ne 0 -or
    $installerVersion.ProductPrivatePart -ne 0 -or
    ([string] $installerVersion.ProductName).Trim() -cne 'PCM CDB Editor' -or
    ([string] $installerVersion.CompanyName).Trim() -cne 'Peter537') {
    throw 'The installer does not carry the locked 0.1.0 product, file, and publisher metadata.'
}

$applicationPath = Join-Path $payloadRoot 'PcmCdbEditor.exe'
$applicationSignature = Get-AuthenticodeSignature -LiteralPath $applicationPath
if ($applicationSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "This 0.1.0 delivery must remain unsigned; application signature status is $($applicationSignature.Status)."
}
$applicationVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath)
if ([string] $applicationVersion.FileVersion -cne '0.1.0.0' -or
    -not ([string] $applicationVersion.ProductVersion).StartsWith(
        '0.1.0-unreleased',
        [System.StringComparison]::Ordinal) -or
    [string] $applicationVersion.ProductName -cne 'PCM CDB Editor' -or
    [string] $applicationVersion.CompanyName -cne 'Peter537') {
    throw 'The published application does not carry the locked 0.1.0 unreleased product and publisher metadata.'
}

$installerScriptPath = Join-Path $script:RepositoryRoot 'installer\PcmCdbEditor.iss'
$installerScriptText = Get-Content -Raw -LiteralPath $installerScriptPath
foreach ($installerIdentity in @(
    'AppId={{81DD1E0D-52AA-4E5F-BC2C-B884A3293B0F}',
    '#define AppProgId "Peter537.PcmCdbEditor.cdb"',
    '#define AppPublisher "Peter537"',
    'PrivilegesRequired=lowest',
    'VersionInfoVersion={#AppVersion}',
    'Root: HKCU; Subkey: "Software\Classes\.cdb\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue'
)) {
    if ($installerScriptText.IndexOf($installerIdentity, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Installer identity or per-user policy is missing: $installerIdentity"
    }
}

if ($installerScriptText -match '(?im)^\s*\[UninstallDelete\]\s*$') {
    throw 'Installer must not delete LocalAppData settings, backups, or recoverable sessions on uninstall.'
}

$installerSourceLines = @(
    Select-String -LiteralPath $installerScriptPath -Pattern '^\s*Source\s*:'
)
if ($installerSourceLines.Count -ne 1 -or $installerSourceLines[0].Line -notmatch 'Source:\s*"\{#SourceDir\}\\\*"') {
    throw 'Installer must have exactly one [Files] source and it must be the verified payload directory.'
}

$sbomText = Get-Content -Raw -LiteralPath $sbomFullPath
if ($sbomText -match '(?i)[A-Z]:\\Users\\') {
    throw 'The generated SBOM contains a personal absolute Windows path.'
}
$sbom = $sbomText | ConvertFrom-Json
if ($null -eq $sbom -or [string]::IsNullOrWhiteSpace([string] $sbom.spdxVersion)) {
    throw 'The generated SBOM is not a readable SPDX document.'
}
$sbomPackageNames = @($sbom.packages | ForEach-Object { [string] $_.name })
foreach ($requiredSbomPackage in @(
    'PCM CDB Editor',
    'Microsoft.WindowsAppSDK',
    'WinUI.TableView',
    'Microsoft.Data.Sqlite',
    'Microsoft.Data.Sqlite.Core',
    'SQLitePCLRaw.bundle_e_sqlite3',
    'SQLitePCLRaw.core',
    'SQLitePCLRaw.lib.e_sqlite3',
    'SQLitePCLRaw.provider.e_sqlite3',
    'Microsoft.NET.Test.Sdk',
    'MSTest.TestFramework',
    'MSTest.TestAdapter'
)) {
    if ($requiredSbomPackage -cnotin $sbomPackageNames) {
        throw "The generated SBOM is missing required package component: $requiredSbomPackage"
    }
}

$sourceManifestPath = Resolve-RequiredPath `
    -Path (Join-Path $metadataRoot 'source-manifest.json') `
    -Kind File
$provenancePath = Resolve-RequiredPath `
    -Path (Join-Path $metadataRoot 'release-provenance.json') `
    -Kind File
$sbomValidationPath = Resolve-RequiredPath `
    -Path (Join-Path $metadataRoot 'sbom-validation.json') `
    -Kind File
$vulnerabilityAuditPath = Resolve-RequiredPath `
    -Path (Join-Path $metadataRoot 'dependency-vulnerabilities.json') `
    -Kind File
$vulnerabilityAudit = Get-Content -Raw -LiteralPath $vulnerabilityAuditPath | ConvertFrom-Json
$expectedAuditedProjects = @(
    'src/PcmCdbEditor.App/PcmCdbEditor.App.csproj',
    'src/PcmCdbEditor.Application/PcmCdbEditor.Application.csproj',
    'src/PcmCdbEditor.Domain/PcmCdbEditor.Domain.csproj',
    'src/PcmCdbEditor.Infrastructure/PcmCdbEditor.Infrastructure.csproj',
    'tests/PcmCdbEditor.IntegrationTests/PcmCdbEditor.IntegrationTests.csproj',
    'tests/PcmCdbEditor.UnitTests/PcmCdbEditor.UnitTests.csproj'
)
$actualAuditedProjects = @($vulnerabilityAudit.Projects)
$vulnerabilityAuditSources = @($vulnerabilityAudit.AuditSources)
$vulnerabilityFindings = @($vulnerabilityAudit.Findings)
if ([int] $vulnerabilityAudit.SchemaVersion -ne 1 -or
    [string] $vulnerabilityAudit.Scanner -cne 'dotnet package list' -or
    [int] $vulnerabilityAudit.OutputVersion -ne 1 -or
    [string] $vulnerabilityAudit.SdkVersion -cne '10.0.303' -or
    [string] $vulnerabilityAudit.Arguments -cne '--include-transitive --vulnerable --no-restore --format json --output-version 1' -or
    $vulnerabilityAuditSources.Count -ne 1 -or
    [string] $vulnerabilityAuditSources[0] -cne 'https://api.nuget.org/v3/index.json' -or
    [int] $vulnerabilityAudit.ProjectCount -ne $expectedAuditedProjects.Count -or
    $actualAuditedProjects.Count -ne $expectedAuditedProjects.Count -or
    @($expectedAuditedProjects | Where-Object { $_ -cnotin $actualAuditedProjects }).Count -gt 0 -or
    [int] $vulnerabilityAudit.VulnerabilityCount -ne 0 -or
    $vulnerabilityFindings.Count -ne 0) {
    throw 'The auditable full-graph NuGet vulnerability report is incomplete, malformed, or contains a known vulnerability.'
}

$sourceManifest = Get-Content -Raw -LiteralPath $sourceManifestPath | ConvertFrom-Json
$sourceEntries = @($sourceManifest.Files)
if ([int] $sourceManifest.SchemaVersion -ne 1 -or
    $sourceEntries.Count -eq 0 -or
    [string] $sourceManifest.TreeSha256 -notmatch '^[0-9A-F]{64}$') {
    throw 'The source manifest is missing required schema, file, or tree-digest evidence.'
}

$sourceRecordsByPath = New-Object 'System.Collections.Generic.SortedDictionary[string,object]' (
    [System.StringComparer]::Ordinal)
$repositoryPrefix = $script:RepositoryRoot.TrimEnd('\', '/') + '\'
foreach ($entry in $sourceEntries) {
    $relativePath = [string] $entry.Path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath.StartsWith('/') -or
        $relativePath.StartsWith('\') -or
        $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "The source manifest contains an unsafe path: $relativePath"
    }
    if ($sourceRecordsByPath.ContainsKey($relativePath)) {
        throw "The source manifest contains a duplicate path: $relativePath"
    }

    $sourcePath = [System.IO.Path]::GetFullPath(
        (Join-Path $script:RepositoryRoot $relativePath.Replace('/', '\')))
    if (-not $sourcePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.File]::Exists($sourcePath)) {
        throw "The source manifest path is missing or escaped the repository: $relativePath"
    }
    if ((New-Object System.IO.FileInfo($sourcePath)).Length -ne [long] $entry.Size -or
        (Get-Sha256 -Path $sourcePath) -ne [string] $entry.Sha256) {
        throw "The release source changed after its manifest was recorded: $relativePath"
    }

    $sourceRecordsByPath.Add($relativePath, $entry)
}

$sourceCanonicalLines = @(
    $sourceRecordsByPath.Values |
        ForEach-Object { "$($_.Path)`0$($_.Size)`0$($_.Sha256)" }
)
$calculatedSourceTree = Get-StringSha256 -Value ([string]::Join("`n", $sourceCanonicalLines))
if ($calculatedSourceTree -ne [string] $sourceManifest.TreeSha256) {
    throw 'The source manifest tree digest does not match its canonical file entries.'
}

$provenance = Get-Content -Raw -LiteralPath $provenancePath | ConvertFrom-Json
if ([int] $provenance.SchemaVersion -ne 3 -or
    [string] $provenance.Source.Manifest -ne 'metadata/source-manifest.json' -or
    [string] $provenance.Source.TreeSha256 -ne $calculatedSourceTree -or
    [int] $provenance.Source.ManifestFileCount -ne $sourceEntries.Count -or
    [string] $provenance.Source.Commit -ne [string] $provenance.SourceCommit) {
    throw 'Release provenance does not match the deterministic source manifest.'
}
$buildEnvironmentProperty = $provenance.PSObject.Properties['BuildEnvironment']
if ($null -eq $buildEnvironmentProperty -or $null -eq $buildEnvironmentProperty.Value) {
    throw 'Release provenance does not record the local build environment.'
}
$buildEnvironment = $buildEnvironmentProperty.Value
foreach ($fieldName in @('OSDescription', 'OSArchitecture', 'ProcessArchitecture', 'PowerShellVersion')) {
    $field = $buildEnvironment.PSObject.Properties[$fieldName]
    if ($null -eq $field -or [string]::IsNullOrWhiteSpace([string] $field.Value)) {
        throw "Release provenance does not record BuildEnvironment.$fieldName."
    }
}
if ([string] $provenance.InnoSetup.AcquisitionMode -cne 'Portable' -or
    [string] $provenance.InnoSetup.PrivilegeMode -cne 'CurrentUser') {
    throw 'Release provenance must record portable, current-user Inno Setup acquisition.'
}
$buildReleaseSource = Get-Content -Raw -LiteralPath (
    Join-Path $script:RepositoryRoot 'eng\Build-Release.ps1')
if ($buildReleaseSource -notmatch "(?m)^\s*'/CURRENTUSER',\s*$" -or
    $buildReleaseSource -notmatch "(?m)^\s*'/PORTABLE=1',\s*$") {
    throw 'Build-Release must invoke the verified Inno compiler in portable current-user mode.'
}

$currentGitStatus = @(& git -C $script:RepositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
if ($LASTEXITCODE -ne 0 -or
    [bool] $provenance.Source.WorkingTreeDirty -ne ($currentGitStatus.Count -gt 0) -or
    [int] $provenance.Source.GitStatusEntryCount -ne $currentGitStatus.Count) {
    throw 'Release provenance does not match the current working-tree state.'
}

$releaseRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $metadataRoot))
$checksumInputs = [System.Collections.Generic.List[string]]::new()
$checksumInputs.Add($zipFullPath)
$checksumInputs.Add($installerFullPath)
$checksumInputs.Add($sbomFullPath)
$checksumInputs.Add($contentAllowPath)
$checksumInputs.Add($contentDenyPath)
$checksumInputs.Add($dependencyAllowPath)
$checksumInputs.Add($dependencyDenyPath)
$checksumInputs.Add($sourceManifestPath)
$checksumInputs.Add($provenancePath)
$checksumInputs.Add($sbomValidationPath)
$checksumInputs.Add($vulnerabilityAuditPath)

$checksumLines = @(
    $checksumInputs |
        Sort-Object |
        ForEach-Object {
            $relativePath = (Get-RelativePath -BasePath $releaseRoot -TargetPath $_).Replace('\', '/')
            "$(Get-Sha256 -Path $_) *$relativePath"
        }
)
$checksumLines | Set-Content -LiteralPath $checksumsFullPath -Encoding ascii

Write-Host "Verified release payload: $($payloadFiles.Count) files"
Write-Host "Verified locked dependency entries: $($dependencies.Count)"
Write-Host 'Verified the auditable full-graph NuGet vulnerability report contains no findings.'
Write-Host "Verified ZIP is byte-for-byte equivalent to the staged payload."
Write-Host 'Verified installer is unsigned and compiled from the single staged payload source.'
Write-Host "Wrote checksums: $checksumsFullPath"
