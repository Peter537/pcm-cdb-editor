[CmdletBinding()]
param(
    [ValidateSet('0.1.0')]
    [string] $Version = '0.1.0',

    [string] $OutputRoot,

    [switch] $RequireCleanSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$sourceCheckout = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$requiredSdkVersion = '10.0.403'
$runtimeFrameworkVersion = '10.0.15'
. (Join-Path $PSScriptRoot 'Release-RuntimePolicy.ps1')
. (Join-Path $PSScriptRoot 'Release-Filesystem.ps1')
. (Join-Path $PSScriptRoot 'Release-Source.ps1')
$runtimeIdentifier = 'win-x64'
$innoVersion = '7.1.0'
$innoReleaseTag = 'is-7_1_0'
$innoInstallerName = 'innosetup-7.1.0-x64.exe'
$innoInstallerUrl = "https://github.com/jrsoftware/issrc/releases/download/$innoReleaseTag/$innoInstallerName"
$innoInstallerSha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$exporterSha256 = '5F1F3F72C5BF5537E74DE1A0FC732B1CBDF191E6CA3A3E9A12F18BBE15C1951D'
$exporterReadmeSha256 = 'CF05919B2BDB7873F4CAFAA98126F2143195BFF1D0A937EF55B00E164EF7DED6'
$sbomToolManifestSha256 = '2935CC70C9DB2DD0C8D97B3978DA236929BB3CD1531BE5B59525B54EE0A72CB8'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $Description
    )

    Write-Host "==> $Description"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-SbomToolManifest {
    $manifestPath = Join-Path $repositoryRoot '.config\dotnet-tools.json'
    $hashPath = Join-Path $repositoryRoot '.config\dotnet-tools.sha256'
    if (-not [System.IO.File]::Exists($manifestPath) -or -not [System.IO.File]::Exists($hashPath)) {
        throw 'The pinned repository-local SBOM tool manifest or integrity file is missing.'
    }

    $recordedHash = (Get-Content -Raw -LiteralPath $hashPath).Trim().ToUpperInvariant()
    if ($recordedHash -ne $sbomToolManifestSha256 -or (Get-Sha256 -Path $manifestPath) -ne $sbomToolManifestSha256) {
        throw 'The repository-local SBOM tool manifest does not match the hard-coded approved SHA-256.'
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $tools = @($manifest.tools.PSObject.Properties)
    $commands = @(if ($tools.Count -eq 1) { @($tools[0].Value.commands) })
    if ([int] $manifest.version -ne 1 -or
        [bool] $manifest.isRoot -ne $true -or
        $tools.Count -ne 1 -or
        $tools[0].Name -cne 'microsoft.sbom.dotnettool' -or
        [string] $tools[0].Value.version -cne '4.1.5' -or
        $commands.Count -ne 1 -or
        [string] $commands[0] -cne 'sbom-tool') {
        throw 'The repository-local tool manifest must contain only Microsoft.Sbom.DotNetTool 4.1.5 with command sbom-tool.'
    }
}

function Assert-NuGetConfiguration {
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

function Get-JsonVulnerabilityEntries {
    param([AllowNull()][object] $InputObject)

    if ($null -eq $InputObject -or $InputObject -is [string]) {
        return
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and
        $InputObject -isnot [System.Management.Automation.PSCustomObject]) {
        foreach ($item in $InputObject) {
            Get-JsonVulnerabilityEntries -InputObject $item
        }
        return
    }

    foreach ($property in $InputObject.PSObject.Properties) {
        if ($property.Name -ceq 'vulnerabilities') {
            foreach ($vulnerability in @($property.Value)) {
                Write-Output $vulnerability
            }
        }
        Get-JsonVulnerabilityEntries -InputObject $property.Value
    }
}

function Invoke-NuGetVulnerabilityAudit {
    param([Parameter(Mandatory = $true)][string] $OutputPath)

    $auditArguments = @(
        'package', 'list',
        '--project', $solutionPath,
        '--include-transitive',
        '--vulnerable',
        '--no-restore',
        '--format', 'json',
        '--output-version', '1'
    )
    Write-Host '==> Audit the complete locked NuGet graph for known vulnerabilities'
    $rawOutput = @(& dotnet @auditArguments)
    $auditExitCode = $LASTEXITCODE
    if ($auditExitCode -ne 0) {
        throw "Full-graph NuGet vulnerability audit failed with exit code $auditExitCode."
    }

    $rawJson = [string]::Join("`n", [string[]] $rawOutput)
    try {
        $rawReport = $rawJson | ConvertFrom-Json
    }
    catch {
        throw "Full-graph NuGet vulnerability audit did not return JSON output version 1: $($_.Exception.Message)"
    }

    $expectedSource = 'https://api.nuget.org/v3/index.json'
    $sources = @($rawReport.sources)
    $projects = @($rawReport.projects)
    if ([int] $rawReport.version -ne 1 -or
        $sources.Count -ne 1 -or
        [string] $sources[0] -cne $expectedSource -or
        $projects.Count -ne 6) {
        throw 'Full-graph NuGet vulnerability audit did not cover the exact six-project, NuGet.org-only graph.'
    }

    $expectedProjects = @(
        'src\PcmCdbEditor.App\PcmCdbEditor.App.csproj',
        'src\PcmCdbEditor.Application\PcmCdbEditor.Application.csproj',
        'src\PcmCdbEditor.Domain\PcmCdbEditor.Domain.csproj',
        'src\PcmCdbEditor.Infrastructure\PcmCdbEditor.Infrastructure.csproj',
        'tests\PcmCdbEditor.IntegrationTests\PcmCdbEditor.IntegrationTests.csproj',
        'tests\PcmCdbEditor.UnitTests\PcmCdbEditor.UnitTests.csproj'
    )
    $expectedByFullPath = New-Object 'System.Collections.Generic.Dictionary[string,string]' (
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in $expectedProjects) {
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
        $expectedByFullPath.Add($fullPath, $relativePath.Replace('\', '/'))
    }

    $auditedProjects = New-Object 'System.Collections.Generic.HashSet[string]' (
        [System.StringComparer]::Ordinal)
    $findings = [System.Collections.Generic.List[object]]::new()
    foreach ($project in $projects) {
        $projectPath = [System.IO.Path]::GetFullPath([string] $project.path)
        if (-not $expectedByFullPath.ContainsKey($projectPath)) {
            throw 'Full-graph NuGet vulnerability audit returned an unexpected project path.'
        }

        $relativeProject = $expectedByFullPath[$projectPath]
        if (-not $auditedProjects.Add($relativeProject)) {
            throw "Full-graph NuGet vulnerability audit returned a duplicate project: $relativeProject"
        }

        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty) {
            continue
        }

        foreach ($framework in @($frameworksProperty.Value)) {
            foreach ($packageGroupName in @('topLevelPackages', 'transitivePackages')) {
                $packageGroup = $framework.PSObject.Properties[$packageGroupName]
                if ($null -eq $packageGroup) {
                    continue
                }

                foreach ($package in @($packageGroup.Value)) {
                    $vulnerabilities = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $vulnerabilities) {
                        continue
                    }

                    foreach ($vulnerability in @($vulnerabilities.Value)) {
                        $requestedVersion = $package.PSObject.Properties['requestedVersion']
                        $findings.Add([pscustomobject]@{
                                Project = $relativeProject
                                Framework = [string] $framework.framework
                                DependencyKind = if ($packageGroupName -eq 'topLevelPackages') { 'Direct' } else { 'Transitive' }
                                Id = [string] $package.id
                                RequestedVersion = if ($null -eq $requestedVersion) { $null } else { [string] $requestedVersion.Value }
                                ResolvedVersion = [string] $package.resolvedVersion
                                Severity = [string] $vulnerability.severity
                                AdvisoryUrl = [string] $vulnerability.advisoryurl
                            })
                    }
                }
            }
        }
    }

    if ($auditedProjects.Count -ne $expectedProjects.Count) {
        throw 'Full-graph NuGet vulnerability audit omitted one or more projects.'
    }

    $allReportedVulnerabilities = @(Get-JsonVulnerabilityEntries -InputObject $projects)
    if ($allReportedVulnerabilities.Count -ne $findings.Count) {
        throw 'Full-graph NuGet vulnerability audit returned an unrecognized vulnerable-package shape.'
    }

    $sanitizedReport = [ordered]@{
        SchemaVersion = 1
        Scanner = 'dotnet package list'
        OutputVersion = 1
        SdkVersion = $requiredSdkVersion
        GeneratedUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
        Arguments = '--include-transitive --vulnerable --no-restore --format json --output-version 1'
        AuditSources = $sources
        Projects = @($auditedProjects | Sort-Object)
        ProjectCount = $auditedProjects.Count
        Findings = @($findings)
        VulnerabilityCount = $findings.Count
    }
    Write-Utf8NoBom -Path $OutputPath -Content ($sanitizedReport | ConvertTo-Json -Depth 10)

    if ($findings.Count -gt 0) {
        throw "The locked dependency graph contains $($findings.Count) known vulnerability finding(s). See $OutputPath."
    }
}

function Assert-AuthenticodePublisher {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $PublisherPattern,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Description has invalid Authenticode status: $($signature.Status)."
    }

    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch $PublisherPattern) {
        throw "$Description was not signed by the approved publisher Pyrsys B.V."
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Copy-ComponentEvidence {
    param([Parameter(Mandatory = $true)][string] $Destination)

    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $rootEvidenceFiles = @(
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'global.json',
        'NuGet.Config',
        'PcmCdbEditor.slnx',
        '.config\dotnet-tools.json'
    )

    foreach ($relativePath in $rootEvidenceFiles) {
        $source = Join-Path $repositoryRoot $relativePath
        if (-not [System.IO.File]::Exists($source)) {
            throw "Required component evidence is missing: $relativePath"
        }

        $destinationPath = Join-Path $Destination $relativePath
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destinationPath -Force
    }

    Assert-SbomToolManifest
    $toolManifestPath = Join-Path $repositoryRoot '.config\dotnet-tools.json'
    $toolManifestHashPath = Join-Path $repositoryRoot '.config\dotnet-tools.sha256'
    Copy-Item -LiteralPath $toolManifestHashPath -Destination (Join-Path $Destination '.config\dotnet-tools.sha256') -Force

    foreach ($safeRootName in @('src', 'tests')) {
        $safeRoot = Join-Path $repositoryRoot $safeRootName
        $evidenceFiles = @(
            Get-ChildItem -LiteralPath $safeRoot -Recurse -Force -File |
                Where-Object { $_.Name -eq 'packages.lock.json' -or $_.Extension -eq '.csproj' }
        )

        foreach ($evidenceFile in $evidenceFiles) {
            $relativePath = Get-RelativePath -BasePath $repositoryRoot -TargetPath $evidenceFile.FullName
            $destinationPath = Join-Path $Destination $relativePath
            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
            Copy-Item -LiteralPath $evidenceFile.FullName -Destination $destinationPath -Force

            if ($evidenceFile.Extension -eq '.csproj') {
                $assetsPath = Join-Path $evidenceFile.DirectoryName 'obj\project.assets.json'
                if (-not [System.IO.File]::Exists($assetsPath)) {
                    throw "Restored component evidence is missing for $relativePath."
                }

                $assetsRelativePath = Get-RelativePath -BasePath $repositoryRoot -TargetPath $assetsPath
                $assetsDestinationPath = Join-Path $Destination $assetsRelativePath
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $assetsDestinationPath)) | Out-Null
                Copy-Item -LiteralPath $assetsPath -Destination $assetsDestinationPath -Force
            }
        }
    }
}

$initialGit = Get-ReleaseGitState -Root $sourceCheckout
$sourceCommit = $initialGit.Commit
$gitStatus = $initialGit.Status
if ($RequireCleanSource -and $gitStatus.Count -ne 0) {
    throw 'A release-eligible build requires a clean source checkout.'
}
$sourceFiles = @(Get-SourceManifestFiles -Root $sourceCheckout)
$sourceTreeSha256 = Get-ReleaseSourceDigest -Files $sourceFiles

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $sourceCheckout 'artifacts\release'
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $sourceCheckout 'artifacts'))
Assert-DescendantPath -Child $outputRootFullPath -Parent $artifactsRoot -Purpose 'Release output root'
Assert-NoReparsePointInDirectoryChain -Path $outputRootFullPath -Root $artifactsRoot -Purpose 'Release output root'
[System.IO.Directory]::CreateDirectory($outputRootFullPath) | Out-Null

$publishedDirectory = Join-Path $outputRootFullPath $Version
Assert-DescendantPath -Child $publishedDirectory -Parent $outputRootFullPath -Purpose 'Versioned release directory'
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 16)
$runRoot = Join-Path $artifactsRoot ".r-$runId"
if (Test-Path -LiteralPath $runRoot) {
    throw 'The generated release scratch directory already exists.'
}
$previousNugetPackages = $env:NUGET_PACKAGES
$locationPushed = $false
try {
    [System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
    Write-Host "Release scratch directory: $runRoot"
    $repositoryRoot = Join-Path $runRoot 's'
    $releaseDirectory = Join-Path $runRoot 'c'
    $buildDirectory = Join-Path $runRoot 'b'
    $toolsDirectory = Join-Path $runRoot 't'
    $publishDirectory = Join-Path $buildDirectory 'publish'
    $componentEvidenceDirectory = Join-Path $buildDirectory 'component-evidence'

    $sourceMode = 'WorkingTreeSnapshot'
    $cleanCommit = ''
    if ($gitStatus.Count -eq 0) {
        $sourceMode = 'CommittedSnapshot'
        $cleanCommit = $sourceCommit
    }
    Copy-ReleaseSourceSnapshot -Root $sourceCheckout -Destination $repositoryRoot -Files $sourceFiles -CleanCommit $cleanCommit
    Assert-ReleaseSnapshot -Root $repositoryRoot -Files $sourceFiles -TreeSha256 $sourceTreeSha256 -BeforeBuild
    Assert-ReleaseSourceState -Root $sourceCheckout -Commit $sourceCommit -StatusSha256 $initialGit.StatusSha256 -TreeSha256 $sourceTreeSha256
    Push-Location -LiteralPath $repositoryRoot
    $locationPushed = $true
    $env:NUGET_PACKAGES = Join-Path $sourceCheckout '.nuget\packages'

    $solutionPath = Join-Path $repositoryRoot 'PcmCdbEditor.slnx'
    $appProjectPath = Join-Path $repositoryRoot 'src\PcmCdbEditor.App\PcmCdbEditor.App.csproj'
    $unitTestsPath = Join-Path $repositoryRoot 'tests\PcmCdbEditor.UnitTests\PcmCdbEditor.UnitTests.csproj'
    $integrationTestsPath = Join-Path $repositoryRoot 'tests\PcmCdbEditor.IntegrationTests\PcmCdbEditor.IntegrationTests.csproj'
    $nugetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
    $installerScriptPath = Join-Path $repositoryRoot 'installer\PcmCdbEditor.iss'
    $verifyScriptPath = Join-Path $repositoryRoot 'eng\Verify-Release.ps1'
    $payloadDirectory = Join-Path $releaseDirectory 'payload'
    $packagesDirectory = Join-Path $releaseDirectory 'packages'
    $metadataDirectory = Join-Path $releaseDirectory 'metadata'
    $sbomDirectory = Join-Path $metadataDirectory 'sbom'
    $checksumsPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    $vulnerabilityAuditPath = Join-Path $metadataDirectory 'dependency-vulnerabilities.json'

    foreach ($directory in @($buildDirectory, $toolsDirectory, $publishDirectory, $payloadDirectory, $packagesDirectory, $metadataDirectory, $sbomDirectory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne $requiredSdkVersion) {
        throw "Release builds require .NET SDK $requiredSdkVersion exactly; found '$sdkVersion'."
    }

    Assert-NuGetConfiguration
    Assert-SbomToolManifest
    $runtimeSecurity = Get-RuntimeSecurityEvidence -RuntimeVersion $runtimeFrameworkVersion

    Invoke-Checked -FilePath 'dotnet' -Description 'Restore repository-local tools' -Arguments @(
        'tool', 'restore', '--configfile', $nugetConfigPath
    )

    Invoke-Checked -FilePath 'dotnet' -Description 'Restore the locked solution graph' -Arguments @(
        'restore', $solutionPath,
        '--locked-mode',
        '--configfile', $nugetConfigPath,
        '--disable-parallel',
        '--disable-build-servers',
        '-p:RestoreUseStaticGraphEvaluation=false',
        '-p:BuildInParallel=false',
        '-m:1'
    )

    # A solution-level RID restore is unsupported, but self-contained publish needs the
    # app and all project references restored with the concrete RID so the pinned
    # runtime/apphost packs are downloaded before --no-restore publish.
    Invoke-Checked -FilePath 'dotnet' -Description 'Restore the locked app graph for win-x64 publish' -Arguments @(
        'restore', $appProjectPath,
        '--runtime', $runtimeIdentifier,
        '--locked-mode',
        '--configfile', $nugetConfigPath,
        '--disable-parallel',
        '--disable-build-servers',
        '-p:RestoreUseStaticGraphEvaluation=false',
        '-p:BuildInParallel=false',
        '-m:1'
    )

    Invoke-NuGetVulnerabilityAudit -OutputPath $vulnerabilityAuditPath

    Invoke-Checked -FilePath 'dotnet' -Description 'Build the complete solution' -Arguments @(
        'build', $solutionPath,
        '--configuration', 'Release',
        '--no-restore',
        '--disable-build-servers',
        '-m:1',
        '-p:ContinuousIntegrationBuild=true',
        '-p:Platform=x64'
    )

    foreach ($testProject in @($unitTestsPath, $integrationTestsPath)) {
        $testArguments = @('test', $testProject, '--configuration', 'Release', '--no-build', '--no-restore', '--disable-build-servers', '-m:1')
        if ($testProject -eq $integrationTestsPath) {
            $testArguments += @('--filter', 'TestCategory!=AuthorizedLocalSmoke')
        }
        Invoke-Checked -FilePath 'dotnet' -Description "Test $(Split-Path -Leaf $testProject)" -Arguments $testArguments
    }

    Invoke-Checked -FilePath 'dotnet' -Description 'Publish the self-contained win-x64 payload' -Arguments @(
        'publish', $appProjectPath,
        '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--output', $publishDirectory,
        '-p:WindowsAppSDKSelfContained=true',
        '-p:WindowsPackageType=None',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:PublishAot=false',
        '--disable-build-servers',
        '-m:1'
    )

    Copy-DirectoryContents -Source $publishDirectory -Destination $payloadDirectory

    $thirdPartySource = Join-Path $repositoryRoot 'third_party\SQLiteExporter'
    $exporterSource = Join-Path $thirdPartySource 'SQLiteExporter.exe'
    $exporterReadmeSource = Join-Path $thirdPartySource 'Readme.pdf'
    if (-not [System.IO.File]::Exists($exporterSource) -or -not [System.IO.File]::Exists($exporterReadmeSource)) {
        throw 'The approved SQLiteExporter.exe and Readme.pdf are required under third_party\SQLiteExporter.'
    }

    if ((Get-Sha256 -Path $exporterSource) -ne $exporterSha256) {
        throw 'Source SQLiteExporter.exe does not match the approved SHA-256.'
    }

    if ((Get-Sha256 -Path $exporterReadmeSource) -ne $exporterReadmeSha256) {
        throw 'Source SQLiteExporter Readme.pdf does not match the approved SHA-256.'
    }

    $thirdPartyPayload = Join-Path $payloadDirectory 'third_party\SQLiteExporter'
    [System.IO.Directory]::CreateDirectory($thirdPartyPayload) | Out-Null
    Copy-Item -LiteralPath $exporterSource -Destination (Join-Path $thirdPartyPayload 'SQLiteExporter.exe') -Force
    Copy-Item -LiteralPath $exporterReadmeSource -Destination (Join-Path $thirdPartyPayload 'Readme.pdf') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $payloadDirectory 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $payloadDirectory 'THIRD_PARTY_NOTICES.md') -Force

    Copy-ComponentEvidence -Destination $componentEvidenceDirectory

    Invoke-Checked -FilePath 'dotnet' -Description 'Generate the SPDX 2.2 SBOM' -Arguments @(
        'tool', 'run', 'sbom-tool', '--allow-roll-forward',
        '--',
        'generate',
        '-b', $payloadDirectory,
        '-bc', $componentEvidenceDirectory,
        '-pn', 'PCM CDB Editor',
        '-pv', $Version,
        '-ps', 'Peter537',
        '-nsb', 'https://github.com/Peter537/pcm-cdb-editor',
        '-m', $sbomDirectory
    )

    $sbomManifestPath = Join-Path $sbomDirectory '_manifest\spdx_2.2\manifest.spdx.json'
    if (-not [System.IO.File]::Exists($sbomManifestPath)) {
        throw "SBOM generation did not create the expected manifest: $sbomManifestPath"
    }

    $sbomText = Get-Content -Raw -LiteralPath $sbomManifestPath
    if ($sbomText -match '(?i)[A-Z]:\\Users\\') {
        throw 'The generated SBOM contains a personal absolute Windows path.'
    }
    $sbomDocument = $sbomText | ConvertFrom-Json
    $sbomPackageNames = @($sbomDocument.packages | ForEach-Object { [string] $_.name })
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

    Invoke-Checked -FilePath 'dotnet' -Description 'Validate the SPDX 2.2 SBOM' -Arguments @(
        'tool', 'run', 'sbom-tool', '--allow-roll-forward',
        '--',
        'validate',
        '-b', $payloadDirectory,
        '-o', (Join-Path $metadataDirectory 'sbom-validation.json'),
        '-mi', 'SPDX:2.2',
        '-m', (Join-Path $sbomDirectory '_manifest')
    )

    $zipPath = Join-Path $packagesDirectory "PcmCdbEditor-$Version-win-x64.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $innoInstallerPath = Join-Path $toolsDirectory $innoInstallerName
    Invoke-WebRequest -Uri $innoInstallerUrl -OutFile $innoInstallerPath -UseBasicParsing
    if ((Get-Sha256 -Path $innoInstallerPath) -ne $innoInstallerSha256) {
        throw 'Inno Setup installer does not match the approved SHA-256.'
    }

    Assert-AuthenticodePublisher -Path $innoInstallerPath -PublisherPattern 'CN=Pyrsys B\.V\.' -Description 'Inno Setup installer'

    $githubCli = Get-Command 'gh' -ErrorAction SilentlyContinue
    if ($null -eq $githubCli) {
        throw 'GitHub CLI is required to verify the Inno Setup release asset attestation.'
    }

    Invoke-Checked -FilePath $githubCli.Path -Description 'Verify the Inno Setup GitHub release attestation' -Arguments @(
        'release', 'verify-asset', $innoReleaseTag, $innoInstallerPath,
        '--repo', 'jrsoftware/issrc'
    )

    $innoInstallDirectory = Join-Path $toolsDirectory "InnoSetup-$innoVersion"
    $innoInstallArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/CURRENTUSER',
        '/PORTABLE=1',
        "/DIR=`"$innoInstallDirectory`""
    )
    $installProcess = Start-Process -FilePath $innoInstallerPath -ArgumentList $innoInstallArguments -Wait -PassThru -WindowStyle Hidden
    if ($installProcess.ExitCode -ne 0) {
        throw "Verified Inno Setup installation failed with exit code $($installProcess.ExitCode)."
    }

    $innoCompilerPath = Join-Path $innoInstallDirectory 'ISCC.exe'
    if (-not [System.IO.File]::Exists($innoCompilerPath)) {
        throw "Inno Setup compiler was not installed at the expected path: $innoCompilerPath"
    }

    Assert-AuthenticodePublisher -Path $innoCompilerPath -PublisherPattern 'CN=Pyrsys B\.V\.' -Description 'Inno Setup compiler'

    Invoke-Checked -FilePath $innoCompilerPath -Description 'Compile the per-user installer from the staged payload' -Arguments @(
        "/DAppVersion=$Version",
        "/DSourceDir=$payloadDirectory",
        "/DOutputDir=$packagesDirectory",
        $installerScriptPath
    )

    $installerPath = Join-Path $packagesDirectory "PcmCdbEditor-$Version-win-x64-setup.exe"
    if (-not [System.IO.File]::Exists($installerPath)) {
        throw "Inno Setup did not create the expected installer: $installerPath"
    }

    Assert-ReleaseSnapshot -Root $repositoryRoot -Files $sourceFiles -TreeSha256 $sourceTreeSha256
    Assert-ReleaseSourceState -Root $sourceCheckout -Commit $sourceCommit -StatusSha256 $initialGit.StatusSha256 -TreeSha256 $sourceTreeSha256
    $sourceManifestPath = Join-Path $metadataDirectory 'source-manifest.json'
    Write-Utf8NoBom `
        -Path $sourceManifestPath `
        -Content (([ordered]@{
                SchemaVersion = 1
                Canonicalization = 'Path NUL decimal-size NUL uppercase-SHA256, UTF-8, sorted ordinal by path, LF-separated'
                TreeSha256 = $sourceTreeSha256
                Files = $sourceFiles
            }) | ConvertTo-Json -Depth 8)
    $trackedStatus = @($gitStatus | Where-Object { -not $_.StartsWith('??') })
    $untrackedStatus = @($gitStatus | Where-Object { $_.StartsWith('??') })

    $provenance = [ordered]@{
        SchemaVersion = 4
        Product = 'PCM CDB Editor'
        Version = $Version
        SourceCommit = $sourceCommit
        Source = [ordered]@{
            Commit = $sourceCommit
            BuildMode = $sourceMode
            GitStatusSha256 = $initialGit.StatusSha256
            WorkingTreeDirty = $gitStatus.Count -gt 0
            TrackedChangesPresent = $trackedStatus.Count -gt 0
            UntrackedFilesPresent = $untrackedStatus.Count -gt 0
            GitStatusEntryCount = $gitStatus.Count
            Manifest = 'metadata/source-manifest.json'
            ManifestFileCount = $sourceFiles.Count
            TreeSha256 = $sourceTreeSha256
        }
        GeneratedUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
        BuildEnvironment = [ordered]@{
            OSDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            OSArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            ProcessArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        }
        DotNet = [ordered]@{
            Sdk = $requiredSdkVersion
            RuntimeFramework = $runtimeFrameworkVersion
            AppHostPack = $runtimeFrameworkVersion
            RuntimeSecurity = $runtimeSecurity
            RuntimeIdentifier = $runtimeIdentifier
        }
        InnoSetup = [ordered]@{
            Version = $innoVersion
            ReleaseTag = $innoReleaseTag
            DownloadUrl = $innoInstallerUrl
            InstallerSha256 = $innoInstallerSha256
            AuthenticodePublisher = 'Pyrsys B.V.'
            AttestationVerification = 'gh release verify-asset is-7_1_0 <asset> --repo jrsoftware/issrc'
            AcquisitionMode = 'Portable'
            PrivilegeMode = 'CurrentUser'
        }
        SQLiteExporter = [ordered]@{
            ExecutableSha256 = $exporterSha256
            ReadmePdfSha256 = $exporterReadmeSha256
        }
        Signing = 'Unsigned verification candidate'
    }
    Write-Utf8NoBom `
        -Path (Join-Path $metadataDirectory 'release-provenance.json') `
        -Content ($provenance | ConvertTo-Json -Depth 8)

    $verifiedTreeHash = & $verifyScriptPath `
        -EmitCandidateTreeHash `
        -Version $Version `
        -SourceCheckout $sourceCheckout `
        -RecordRestoreAssets `
        -PayloadDirectory $payloadDirectory `
        -ZipPath $zipPath `
        -InstallerPath $installerPath `
        -SbomPath $sbomManifestPath `
        -MetadataDirectory $metadataDirectory `
        -ChecksumsPath $checksumsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Release verification failed with exit code $LASTEXITCODE."
    }

    Assert-ReleaseSnapshot -Root $repositoryRoot -Files $sourceFiles -TreeSha256 $sourceTreeSha256
    Assert-ReleaseSourceState -Root $sourceCheckout -Commit $sourceCommit -StatusSha256 $initialGit.StatusSha256 -TreeSha256 $sourceTreeSha256

    Publish-ReleaseCandidate -Staged $releaseDirectory -Destination $publishedDirectory -RunId $runId -ArtifactsRoot $artifactsRoot -VerifiedTreeHash $verifiedTreeHash

    Write-Host ''
    Write-Host 'Release verification candidate created successfully.'
    Write-Host "Location: $publishedDirectory"
    Write-Host 'The artifacts are intentionally unsigned and have not been published.'
}
finally {
    if ($locationPushed) {
        Pop-Location
    }
    $env:NUGET_PACKAGES = $previousNugetPackages
    Remove-ReleaseScratch -Path $runRoot -ArtifactsRoot $artifactsRoot
}
