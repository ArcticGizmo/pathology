# Regenerates every icon asset from the source-of-truth SVG (pathology.svg, repo root).
#
#   src/Pathology.App/Assets/pathology.ico   multi-res ICO  (window, .exe ApplicationIcon, installer)
#   landing-icon.png                         512x512 PNG    (README header)
#
# The rasters come from tools/IconGen, which renders the SVG with Svg.Skia (SkiaSharp).
# Run this after editing pathology.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
