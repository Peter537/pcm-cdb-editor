[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PythonPath,

    [string] $SourceSvg,

    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($SourceSvg)) {
    $SourceSvg = Join-Path $repositoryRoot 'src\PcmCdbEditor.App\Assets\App.svg'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'src\PcmCdbEditor.App\Assets'
}

$sourceSvgFullPath = [System.IO.Path]::GetFullPath($SourceSvg)
$outputDirectoryFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$assetsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src\PcmCdbEditor.App\Assets')).TrimEnd('\', '/')
$assetsPrefix = "$assetsRoot$([System.IO.Path]::DirectorySeparatorChar)"

if (-not [System.IO.File]::Exists($sourceSvgFullPath)) {
    throw "The source SVG does not exist: $sourceSvgFullPath"
}

if ($outputDirectoryFullPath -ne $assetsRoot -and -not $outputDirectoryFullPath.StartsWith($assetsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve to the app Assets directory or one of its descendants: $assetsRoot"
}

$pythonCommand = Get-Command -Name $PythonPath -ErrorAction SilentlyContinue
if ($null -eq $pythonCommand) {
    if (-not [System.IO.File]::Exists([System.IO.Path]::GetFullPath($PythonPath))) {
        throw "The explicitly supplied Python renderer was not found: $PythonPath"
    }
    $pythonExecutable = [System.IO.Path]::GetFullPath($PythonPath)
}
else {
    $pythonExecutable = $pythonCommand.Source
}

$svgText = Get-Content -Raw -LiteralPath $sourceSvgFullPath
foreach ($requiredIdentityFragment in @('#315FCC', '#1A8F85', 'viewBox="0 0 512 512"')) {
    if ($svgText.IndexOf($requiredIdentityFragment, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The SVG is not the approved cobalt/teal 512-square identity; missing '$requiredIdentityFragment'."
    }
}

[System.IO.Directory]::CreateDirectory($outputDirectoryFullPath) | Out-Null

$pythonScript = @'
import os
import sys

try:
    from PIL import Image, ImageDraw
except ImportError as exc:
    raise SystemExit(
        "Generate-Assets.ps1 requires Pillow in the explicitly supplied Python environment; "
        f"missing dependency: {exc.name}"
    ) from exc

_source_svg, output_dir = sys.argv[1], sys.argv[2]
sizes = (16, 24, 32, 48, 64, 128, 256, 512)

# This fixed geometry mirrors App.svg's 512x512 cobalt rounded tile, white grid,
# and teal edit mark. Rendering at 4096 then applying one LANCZOS reduction
# makes every output independent of an external SVG engine and its version.
scale = 8
canvas_size = 512 * scale
image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)

def box(values):
    return tuple(int(value * scale) for value in values)

def points(values):
    return [(int(x * scale), int(y * scale)) for x, y in values]

draw.rounded_rectangle(box((0, 0, 512, 512)), radius=104 * scale, fill="#315FCC")

# Explicit white grid outline and dividers mirror App.svg.
draw.rounded_rectangle(
    box((88, 112, 424, 400)),
    radius=24 * scale,
    fill=None,
    outline="#FFFFFF",
    width=28 * scale,
)
draw.line(points(((104, 208), (408, 208))), fill="#FFFFFF", width=24 * scale)
draw.line(points(((104, 288), (408, 288))), fill="#FFFFFF", width=24 * scale)
draw.line(points(((232, 128), (232, 384))), fill="#FFFFFF", width=24 * scale)

edit = points(((244, 367), (256, 301), (358, 199), (412, 253), (310, 355)))
draw.polygon(edit, fill="#1A8F85")
outline = edit + [edit[0]]
draw.line(outline, fill="#FFFFFF", width=14 * scale, joint="curve")
radius = 7 * scale
for x, y in edit:
    draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill="#FFFFFF")
draw.line(points(((358, 253), (384, 279))), fill="#FFFFFF", width=14 * scale)

rendered = {}
for size in sizes:
    output = image.resize((size, size), Image.Resampling.LANCZOS)
    rendered[size] = output
    output.save(
        os.path.join(output_dir, f"App-{size}.png"),
        format="PNG",
        optimize=False,
        compress_level=9,
    )

# Pillow writes PNG-compressed frames for all requested Windows icon sizes.
rendered[256].save(
    os.path.join(output_dir, "App.ico"),
    format="ICO",
    sizes=[(size, size) for size in (16, 24, 32, 48, 64, 128, 256)],
    bitmap_format="png",
)
'@

$temporaryScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "PcmCdbEditor.GenerateAssets.$([System.Guid]::NewGuid().ToString('N')).py"
try {
    [System.IO.File]::WriteAllText(
        $temporaryScriptPath,
        $pythonScript,
        (New-Object System.Text.UTF8Encoding($false)))
    & $pythonExecutable -I $temporaryScriptPath $sourceSvgFullPath $outputDirectoryFullPath
    if ($LASTEXITCODE -ne 0) {
        throw "Asset rendering failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ([System.IO.File]::Exists($temporaryScriptPath)) {
        Remove-Item -LiteralPath $temporaryScriptPath -Force
    }
}

$expectedFiles = @(
    @(16, 24, 32, 48, 64, 128, 256, 512 | ForEach-Object { "App-$_.png" })
    'App.ico'
)

foreach ($expectedFile in $expectedFiles) {
    $expectedPath = Join-Path $outputDirectoryFullPath $expectedFile
    if (-not [System.IO.File]::Exists($expectedPath) -or (Get-Item -LiteralPath $expectedPath).Length -eq 0) {
        throw "Asset generation did not produce a non-empty $expectedFile."
    }
}

Write-Host "Generated deterministic app assets from $sourceSvgFullPath"
$expectedFiles | ForEach-Object { Write-Host "  $(Join-Path $outputDirectoryFullPath $_)" }
