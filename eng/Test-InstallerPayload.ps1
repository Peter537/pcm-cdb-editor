[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CandidateDirectory,

    [ValidateRange(1, 15)]
    [int] $TimeoutMinutes = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Filesystem.ps1')
. (Join-Path $PSScriptRoot 'Release-InstallerEvidence.ps1')

$sandbox = Get-Command 'WindowsSandbox.exe' -CommandType Application -ErrorAction SilentlyContinue
if ($null -eq $sandbox) {
    throw 'Windows Sandbox is unavailable. Enable it in a supported Windows environment and rerun this check there.'
}

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
$candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
Assert-DescendantPath -Child $candidate -Parent $artifacts -Purpose 'Installer verification candidate'
Assert-NoReparsePointInDirectoryChain -Path $candidate -Root $artifacts -Purpose 'Installer verification candidate'
$payload = Join-Path $candidate 'payload'
$packages = Join-Path $candidate 'packages'
$metadata = Join-Path $candidate 'metadata'
foreach ($directory in @($payload, $packages, $metadata)) {
    Assert-NoReparsePointInDirectoryChain -Path $directory -Root $artifacts -Purpose 'Installer verification input'
    Assert-NoReparsePointInDirectoryTree -Path $directory -Purpose 'Installer verification input'
}
$installers = @(Get-ChildItem -LiteralPath $packages -File -Filter 'PcmCdbEditor-*-win-x64-setup.exe')
if ($installers.Count -ne 1) {
    throw 'The candidate must contain exactly one win-x64 application installer.'
}
$installer = $installers[0].FullName
$payloadFiles = @(
    Get-ChildItem -LiteralPath $payload -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
            Size = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
)
if ($payloadFiles.Count -eq 0) {
    throw 'Installer verification requires a nonempty staged application payload.'
}

$run = Join-Path $artifacts ('.installer-probe-' + [Guid]::NewGuid().ToString('N'))
Assert-DescendantPath -Child $run -Parent $artifacts -Purpose 'Installer probe scratch'
if (Test-Path -LiteralPath $run) {
    throw 'The generated installer-probe scratch directory already exists.'
}
$process = $null
Write-Host "Installer probe scratch: $run"
try {
    $inputDirectory = Join-Path $run 'input'
    $outputDirectory = Join-Path $run 'output'
    [System.IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    Copy-Item -LiteralPath $installer -Destination (Join-Path $inputDirectory 'setup.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-InstallerProbe.ps1') -Destination $inputDirectory
    $escapedInput = [Security.SecurityElement]::Escape($inputDirectory)
    $escapedOutput = [Security.SecurityElement]::Escape($outputDirectory)
    $configuration = Join-Path $run 'probe.wsb'
    @"
<Configuration>
  <Networking>Disable</Networking>
  <VGpu>Disable</VGpu>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedInput</HostFolder>
      <SandboxFolder>C:\PCMInstaller\Input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedOutput</HostFolder>
      <SandboxFolder>C:\PCMInstaller\Output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PCMInstaller\Input\Invoke-InstallerProbe.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $configuration -Encoding UTF8

    # The Sandbox client is an interactive VM window that also surfaces feature/startup failures.
    $process = Start-Process -FilePath $sandbox.Source -ArgumentList ('"{0}"' -f $configuration) -PassThru -WindowStyle Normal
    $result = Join-Path $outputDirectory 'installed-payload.json'
    $failure = Join-Path $outputDirectory 'failed.txt'
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $result -PathType Leaf)) {
        if (Test-Path -LiteralPath $failure -PathType Leaf) {
            throw ('Windows Sandbox installer check failed: ' + (Get-Content -Raw -LiteralPath $failure))
        }
        if ($elapsed.Elapsed -ge [TimeSpan]::FromMinutes($TimeoutMinutes)) {
            throw "Windows Sandbox did not return installer evidence within $TimeoutMinutes minutes."
        }
        Start-Sleep -Milliseconds 500
    }

    Assert-NoReparsePointInDirectoryTree -Path $outputDirectory -Purpose 'Installer probe result'
    Assert-InstalledPayloadEvidence -EvidencePath $result -InstallerPath $installer -PayloadFiles $payloadFiles
    $destination = Join-Path $metadata 'installed-payload.json'
    $stagedEvidence = Join-Path $run 'verified-payload.json'
    Copy-Item -LiteralPath $result -Destination $stagedEvidence
    Move-Item -LiteralPath $stagedEvidence -Destination $destination -Force
    Write-Host "Verified Windows Sandbox installation and uninstall evidence: $destination"
}
finally {
    if ($null -ne $process) {
        try {
            if (-not $process.WaitForExit(10000)) {
                Stop-Process -InputObject $process -Force -ErrorAction SilentlyContinue
                [void] $process.WaitForExit(5000)
            }
        }
        finally {
            $process.Dispose()
        }
    }
    Remove-ReleaseScratch -Path $run -ArtifactsRoot $artifacts
}
