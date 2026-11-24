function Assert-DescendantPath {
    param(
        [Parameter(Mandatory = $true)][string] $Child,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Purpose
    )

    $childFullPath = [System.IO.Path]::GetFullPath($Child)
    $parentFullPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $prefix = "$parentFullPath$([System.IO.Path]::DirectorySeparatorChar)"
    if (-not $childFullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Purpose must be below $parentFullPath; resolved path was $childFullPath."
    }
}

function Assert-NoReparsePointInDirectoryChain {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Purpose
    )

    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootPrefix = "$rootFull$([System.IO.Path]::DirectorySeparatorChar)"
    if (-not $pathFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $pathFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Purpose escaped its approved root: $pathFull"
    }

    $current = $pathFull
    while ($true) {
        if ([System.IO.Directory]::Exists($current)) {
            $directory = New-Object System.IO.DirectoryInfo($current)
            if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Purpose must not traverse a reparse point: $current"
            }
        }

        if ($current.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Purpose could not be resolved safely below $rootFull."
        }
        $current = $parent.TrimEnd('\', '/')
    }
}

function Assert-NoReparsePointInDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Purpose
    )

    $root = New-Object System.IO.DirectoryInfo([System.IO.Path]::GetFullPath($Path))
    if (-not $root.Exists) {
        throw "$Purpose directory does not exist: $($root.FullName)"
    }

    $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in $directory.EnumerateFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Purpose must not contain a reparse point: $($entry.FullName)"
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push((New-Object System.IO.DirectoryInfo($entry.FullName)))
            }
        }
    }
}

function Get-ReleaseCandidateTreeHash {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-NoReparsePointInDirectoryTree -Path $Path -Purpose 'Release candidate digest'
    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd([char]92, [char]47)
    $files = [string[]] @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | ForEach-Object { $_.FullName })
    [Array]::Sort($files, [StringComparer]::Ordinal)
    $records = foreach ($file in $files) {
        $relative = $file.Substring($root.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        "$hash *$relative"
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($records -join [char]10))))
}

function Publish-ReleaseCandidate {
    param(
        [Parameter(Mandatory = $true)][string] $Staged,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][string] $RunId,
        [Parameter(Mandatory = $true)][string] $ArtifactsRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-F0-9]{64}$')][string] $VerifiedTreeHash
    )

    Assert-DescendantPath -Child $Staged -Parent $ArtifactsRoot -Purpose 'Staged release candidate'
    Assert-DescendantPath -Child $Destination -Parent $ArtifactsRoot -Purpose 'Published release candidate'
    Assert-NoReparsePointInDirectoryChain -Path $Staged -Root $ArtifactsRoot -Purpose 'Staged release candidate'
    Assert-NoReparsePointInDirectoryChain -Path $Destination -Root $ArtifactsRoot -Purpose 'Published release candidate'
    Assert-NoReparsePointInDirectoryTree -Path $Staged -Purpose 'Staged release candidate'
    $destinationParent = Split-Path -Parent $Destination
    $version = Split-Path -Leaf $Destination
    $promotionLock = Join-Path $destinationParent ".promotion-$version.lock"
    $lock = [System.IO.File]::Open($promotionLock, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        if ((Get-ReleaseCandidateTreeHash -Path $Staged) -cne $VerifiedTreeHash) {
            throw 'The candidate tree changed after release verification.'
        }
        $previousCandidate = $null
        if ([System.IO.Directory]::Exists($Destination)) {
            $previousCandidate = Join-Path $destinationParent "$version.previous-$RunId"
            Assert-DescendantPath -Child $previousCandidate -Parent $destinationParent -Purpose 'Previous release candidate'
            Assert-NoReparsePointInDirectoryTree -Path $Destination -Purpose 'Previous release candidate'
            [System.IO.Directory]::Move($Destination, $previousCandidate)
        }
        try {
            [System.IO.Directory]::Move($Staged, $Destination)
        }
        catch {
            if ($null -ne $previousCandidate -and -not (Test-Path -LiteralPath $Destination)) {
                [System.IO.Directory]::Move($previousCandidate, $Destination)
            }
            throw
        }
        if ($null -ne $previousCandidate) {
            $bytes = (Get-ChildItem -LiteralPath $previousCandidate -File -Recurse -Force | Measure-Object Length -Sum).Sum
            Write-Host "Previous candidate retained at $previousCandidate ($bytes bytes). Review after accepting the new candidate."
        }
    }
    finally {
        $lock.Dispose()
    }
}

function Remove-ReleaseScratch {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ArtifactsRoot
    )

    if (-not [System.IO.Directory]::Exists($Path)) {
        return
    }
    Assert-DescendantPath -Child $Path -Parent $ArtifactsRoot -Purpose 'Release scratch cleanup'
    Assert-NoReparsePointInDirectoryChain -Path $Path -Root $ArtifactsRoot -Purpose 'Release scratch cleanup'
    Assert-NoReparsePointInDirectoryTree -Path $Path -Purpose 'Release scratch cleanup'
    for ($attempt = 0; ; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch [System.IO.IOException] {
            if ($attempt -ge 4) { throw }
            Start-Sleep -Milliseconds 150
        }
    }
}
