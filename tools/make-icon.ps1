# 生成 assets\icon.ico（多尺寸，PNG 压缩条目，GDI+ 绘制）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root 'assets\icon.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [math]::Max(1, [int]($size * 0.04))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $r = [math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 43, 127, 255),
        [System.Drawing.Color]::FromArgb(255, 12, 78, 190),
        90.0)
    $g.FillPath($brush, $path)

    $penW = [math]::Max(1.0, $size * 0.085)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inner = [int]($size * 0.27)
    $len = [int]($size * 0.17)
    $g.DrawLine($pen, $inner, $inner, $inner + $len, $inner)
    $g.DrawLine($pen, $inner, $inner, $inner, $inner + $len)
    $g.DrawLine($pen, $size - $inner, $inner, $size - $inner - $len, $inner)
    $g.DrawLine($pen, $size - $inner, $inner, $size - $inner, $inner + $len)
    $g.DrawLine($pen, $inner, $size - $inner, $inner + $len, $size - $inner)
    $g.DrawLine($pen, $inner, $size - $inner, $inner, $size - $inner - $len)
    $g.DrawLine($pen, $size - $inner, $size - $inner, $size - $inner - $len, $size - $inner)
    $g.DrawLine($pen, $size - $inner, $size - $inner, $size - $inner, $size - $inner - $len)

    if ($size -ge 32) {
        $dotR = [math]::Max(1.0, $size * 0.055)
        $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $g.FillEllipse($dot, ($size / 2 - $dotR), ($size / 2 - $dotR), (2 * $dotR), (2 * $dotR))
        $dot.Dispose()
    }

    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , ($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$blobs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Output ("OK  {0}  ({1} bytes)" -f $outPath, (Get-Item $outPath).Length)
