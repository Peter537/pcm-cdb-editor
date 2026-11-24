# Executed only by the Windows Sandbox logon command created by Test-InstallerPayload.ps1.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:USERNAME -cne 'WDAGUtilityAccount' -or -not [System.IO.Directory]::Exists('C:\PCMInstaller\Output')) {
    throw 'The installer probe requires its dedicated Windows Sandbox guest.'
}

function Invoke-ProbeProcess {
    param([string] $Path, [string[]] $Arguments, [string] $Operation)

    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(180000)) {
            Stop-Process -InputObject $process -Force
            throw "$Operation exceeded its 180-second deadline."
        }
        if ($process.ExitCode -ne 0) {
            throw "$Operation returned $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Probe {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Get-RegistryString {
    param([string] $Path, [string] $Name = '')

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $key = Get-Item -LiteralPath $Path
    try { return $key.GetValue($Name) }
    finally { $key.Close() }
}

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\PcmCdbEditor'
$installer = Join-Path $PSScriptRoot 'setup.exe'
$output = 'C:\PCMInstaller\Output'
$userData = Join-Path $env:LOCALAPPDATA 'PcmCdbEditor'
$marker = Join-Path $userData 'sandbox-preservation-marker.txt'
$classes = 'HKCU:\Software\Classes'
$extension = Join-Path $classes '.cdb'
$openWith = Join-Path $extension 'OpenWithProgids'
$progId = 'Peter537.PcmCdbEditor.cdb'
$handler = Join-Path $classes $progId
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{81DD1E0D-52AA-4E5F-BC2C-B884A3293B0F}_is1'
$default = 'PCMInstaller.SandboxExistingDefault'
try {
    Assert-Probe (-not (Test-Path -LiteralPath $installRoot)) 'The default installation directory already exists.'
    New-Item -Path $extension -Force | Out-Null
    Set-Item -LiteralPath $extension -Value $default
    [System.IO.Directory]::CreateDirectory($userData) | Out-Null
    [System.IO.File]::WriteAllText($marker, 'Disposable Windows Sandbox user data')
    $markerHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash

    Invoke-ProbeProcess -Path $installer -Operation 'Installer' -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART')
    Assert-Probe (Test-Path -LiteralPath $installRoot -PathType Container) 'The installer did not use the default per-user directory.'
    Assert-Probe (Test-Path -LiteralPath $uninstallKey) 'The per-user uninstall registration is missing.'
    $machineHandler = "HKLM:\Software\Classes\$progId"
    Assert-Probe (-not (Test-Path -LiteralPath $machineHandler)) 'Unexpected machine-wide handler registration.'
    Assert-Probe ($null -ne (Get-RegistryString -Path $openWith -Name $progId)) 'The Open with registration is missing.'
    $installedApp = Join-Path $installRoot 'PcmCdbEditor.exe'
    $openCommand = Get-RegistryString -Path (Join-Path $handler 'shell\open\command')
    Assert-Probe ($openCommand -ceq ('"{0}" "%1"' -f $installedApp)) 'The registered Open with command is invalid.'
    Assert-Probe ((Get-RegistryString -Path $extension) -ceq $default) 'Installing replaced the existing .cdb default.'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'PCM CDB Editor\PCM CDB Editor.lnk'
    Assert-Probe (Test-Path -LiteralPath $shortcut -PathType Leaf) 'The per-user Start Menu shortcut is missing.'
    $shell = New-Object -ComObject WScript.Shell
    try {
        Assert-Probe ($shell.CreateShortcut($shortcut).TargetPath -ceq $installedApp) 'The Start Menu shortcut points to an unexpected executable.'
    }
    finally {
        [void] [Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }

    # Inno owns these two uninstall files; report all other installed files, including unexpected extras.
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    $uninstallerData = Join-Path $installRoot 'unins000.dat'
    Assert-Probe ((Test-Path -LiteralPath $uninstaller -PathType Leaf) -and
        (Test-Path -LiteralPath $uninstallerData -PathType Leaf)) 'The expected Inno uninstaller files are missing.'
    $files = @(
        Get-ChildItem -LiteralPath $installRoot -File -Recurse -Force |
            Where-Object { $_.FullName -cnotin @($uninstaller, $uninstallerData) } |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    Path = $_.FullName.Substring($installRoot.Length + 1).Replace('\', '/')
                    Size = $_.Length
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )

    Invoke-ProbeProcess -Path $uninstaller -Operation 'Uninstaller' -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Assert-Probe (-not (Test-Path -LiteralPath $installedApp)) 'Uninstall left the application executable installed.'
    foreach ($file in $files) {
        Assert-Probe (-not (Test-Path -LiteralPath (Join-Path $installRoot $file.Path))) 'Uninstall left a payload file installed.'
    }
    Assert-Probe (-not (Test-Path -LiteralPath $uninstallKey)) 'Uninstall left the uninstall registration.'
    Assert-Probe (-not (Test-Path -LiteralPath $handler)) 'Uninstall left the shell handler.'
    Assert-Probe ($null -eq (Get-RegistryString -Path $openWith -Name $progId)) 'Uninstall left the Open with registration.'
    Assert-Probe (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall left the Start Menu shortcut.'
    Assert-Probe ((Get-RegistryString -Path $extension) -ceq $default) 'Uninstall changed the existing .cdb default.'
    Assert-Probe ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ceq $markerHash) 'Uninstall changed the preserved user-data marker.'

    $evidence = [ordered]@{
        SchemaVersion = 1
        Isolation = 'WindowsSandbox'
        WindowsVersion = [Environment]::OSVersion.Version.ToString()
        VerifiedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        InstallerSha256 = $installerHash
        Checks = [ordered]@{
            PerUserRegistration = $true
            OpenWithDefaultPreserved = $true
            StartMenuShortcut = $true
            UninstallRegistrationRemoved = $true
            UserDataPreserved = $true
        }
        Files = $files
    }
    $temporaryEvidence = Join-Path $output 'probe.tmp'
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $temporaryEvidence -Encoding UTF8
    [System.IO.File]::Move($temporaryEvidence, (Join-Path $output 'installed-payload.json'))
}
catch {
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $output 'failed.txt') -Encoding UTF8
}
finally {
    & "$env:SystemRoot\System32\shutdown.exe" /s /t 0
}
