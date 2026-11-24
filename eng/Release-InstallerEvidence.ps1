function Assert-InstalledPayloadEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $EvidencePath,
        [Parameter(Mandatory = $true)][string] $InstallerPath,
        [Parameter(Mandatory = $true)][object[]] $PayloadFiles
    )

    $evidence = Get-Content -Raw -LiteralPath $EvidencePath | ConvertFrom-Json
    $installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
    if ([int] $evidence.SchemaVersion -ne 1 -or $evidence.Isolation -cne 'WindowsSandbox' -or
        [string]::IsNullOrWhiteSpace($evidence.WindowsVersion) -or
        [string] $evidence.InstallerSha256 -cne $installerHash) {
        throw 'Installed-payload evidence is missing its isolated environment or does not match this installer.'
    }
    foreach ($check in @('PerUserRegistration', 'OpenWithDefaultPreserved', 'StartMenuShortcut', 'UninstallRegistrationRemoved', 'UserDataPreserved')) {
        $result = $evidence.Checks.PSObject.Properties[$check]
        if ($null -eq $result -or $result.Value -isnot [bool] -or -not $result.Value) {
            throw "Installer behavior check did not pass: $check."
        }
    }

    $expected = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in $PayloadFiles) {
        $expected.Add([string] $file.Path, $file)
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $evidence.Files) {
        $path = [string] $file.Path
        if (-not $seen.Add($path) -or -not $expected.ContainsKey($path)) {
            throw "Unexpected or duplicate installed payload path: $path."
        }
        $reference = $expected[$path]
        if ([long] $file.Size -ne [long] $reference.Size -or [string] $file.Sha256 -cne [string] $reference.Sha256) {
            throw "Installed payload size or SHA-256 differs: $path."
        }
    }
    if ($seen.Count -ne $expected.Count) {
        $missing = @($expected.Keys | Where-Object { -not $seen.Contains($_) } | Sort-Object)
        throw "Installed payload files are missing: $($missing -join ', ')."
    }
}
