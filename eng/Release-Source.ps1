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

function Get-ReleaseGitState {
    param([Parameter(Mandatory = $true)][string] $Root)

    $commit = (& git -C $Root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or [string] $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the source commit.'
    }
    $status = @(& git -C $Root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to record the release source working-tree state.'
    }
    return [pscustomobject]@{
        Commit = [string] $commit
        Status = $status
        StatusSha256 = Get-StringSha256 -Value ([string]::Join("`n", [string[]] $status) + "`n")
    }
}

function Get-SourceManifestFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    # Include build inputs tracked by Git and unignored changes for local candidates.
    # Construct the snapshot from this list, so ignored build inputs cannot leak in.
    $listing = @(& git -C $Root -c core.quotepath=false ls-files -z --cached --others --exclude-standard --deduplicate)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate release sources.'
    }
    $paths = ([string]::Join("`n", [string[]] $listing)).Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries)
    [Array]::Sort($paths, [System.StringComparer]::Ordinal)
    foreach ($path in $paths) {
        $fullPath = Join-Path $Root $path
        if ([System.IO.Directory]::Exists($fullPath)) {
            throw "Release source '$path' is a directory. Submodule inputs require separate snapshot support."
        }
        if (-not [System.IO.File]::Exists($fullPath)) {
            continue  # A tracked deletion is represented by its absence and the Git status digest.
        }
        Get-ReleaseSourceFileRecord -Path $path -Root $Root
    }
}

function Get-ReleaseSourceFileRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root
    )
    $file = [System.IO.FileInfo]::new((Join-Path $Root $Path))
    Assert-NoReparsePointInDirectoryChain -Path $file.DirectoryName -Root $Root -Purpose 'Source-manifest path'
    if (-not $file.Exists -or ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release source '$Path' must be a regular file."
    }
    [pscustomobject]@{
        Path = $Path
        Size = $file.Length
        Sha256 = Get-Sha256 -Path $file.FullName
    }
}

function Get-ReleaseSourceDigest {
    param([Parameter(Mandatory = $true)][object[]] $Files)

    $canonicalLines = @($Files | ForEach-Object { "$($_.Path)`0$($_.Size)`0$($_.Sha256)" })
    Get-StringSha256 -Value ([string]::Join("`n", [string[]] $canonicalLines))
}

function Copy-ReleaseSourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][object[]] $Files,
        [string] $CleanCommit
    )
    if (-not [string]::IsNullOrEmpty($CleanCommit)) {
        # Export the recorded commit through Git's checkout filters using a private index.
        # The developer's index and checkout are never used as build inputs for this candidate.
        $previousIndex = $env:GIT_INDEX_FILE
        try {
            $env:GIT_INDEX_FILE = Join-Path (Split-Path -Parent $Destination) 'captured-index'
            & git -C $Root read-tree $CleanCommit
            if ($LASTEXITCODE -ne 0) { throw 'Unable to read the captured commit into the private Git index.' }
            $prefix = $Destination.Replace('\', '/') + '/'
            & git -C $Root checkout-index --all "--prefix=$prefix"
            if ($LASTEXITCODE -ne 0) { throw 'Unable to export the captured source commit.' }
        }
        finally {
            $env:GIT_INDEX_FILE = $previousIndex
        }
        return
    }
    foreach ($entry in $Files) {
        $sourcePath = Join-Path $Root $entry.Path
        $destinationPath = Join-Path $Destination $entry.Path
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        [System.IO.File]::Copy($sourcePath, $destinationPath, $false)
    }
}

function Assert-ReleaseSourceState {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Commit,
        [Parameter(Mandatory = $true)][string] $StatusSha256,
        [Parameter(Mandatory = $true)][string] $TreeSha256
    )
    $state = Get-ReleaseGitState -Root $Root
    $files = @(Get-SourceManifestFiles -Root $Root)
    if ($state.Commit -cne $Commit -or $state.StatusSha256 -cne $StatusSha256 -or
        (Get-ReleaseSourceDigest -Files $files) -cne $TreeSha256) {
        throw 'The source commit, working-tree status, or source file contents changed since release capture.'
    }
}

function Assert-ReleaseSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][object[]] $Files,
        [Parameter(Mandatory = $true)][string] $TreeSha256,
        [switch] $BeforeBuild
    )
    if ($BeforeBuild) {
        Assert-NoReparsePointInDirectoryTree -Path $Root -Purpose 'Isolated release snapshot'
        if (@(Get-ChildItem -LiteralPath $Root -Recurse -Force -File).Count -ne $Files.Count) {
            throw 'The isolated release snapshot does not have the captured source file set.'
        }
    }
    $snapshotFiles = @(foreach ($entry in $Files) { Get-ReleaseSourceFileRecord -Path $entry.Path -Root $Root })
    if ((Get-ReleaseSourceDigest -Files $snapshotFiles) -cne $TreeSha256) {
        throw 'An input in the isolated release snapshot differs from the captured source manifest.'
    }
}
