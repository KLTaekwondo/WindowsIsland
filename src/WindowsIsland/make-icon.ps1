Add-Type -AssemblyName System.Drawing
$out = 'D:\Projects\WindowsIsland\WindowsIsland\app.ico'
$n = 32

$bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$c1 = [System.Drawing.Color]::FromArgb(255, 42, 42, 52)
$c2 = [System.Drawing.Color]::FromArgb(255, 12, 12, 16)
$outer = New-Object System.Drawing.RectangleF(2, 9, 28, 14)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(2, 9, 14, 14, 180, 90)
$path.AddArc(16, 9, 14, 14, 270, 90)
$path.AddArc(16, 9, 14, 14, 0, 90)
$path.AddArc(2, 9, 14, 14, 90, 90)
$path.CloseFigure()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($outer, $c1, $c2, [single]90)
$g.FillPath($brush, $path)

$inner = New-Object System.Drawing.Drawing2D.GraphicsPath
$inner.AddArc(9, 14.5, 3, 3, 180, 90)
$inner.AddArc(17, 14.5, 3, 3, 270, 90)
$inner.AddArc(17, 14.5, 3, 3, 0, 90)
$inner.AddArc(9, 14.5, 3, 3, 90, 90)
$inner.CloseFigure()
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
$g.FillPath($white, $inner)

$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 87, 227, 155))
$g.FillEllipse($accent, [single]21.5, [single]14, [single]4, [single]4)
$g.Dispose()

$rect = New-Object System.Drawing.Rectangle(0, 0, $n, $n)
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$pixels = New-Object byte[] ($stride * $n)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
$bmp.UnlockBits($data)
$bmp.Dispose()

$xorSize = $n * $n * 4
$andRow = 4                      # 32 位宽的 1bpp 行，按 4 字节对齐
$andSize = $andRow * $n
$imageSize = 40 + $xorSize + $andSize

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]$n); $bw.Write([byte]$n); $bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$imageSize); $bw.Write([uint32]22)

$bw.Write([uint32]40); $bw.Write([int32]$n); $bw.Write([int32]($n * 2))
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]0); $bw.Write([uint32]$xorSize)
$bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)

for ($y = $n - 1; $y -ge 0; $y--) {
    $bw.Write($pixels, ($y * $stride), ($n * 4))
}

$mask = New-Object byte[] $andSize
$bw.Write($mask)
$bw.Flush()

[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
"written $out : $((Get-Item $out).Length) bytes"

$ico = New-Object System.Drawing.Icon($out)
"read back: $($ico.Width)x$($ico.Height)"
$check = $ico.ToBitmap()
foreach ($pt in @(@(16, 16), @(23, 16), @(4, 16), @(16, 3))) {
    $c = $check.GetPixel($pt[0], $pt[1])
    "  ($($pt[0]),$($pt[1])) A=$($c.A) R=$($c.R) G=$($c.G) B=$($c.B)"
}
$check.Dispose(); $ico.Dispose()
