# Build PgLootMaster as a self-contained single-file Windows executable.
#
# Usage:
#   .\publish.ps1                 — build only, output to dist\
#   .\publish.ps1 -Release v1.0.0 — build, then create a GitHub release with the exe attached
#
# Output:
#   .\dist\PgLootMaster.exe
#   .\dist\Templates\*.png
#
# Distributable: zip the entire dist\ folder, or use -Release to upload via gh CLI.

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

# Stage into dist/ (no pdbs).
New-Item -ItemType Directory -Path $distDir | Out-Null
Copy-Item (Join-Path $publishDir 'PgLootMaster.exe') $distDir
Copy-Item (Join-Path $publishDir 'Templates') $distDir -Recurse

$exe = Join-Path $distDir 'PgLootMaster.exe'
$size = (Get-Item $exe).Length / 1MB
Write-Output ""
Write-Output ("Built: {0} ({1:N0} MB)" -f $exe, $size)
Write-Output ("Templates: {0}" -f (Join-Path $distDir 'Templates'))

if ($Release) {
    Write-Output ""
    Write-Output "Creating GitHub release $Release ..."

    # Verify gh CLI is present + authed.
    $ghVersion = & gh --version 2>$null
    if ($LASTEXITCODE -ne 0) { throw "gh CLI not found; install from https://cli.github.com" }

    # Also zip the Templates folder alongside, since the user needs both. Two assets:
    #   PgLootMaster.exe (271 MB) + Templates/ files
    # Easier: zip dist\ as a single asset.
    $zipPath = Join-Path $distDir 'PgLootMaster-windows.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $distDir 'PgLootMaster.exe'), (Join-Path $distDir 'Templates') `
        -DestinationPath $zipPath -CompressionLevel Optimal

    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Output ("Zipped: {0} ({1:N0} MB)" -f $zipPath, $zipSize)

    $releaseArgs = @(
        'release', 'create', $Release,
        $zipPath,
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
