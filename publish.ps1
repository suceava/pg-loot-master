# Build PgLootMaster as a self-contained single-file Windows executable.
#
# Usage:
#   .\publish.ps1                 — build only, output to dist\
#   .\publish.ps1 -Release v1.0.0 — build, then create a GitHub release with the exe attached
#
# Output:
#   .\dist\PgLootMaster.exe   (single file — panel templates are baked into the assembly)
#
# Distributable: ship the .exe directly, or use -Release to upload via gh CLI.

param(
    [string]$Release,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$publishDir = Join-Path $root 'src\PgLootMaster\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$distDir = Join-Path $root 'dist'

# Stop any running instance so build can write the exe.
Get-Process PgLootMaster -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# Clean prior publish output so stale files don't tag along.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }

dotnet publish (Join-Path $root 'src\PgLootMaster\PgLootMaster.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Stage the single exe into dist/. Panel templates are baked into the Vision assembly, so
# there's no Templates\ folder to ship separately.
New-Item -ItemType Directory -Path $distDir | Out-Null
Copy-Item (Join-Path $publishDir 'PgLootMaster.exe') $distDir

$exe = Join-Path $distDir 'PgLootMaster.exe'
$size = (Get-Item $exe).Length / 1MB
Write-Output ""
Write-Output ("Built: {0} ({1:N0} MB)" -f $exe, $size)

if ($Release) {
    Write-Output ""
    Write-Output "Creating GitHub release $Release ..."

    # Verify gh CLI is present + authed.
    $ghVersion = & gh --version 2>$null
    if ($LASTEXITCODE -ne 0) { throw "gh CLI not found; install from https://cli.github.com" }

    $releaseArgs = @(
        'release', 'create', $Release,
        $exe,
        '--title', $Release,
        '--latest'
    )
    if ($Notes) {
        $releaseArgs += '--notes'
        $releaseArgs += $Notes
    } else {
        $releaseArgs += '--generate-notes'
    }

    & gh @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

    Write-Output ""
    Write-Output "Release $Release published. Visit:"
    Write-Output "  https://github.com/suceava/pg-loot-master/releases/tag/$Release"
}
