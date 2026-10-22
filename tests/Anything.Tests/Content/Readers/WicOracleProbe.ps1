# Writes the pictures WicOracleProbe.cs reads (set RP_WIC_PICTURES to the folder): powershell -File WicOracleProbe.ps1 <folder>
param([string]$Folder)
# Writes real pictures with Windows' own encoders (WIC through WPF), with metadata set through BitmapMetadata, so the
# picture readers can be checked against an independent writer.
Add-Type -AssemblyName PresentationCore, WindowsBase
New-Item -ItemType Directory -Force $Folder | Out-Null
$ErrorActionPreference = 'Stop'

function Source($w, $h, $dpi, $format) {
    $bpp = $format.BitsPerPixel
    $stride = [int](($w * $bpp + 7) / 8)
    $pixels = New-Object byte[] ($stride * $h)
    (New-Object Random 7).NextBytes($pixels)
    $palette = $null
    if ($format.BitsPerPixel -le 8 -and $format -ne [Windows.Media.PixelFormats]::Gray8) { $palette = [Windows.Media.Imaging.BitmapPalettes]::WebPalette }
    return [Windows.Media.Imaging.BitmapSource]::Create($w, $h, $dpi, $dpi, $format, $palette, $pixels, $stride)
}

function Save($encoder, $source, $meta, $name) {
    $frame = [Windows.Media.Imaging.BitmapFrame]::Create($source, $null, $meta, $null)
    $encoder.Frames.Add($frame)
    $fs = [IO.File]::Create((Join-Path $Folder $name))
    $encoder.Save($fs)
    $fs.Close()
}

function Keywords([string[]]$k) { return New-Object 'System.Collections.ObjectModel.ReadOnlyCollection[string]' (,[string[]]$k) }

# JPEG: camera, title, keywords, date taken, orientation, GPS.
$m = New-Object Windows.Media.Imaging.BitmapMetadata("jpg")
$m.CameraManufacturer = "WicMaker"
$m.CameraModel = "WicModel 9"
$m.Title = "Wic jpeg title"
$m.Keywords = Keywords @("harbour", "evening")
$m.DateTaken = "2023-07-14T15:30:12"
$m.SetQuery("/app1/ifd/{ushort=274}", [uint16]6)
$m.SetQuery("/app1/ifd/gps/{ushort=1}", "S")
$m.SetQuery("/app1/ifd/gps/{ushort=2}", [uint64[]]@((([uint64]1 -shl 32) + 33), (([uint64]1 -shl 32) + 51), (([uint64]100 -shl 32) + 3000)))
$m.SetQuery("/app1/ifd/gps/{ushort=3}", "E")
$m.SetQuery("/app1/ifd/gps/{ushort=4}", [uint64[]]@((([uint64]1 -shl 32) + 151), (([uint64]1 -shl 32) + 12), (([uint64]1 -shl 32) + 0)))
Save (New-Object Windows.Media.Imaging.JpegBitmapEncoder) (Source 640 480 300 ([Windows.Media.PixelFormats]::Bgr24)) $m "wic.jpg"

# CMYK TIFF at 300 dpi with a title.
$t = New-Object Windows.Media.Imaging.BitmapMetadata("tiff")
$t.Title = "Wic tiff title"
Save (New-Object Windows.Media.Imaging.TiffBitmapEncoder) (Source 300 200 300 ([Windows.Media.PixelFormats]::Cmyk32)) $t "wic-cmyk.tif"
Save (New-Object Windows.Media.Imaging.TiffBitmapEncoder) (Source 120 90 150 ([Windows.Media.PixelFormats]::Gray8)) $null "wic-gray.tif"

# PNG at 96 dpi with a title in tEXt, GIF, BMP at 72 dpi with an 8-bit palette.
$p = New-Object Windows.Media.Imaging.BitmapMetadata("png")
$p.SetQuery("/tEXt/{str=Title}", "Wic png title")
Save (New-Object Windows.Media.Imaging.PngBitmapEncoder) (Source 333 222 96 ([Windows.Media.PixelFormats]::Bgra32)) $p "wic.png"
Save (New-Object Windows.Media.Imaging.GifBitmapEncoder) (Source 160 120 96 ([Windows.Media.PixelFormats]::Indexed8)) $null "wic.gif"
Save (New-Object Windows.Media.Imaging.BmpBitmapEncoder) (Source 200 100 72 ([Windows.Media.PixelFormats]::Bgr24)) $null "wic.bmp"
# Title and Comments as Explorer's Details tab writes them (System.Title, System.Comment), and a camera's own
# ImageDescription with a comment added in Explorer.
$e = New-Object Windows.Media.Imaging.BitmapMetadata("jpg")
$e.Title = "Beach day"
$e.Comment = "with Anna"
Save (New-Object Windows.Media.Imaging.JpegBitmapEncoder) (Source 16 16 96 ([Windows.Media.PixelFormats]::Bgr24)) $e "explorer.jpg"
$c = New-Object Windows.Media.Imaging.BitmapMetadata("jpg")
$c.SetQuery("/app1/ifd/{ushort=270}", "OLYMPUS DIGITAL CAMERA")
$c.Comment = "with Anna"
Save (New-Object Windows.Media.Imaging.JpegBitmapEncoder) (Source 16 16 96 ([Windows.Media.PixelFormats]::Bgr24)) $c "camera.jpg"
"written"
