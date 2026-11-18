# Packs the built program (build\Publish.ps1's dist\RockPreview.exe) into the Microsoft Store's package:
#   dist\RockPreview-Store.msix   - uploaded in Partner Center; the Store signs it and installs, updates and removes it
# The identity is the app's in Partner Center (Rock Preview, Store ID 9NCCSZW9VVK1 › Product identity). Unsigned, the
# package installs only through the Store, or for trying the Store mode on a PC in Developer Mode
# (Add-AppxPackage -Register dist\store\layout\AppxManifest.xml).
# Usage:  powershell -ExecutionPolicy Bypass -File build\Package-Store.ps1 [-IdentityName ...] [-Publisher "CN=..."] [-PublisherDisplayName ...]
param(
    [string]$IdentityName = "RockySharaf.RockPreview",
    [string]$Publisher = "CN=FEB78D64-CABE-490A-BB8B-64B5A36E7F51",
    [string]$PublisherDisplayName = "Rocky Sharaf",
    [string]$Exe = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
if (-not $Exe) { $Exe = Join-Path $dist "RockPreview.exe" }
if (-not $Out) { $Out = Join-Path $dist "RockPreview-Store.msix" }
if (-not (Test-Path $Exe)) { throw "$Exe isn't there: run build\Publish.ps1 first." }

# The Store's version is the release's with a 0 at the end (the Store keeps the fourth number for itself).
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1) + ".0"

# The newest Windows SDK's tools.
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" | Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $sdk) { throw "The Windows SDK (makeappx.exe) isn't installed." }
$makeappx = $sdk.FullName
$makepri = Join-Path $sdk.DirectoryName "makepri.exe"

# The package's folder: the program, its logos and its manifest.
$work = Join-Path $dist "store"
$layout = Join-Path $work "layout"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $layout "Assets") | Out-Null
Copy-Item $Exe (Join-Path $layout "RockPreview.exe")
Copy-Item (Join-Path $PSScriptRoot "Store\Assets\*.png") (Join-Path $layout "Assets")
$manifest = (Get-Content (Join-Path $PSScriptRoot "Store\AppxManifest.xml") -Raw).
    Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('$Version$', $version)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))

# resources.pri: Windows picks each logo's size (scale-200, targetsize-24...) from it.
$config = Join-Path $work "priconfig.xml"
& $makepri createconfig /cf $config /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed ($LASTEXITCODE)" }
# One package, so every size goes in its one resources.pri: the config's <packaging> would split them into separate
# files that only a bundle's resource packages use (leaving one size of each logo).
[xml]$pri = Get-Content $config
$pri.resources.RemoveChild($pri.resources.packaging) | Out-Null
$pri.Save($config)
& $makepri new /pr $layout /cf $config /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed ($LASTEXITCODE)" }

& $makeappx pack /d $layout /p $Out /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)" }
Get-Item $Out | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }, @{ n = "Version"; e = { $version } }, @{ n = "Identity"; e = { $IdentityName } } -AutoSize
