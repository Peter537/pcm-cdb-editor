# Microsoft services the runtime independently of the project package graph.
function ConvertFrom-ReleaseTimestamp {
    param([Parameter(Mandatory = $true)] $Value)

    if ($Value -is [DateTimeOffset]) {
        return $Value
    }
    if ($Value -is [DateTime]) {
        return [DateTimeOffset] $Value
    }

    return [DateTimeOffset]::Parse([string] $Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-RuntimeSecurityEvidence {
    param([Parameter(Mandatory = $true)][version] $RuntimeVersion)

    $uri = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
    $metadata = Invoke-RestMethod -Uri $uri -TimeoutSec 30
    $endOfSupport = ConvertFrom-ReleaseTimestamp -Value $metadata.'eol-date'
    if ($metadata.'channel-version' -ne '10.0' -or
        $metadata.'support-phase' -notin @('active', 'maintenance') -or
        $endOfSupport -le [DateTimeOffset]::UtcNow) {
        throw 'The selected .NET runtime channel is outside its support period.'
    }

    $securityReleases = @($metadata.releases | Where-Object { $_.security -eq $true -and $_.runtime.version -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version] $_.runtime.version } -Descending)
    if ($securityReleases.Count -eq 0) {
        throw 'Microsoft did not supply any .NET 10 security servicing evidence.'
    }

    $securityRelease = $securityReleases[0]
    if ($RuntimeVersion -lt [version] $securityRelease.runtime.version -or
        -not ($metadata.releases.runtime.version -contains $RuntimeVersion.ToString())) {
        throw "Runtime $RuntimeVersion does not meet Microsoft's .NET 10 security servicing floor $($securityRelease.runtime.version)."
    }

    [ordered]@{
        Metadata = $uri
        CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SupportPhase = [string] $metadata.'support-phase'
        EndOfSupport = $endOfSupport.ToString('O')
        RuntimePack = 'Microsoft.NETCore.App.Runtime.win-x64'
        AppHostPack = 'Microsoft.NETCore.App.Host.win-x64'
        Version = $RuntimeVersion.ToString()
        SecurityFloor = [string] $securityRelease.runtime.version
        Cves = @($securityRelease.'cve-list' | ForEach-Object { $_.'cve-id' })
    }
}
