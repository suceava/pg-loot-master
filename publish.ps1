# Build PgLootMaster as a self-contained single-file Windows executable.
# Output:
#   .\dist\PgLootMaster.exe
#   .\dist\Templates\*.png
#
# Distributable: zip the entire dist\ folder.

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
