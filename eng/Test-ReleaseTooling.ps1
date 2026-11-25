[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Filesystem.ps1')
. (Join-Path $PSScriptRoot 'Release-RuntimePolicy.ps1')
. (Join-Path $PSScriptRoot 'Release-Source.ps1')
. (Join-Path $PSScriptRoot 'Release-InstallerEvidence.ps1')

function Get-Sha256 {
    param([string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Rejected {
    param([scriptblock] $Check, [string] $MessagePattern)

    try { & $Check }
    catch {
        if ($_.Exception.Message -match $MessagePattern) { return }
        throw
    }
    throw "Expected rejection matching '$MessagePattern'."
}

function Invoke-FixtureGit {
    param([string[]] $Arguments)

    & git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to prepare the synthetic release checkout.' }
}

$artifacts = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$run = Join-Path $artifacts ('.release-tests-' + [Guid]::NewGuid().ToString('N'))
Assert-DescendantPath -Child $run -Parent $artifacts -Purpose 'Release fixture scratch'
Assert-NoReparsePointInDirectoryChain -Path $run -Root $artifacts -Purpose 'Release fixture scratch'
if (Test-Path -LiteralPath $run) { throw 'The generated release fixture directory already exists.' }
try {
    $fixture = Join-Path $run 'source'
    [System.IO.Directory]::CreateDirectory($fixture) | Out-Null
    Invoke-FixtureGit -Arguments @('init', '-q')
    $sourceFile = Join-Path $fixture 'build.txt'
    [System.IO.File]::WriteAllText($sourceFile, 'input')
    Invoke-FixtureGit -Arguments @('add', 'build.txt')
    Invoke-FixtureGit -Arguments @('commit', '-qm', 'Initial')

    $clean = Get-ReleaseGitState -Root $fixture
    $cleanFiles = @(Get-SourceManifestFiles -Root $fixture)
    $cleanDigest = Get-ReleaseSourceDigest -Files $cleanFiles
    $snapshot = Join-Path $run 'snapshot'
    Copy-ReleaseSourceSnapshot -Root $fixture -Destination $snapshot -Files $cleanFiles -CleanCommit $clean.Commit
    Assert-ReleaseSnapshot -Root $snapshot -Files $cleanFiles -TreeSha256 $cleanDigest -BeforeBuild
    [System.IO.File]::WriteAllText((Join-Path $snapshot 'additional-input.txt'), 'extra')
    Assert-Rejected { Assert-ReleaseSnapshot -Root $snapshot -Files $cleanFiles -TreeSha256 $cleanDigest -BeforeBuild } 'does not have the captured source file set'
    Assert-ReleaseSourceState -Root $fixture -Commit $clean.Commit -StatusSha256 $clean.StatusSha256 -TreeSha256 $cleanDigest

    [System.IO.File]::WriteAllText($sourceFile, 'dirty')
    $dirty = Get-ReleaseGitState -Root $fixture
    $dirtyFiles = @(Get-SourceManifestFiles -Root $fixture)
    $dirtyDigest = Get-ReleaseSourceDigest -Files $dirtyFiles
    $dirtySnapshot = Join-Path $run 'dirty-snapshot'
    Copy-ReleaseSourceSnapshot -Root $fixture -Destination $dirtySnapshot -Files $dirtyFiles
    [System.IO.File]::WriteAllText($sourceFile, 'later')
    Assert-Rejected { Assert-ReleaseSourceState -Root $fixture -Commit $dirty.Commit -StatusSha256 $dirty.StatusSha256 -TreeSha256 $dirtyDigest } 'changed since release capture'
    Assert-ReleaseSnapshot -Root $dirtySnapshot -Files $dirtyFiles -TreeSha256 $dirtyDigest
    [System.IO.File]::WriteAllText((Join-Path $dirtySnapshot 'build.txt'), 'other')
    Assert-Rejected { Assert-ReleaseSnapshot -Root $dirtySnapshot -Files $dirtyFiles -TreeSha256 $dirtyDigest } 'differs'

    [System.IO.File]::WriteAllText($sourceFile, 'input')
    [System.IO.File]::WriteAllText((Join-Path $fixture 'new.txt'), 'extra')
    Assert-Rejected { Assert-ReleaseSourceState -Root $fixture -Commit $clean.Commit -StatusSha256 $clean.StatusSha256 -TreeSha256 $cleanDigest } 'changed since release capture'
    Remove-Item -LiteralPath (Join-Path $fixture 'new.txt')
    Invoke-FixtureGit -Arguments @('commit', '--allow-empty', '-qm', 'Independent HEAD change')
    Assert-Rejected { Assert-ReleaseSourceState -Root $fixture -Commit $clean.Commit -StatusSha256 $clean.StatusSha256 -TreeSha256 $cleanDigest } 'changed since release capture'
    Write-Host 'PASS source provenance rejects changed bytes, input additions, and a changed HEAD.'

    $previousCulture = [System.Globalization.CultureInfo]::CurrentCulture
    try {
        [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('da-DK')
        $expected = [DateTimeOffset]::new(2028, 11, 14, 0, 0, 0, [TimeSpan]::FromHours(1))
        $timestamp = $expected.ToString('O')
        $provenance = "{""EndOfSupport"":""$timestamp""}" | ConvertFrom-Json
        if ((ConvertFrom-ReleaseTimestamp -Value $provenance.EndOfSupport) -ne $expected -or
            (ConvertFrom-ReleaseTimestamp -Value $timestamp) -ne $expected) {
            throw 'Runtime evidence timestamp changed under Danish date formatting.'
        }
        Write-Host 'PASS runtime evidence timestamps are culture independent.'
    }
    finally {
        [System.Globalization.CultureInfo]::CurrentCulture = $previousCulture
    }

    $old = Join-Path $run 'output\0.1.0'
    $staged = Join-Path $run 'staged'
    [System.IO.Directory]::CreateDirectory($old) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $old 'candidate.txt'), 'previous')
    Assert-Rejected { Publish-ReleaseCandidate -Staged $staged -Destination $old -RunId 'fixture' -ArtifactsRoot $artifacts -VerifiedTreeHash ('0' * 64) } 'directory does not exist'
    if ((Get-Content -Raw -LiteralPath (Join-Path $old 'candidate.txt')) -cne 'previous') { throw 'Failed promotion lost the previous candidate.' }
    [System.IO.Directory]::CreateDirectory($staged) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $staged 'candidate.txt'), 'verified')
    $candidateHash = Get-ReleaseCandidateTreeHash -Path $staged
    [System.IO.File]::WriteAllText((Join-Path $staged 'candidate.txt'), 'changed')
    Assert-Rejected { Publish-ReleaseCandidate -Staged $staged -Destination $old -RunId 'changed' -ArtifactsRoot $artifacts -VerifiedTreeHash $candidateHash } 'changed after release verification'
    if ((Get-Content -Raw -LiteralPath (Join-Path $old 'candidate.txt')) -cne 'previous') { throw 'Changed staged content displaced the previous candidate.' }
    [System.IO.File]::WriteAllText((Join-Path $staged 'candidate.txt'), 'verified')
    Publish-ReleaseCandidate -Staged $staged -Destination $old -RunId 'fixture' -ArtifactsRoot $artifacts -VerifiedTreeHash $candidateHash
    if ((Get-Content -Raw -LiteralPath (Join-Path $old 'candidate.txt')) -cne 'verified' -or
        (Get-Content -Raw -LiteralPath "$old.previous-fixture\candidate.txt") -cne 'previous') { throw 'Promotion did not preserve both candidate versions.' }
    Write-Host 'PASS candidate promotion preserves the previous candidate.'

    $installer = Join-Path $run 'reference.exe'
    [System.IO.File]::WriteAllText($installer, 'disposable synthetic installer identity')
    $file = [pscustomobject]@{ Path = 'bin/app.exe'; Size = 7; Sha256 = '1' * 64 }
    $files = @($file, [pscustomobject]@{ Path = 'bin/library.dll'; Size = 12; Sha256 = '2' * 64 })
    $evidence = [ordered]@{
        SchemaVersion = 1
        Isolation = 'WindowsSandbox'
        WindowsVersion = 'test-fixture'
        InstallerSha256 = Get-Sha256 $installer
        Files = $files
        Checks = [ordered]@{
            PerUserRegistration = $true
            OpenWithDefaultPreserved = $true
            StartMenuShortcut = $true
            UninstallRegistrationRemoved = $true
            UserDataPreserved = $true
        }
    }
    $evidencePath = Join-Path $run 'test-only-evidence.json'
    function Write-FixtureEvidence { $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $evidencePath -Encoding UTF8 }
    function Assert-FixtureEvidence { Assert-InstalledPayloadEvidence -EvidencePath $evidencePath -InstallerPath $installer -PayloadFiles $files }
    Write-FixtureEvidence
    Assert-FixtureEvidence
    $evidence.Files = @($file)
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'files are missing'
    $evidence.Files = @($file, $file)
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'duplicate installed payload'
    $evidence.Files = @($file, [pscustomobject]@{ Path = 'bin/additional.dll'; Size = 12; Sha256 = '2' * 64 })
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'Unexpected or duplicate installed payload'
    $evidence.Files = @([pscustomobject]@{ Path = $file.Path; Size = 7; Sha256 = '9' * 64 }, $files[1])
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'SHA-256 differs'
    $evidence.Files = $files
    $evidence.Checks.UserDataPreserved = $false
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'behavior check did not pass'
    $evidence.Checks.UserDataPreserved = $true
    $evidence.InstallerSha256 = '0' * 64
    Write-FixtureEvidence
    Assert-Rejected { Assert-FixtureEvidence } 'does not match this installer'
    Write-Host 'PASS installer evidence rejects missing, added, duplicate, changed, and wrong-installer evidence.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $run 'candidate.zip'
    $payloadFiles = @(
        [pscustomobject]@{ Path = 'bin/first.txt'; Sha256 = $null }
        [pscustomobject]@{ Path = 'bin/second.txt'; Sha256 = $null }
    )
    foreach ($file in $payloadFiles) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($file.Path)
        $file.Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
    function Write-ArchiveFixture {
        param([string[]] $EntryPaths, [string] $ChangedPath)

        if ([IO.File]::Exists($archive)) { [IO.File]::Delete($archive) }
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($path in $EntryPaths) {
                $entry = $zip.CreateEntry($path)
                if ($path.EndsWith('/')) { continue }
                $content = if ($path -ceq $ChangedPath) { 'changed-bytes' } else { $path }
                $bytes = [Text.Encoding]::UTF8.GetBytes($content)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) }
                finally { $stream.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }

    $paths = @($payloadFiles.Path)
    Write-ArchiveFixture -EntryPaths ($paths + 'bin/')
    Assert-ZipMatchesPayload -PayloadFiles $payloadFiles -ArchivePath $archive
    Write-ArchiveFixture -EntryPaths @($paths[0])
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'Missing: \[bin/second\.txt\]'
    Write-ArchiveFixture -EntryPaths ($paths + 'extra.txt')
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'extra: \[extra\.txt\]'
    Write-ArchiveFixture -EntryPaths ($paths + $paths[0])
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'duplicate path: bin/first\.txt'
    Write-ArchiveFixture -EntryPaths $paths -ChangedPath $paths[1]
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'hash mismatch: \[bin/second\.txt\]'
    Write-ArchiveFixture -EntryPaths ($paths + '../outside.txt')
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'ZIP contains an unsafe path'
    Write-ArchiveFixture -EntryPaths ($paths + '../')
    Assert-Rejected { Assert-ZipMatchesPayload $payloadFiles $archive } 'ZIP contains an unsafe path'
    Write-Host 'PASS ZIP verification accepts matching payload and rejects missing, extra, duplicate, changed, and unsafe entries.'
}
finally {
    Remove-ReleaseScratch -Path $run -ArtifactsRoot $artifacts
}
if (Test-Path -LiteralPath $run) { throw 'The disposable release regression fixture was retained.' }
