# Builds Rock Preview as one self-contained file (no .NET install needed on the target PC) and lays out:
#   dist\RockPreview-Setup.exe   - the installer: Start menu shortcut, Settings > Apps entry, options
#   dist\RockPreview.exe         - the portable copy: runs from anywhere (USB stick, any folder)
# Both are the same program; named "...Setup.exe" it installs itself.
# Usage:  powershell -ExecutionPolicy Bypass -File build\Publish.ps1 [-Runtime win-x64|win-arm64]
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$out = Join-Path $dist "publish-$Runtime"

dotnet publish (Join-Path $root "src\Anything\Anything.csproj") -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$suffix = if ($Runtime -eq "win-x64") { "" } else { "-" + ($Runtime -replace '^win-', '') }
Copy-Item (Join-Path $out "RockPreview.exe") (Join-Path $dist "RockPreview-Setup$suffix.exe") -Force
Copy-Item (Join-Path $out "RockPreview.exe") (Join-Path $dist "RockPreview$suffix.exe") -Force
Get-ChildItem $dist -Filter "RockPreview*.exe" | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
