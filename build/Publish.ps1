# Builds Rock Preview as one self-contained file (no .NET install needed on the target PC) and lays out:
#   dist\RockPreview-Setup.exe   - the installer: Start menu shortcut, Settings > Apps entry, options
#   dist\RockPreview.exe         - the portable copy: runs from anywhere (USB stick, any folder)
#   dist\publish-store-win-x64\  - the Microsoft Store's copy, which build\Package-Store.ps1 packs (x64 only)
# All are the same program; named "...Setup.exe" it installs itself.
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

# The Store's copy keeps the few native libraries the runtime brings beside the program, in the package's folder that
# only administrators can change, instead of unpacking them into the user's %TEMP% at each start (which the single file
# above does). Its instant-indexing helper runs elevated: nothing it loads may come from a folder a program at the
# user's normal level could have put a file in.
if ($Runtime -eq "win-x64") {
    $store = Join-Path $dist "publish-store-$Runtime"
    if (Test-Path $store) { Remove-Item $store -Recurse -Force }
    dotnet publish (Join-Path $root "src\Anything\Anything.csproj") -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $store -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for the Store failed ($LASTEXITCODE)" }
}

$suffix = if ($Runtime -eq "win-x64") { "" } else { "-" + ($Runtime -replace '^win-', '') }
Copy-Item (Join-Path $out "RockPreview.exe") (Join-Path $dist "RockPreview-Setup$suffix.exe") -Force
Copy-Item (Join-Path $out "RockPreview.exe") (Join-Path $dist "RockPreview$suffix.exe") -Force
Get-ChildItem $dist -Filter "RockPreview*.exe" | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
