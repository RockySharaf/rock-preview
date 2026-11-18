# Makes the Store package's logos (build/Store/Assets) from the app's own icon and logo, so they always match it.
#   build/Store/Make-Assets.ps1
# The app icon sizes (taskbar, Start, title bar) are the icon's own frames, or drawn from its 256 px frame. The tiles and
# the Store logo are the colored logo (src/Anything.Preview/RockPreviewMarkColor.png) centered on a transparent square.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter *.png | Remove-Item

# The icon's PNG frames, by size.
$ico = [IO.File]::ReadAllBytes((Join-Path $root 'src\Anything\RockPreview.ico'))
$frames = @{}
for ($i = 0; $i -lt [BitConverter]::ToUInt16($ico, 4); $i++) {
    $entry = 6 + 16 * $i
    $size = if ($ico[$entry] -eq 0) { 256 } else { [int]$ico[$entry] }
    $length = [BitConverter]::ToInt32($ico, $entry + 8)
    $offset = [BitConverter]::ToInt32($ico, $entry + 12)
    $frames[$size] = [byte[]]$ico[$offset..($offset + $length - 1)]
}
function Frame([int]$size) { [System.Drawing.Image]::FromStream([IO.MemoryStream]::new($frames[$size])) }
$icon = Frame 256
$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'src\Anything.Preview\RockPreviewMarkColor.png'))

# Draws $image into a transparent $width x $height picture, fitted into the middle $share of it.
function Save([System.Drawing.Image]$image, [int]$width, [int]$height, [double]$share, [string]$name) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = [Math]::Min($width * $share / $image.Width, $height * $share / $image.Height)
    $w = $image.Width * $scale; $h = $image.Height * $scale
    $g.DrawImage($image, [System.Drawing.RectangleF]::new(($width - $w) / 2, ($height - $h) / 2, $w, $h))
    $g.Dispose()
    $bitmap.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# The app icon (Square44x44Logo): taskbar and Start sizes, with and without Windows' plate (the icon has no tile).
foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
        $name = "Square44x44Logo.targetsize-$size$suffix.png"
        if ($frames.ContainsKey($size)) { [IO.File]::WriteAllBytes((Join-Path $out $name), $frames[$size]) }
        else { Save $icon $size $size 1.0 $name }
    }
}
foreach ($scale in 100, 125, 150, 200, 400) { $s = [int][Math]::Round(44 * $scale / 100); Save $icon $s $s 1.0 "Square44x44Logo.scale-$scale.png" }

# Start tiles and the Store logo.
foreach ($scale in 100, 125, 150, 200, 400) {
    $f = $scale / 100
    Save $logo ([int](150 * $f)) ([int](150 * $f)) 0.62 "Square150x150Logo.scale-$scale.png"
    Save $logo ([int](310 * $f)) ([int](150 * $f)) 0.62 "Wide310x150Logo.scale-$scale.png"
    Save $icon ([int](50 * $f)) ([int](50 * $f)) 1.0 "StoreLogo.scale-$scale.png"
}
$icon.Dispose(); $logo.Dispose()
Get-ChildItem $out -Filter *.png | Measure-Object | ForEach-Object { "$($_.Count) logos in $out" }
