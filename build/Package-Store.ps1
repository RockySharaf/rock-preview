# Packs the built program (build\Publish.ps1's dist\publish-store-win-x64: RockPreview.exe and the native libraries
# beside it, which it doesn't unpack into %TEMP% as dist\RockPreview.exe does) into the Microsoft Store's package:
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
if (-not $Exe) { $Exe = Join-Path $dist "publish-store-win-x64\RockPreview.exe" }
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
# The native libraries beside it: in the package's folder, which only administrators can change, they're loaded from
# there, never unpacked into the user's %TEMP%, where the elevated instant-indexing helper could pick up a planted one.
$natives = @(Get-ChildItem (Split-Path $Exe -Parent) -Filter *.dll -File)
foreach ($dll in $natives) { Copy-Item $dll.FullName (Join-Path $layout $dll.Name) }
Copy-Item (Join-Path $PSScriptRoot "Store\Assets\*.png") (Join-Path $layout "Assets")
# Read as UTF-8 in Windows PowerShell too: it takes a file without a BOM as ANSI, and the comments' arrows came out garbled.
$manifest = (Get-Content (Join-Path $PSScriptRoot "Store\AppxManifest.xml") -Raw -Encoding UTF8).
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

# What MakeAppx doesn't check, so a package Partner Center would refuse (or that shows blank logos) fails here instead.
$problems = @()
$text = Get-Content (Join-Path $layout "AppxManifest.xml") -Raw -Encoding UTF8
if ($text -match '\$[A-Za-z]+\$') { $problems += "the manifest still has $($Matches[0]) in it" }
# A restricted capability needs Microsoft's approval: runFullTrust is the one the app needs. allowElevation was refused on
# 2026-11-24, and nothing else may come back by accident.
[xml]$doc = $text
$restricted = @($doc.SelectNodes("//*[local-name()='Capability' and starts-with(namespace-uri(), 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')]") |
    ForEach-Object { $_.GetAttribute("Name") } | Where-Object { $_ -ne "runFullTrust" })
if ($restricted) { $problems += "the manifest declares the restricted capability $($restricted -join ', '): only runFullTrust may be there (Microsoft refused allowElevation)" }
$bytes = [IO.File]::ReadAllBytes((Join-Path $layout "RockPreview.exe"))
$machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
if ($machine -ne 0x8664) { $problems += ("RockPreview.exe is for machine 0x{0:X4}, but the manifest says x64" -f $machine) }
$product = (Get-Item (Join-Path $layout "RockPreview.exe")).VersionInfo.ProductVersion
if ($product -ne $version.Substring(0, $version.LastIndexOf('.'))) { $problems += "RockPreview.exe is version $product, the package $version" }
# Not the single file that unpacks its native libraries into %TEMP% at each start (dist\RockPreview.exe, the GitHub copy).
$selfExtracting = Join-Path $dist "RockPreview.exe"
if ((Test-Path $selfExtracting) -and (Get-FileHash $selfExtracting).Hash -eq (Get-FileHash (Join-Path $layout "RockPreview.exe")).Hash) {
    $problems += "RockPreview.exe is the GitHub copy, which unpacks its native libraries into %TEMP%: pack dist\publish-store-win-x64's (build\Publish.ps1)"
}
$dump = Join-Path $work "resources.xml"
& $makepri dump /if (Join-Path $layout "resources.pri") /of $dump /o | Out-Null
[xml]$pri = Get-Content $dump
foreach ($logo in 'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png', 'Wide310x150Logo.png') {
    $candidates = @($pri.SelectNodes("//NamedResource[@name='$logo']/Candidate"))
    if ($candidates.Count -eq 0) { $problems += "resources.pri has no $logo" }
    foreach ($c in $candidates) { if (-not (Test-Path (Join-Path $layout $c.Value))) { $problems += "resources.pri points at a missing $($c.Value)" } }
}
if ($problems) { throw "The Store package isn't right:`n  " + ($problems -join "`n  ") }
Write-Host "Checked: identity $IdentityName, version $version, x64, runFullTrust the only restricted capability, every logo size present, $($natives.Count) native libraries beside the program (Windows SDK $($sdk.Directory.Parent.Name))."
Get-Item $Out | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }, @{ n = "Version"; e = { $version } }, @{ n = "Identity"; e = { $IdentityName } } -AutoSize
