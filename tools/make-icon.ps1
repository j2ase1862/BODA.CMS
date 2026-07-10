# BODA.CMS 앱 아이콘 생성기 — 외부 디자인 도구 없이 재현 가능하도록 System.Drawing 으로 그린다.
# 콘셉트: 협동로봇 팔 + 상태 펄스(심전도) = "로봇 상태 감시(CMS)".
# 산출물:
#   Assets\app.ico                 — exe 임베드·MSI ARP·번들 아이콘 (256/64/48/32/24/16, 32bpp)
#   Assets\app-256.png             — 문서·미리보기용
#   Collector\wwwroot\favicon.ico  — 웹 대시보드 파비콘 (정적 서빙으로 자동 적용)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root "Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 256 좌표계에 그린 뒤 ScaleTransform 으로 각 크기에 내려 그린다 (작은 크기도 AA 로 깨끗함).
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $accent = [System.Drawing.Color]::FromArgb(255, 0x38, 0xE0, 0xD0)   # 청록 — 펄스·관절
    $white  = [System.Drawing.Color]::FromArgb(255, 0xEF, 0xF3, 0xFA)   # 로봇 팔
    $pinCol = [System.Drawing.Color]::FromArgb(255, 0x12, 0x22, 0x3C)   # 관절 핀(배경색 계열)

    # ── 배경: 남색 그라데이션 라운드 사각형 ──
    $bgPath = New-RoundedRectPath 10 10 236 236 54
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 10)), (New-Object System.Drawing.PointF(0, 246)),
        [System.Drawing.Color]::FromArgb(255, 0x25, 0x43, 0x74),
        [System.Drawing.Color]::FromArgb(255, 0x0A, 0x15, 0x24))
    $g.FillPath($bgBrush, $bgPath)
    $hl = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 255, 255, 255), 3)
    $g.DrawPath($hl, $bgPath)

    # ── 상태 펄스(심전도) — 팔보다 먼저 그려 뒤에 깔리게 ──
    $pulsePen = New-Object System.Drawing.Pen($accent, 13)
    $pulsePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pulsePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pulsePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pulsePts = @(
        (New-Object System.Drawing.PointF(116, 170)),
        (New-Object System.Drawing.PointF(142, 170)),
        (New-Object System.Drawing.PointF(158, 136)),
        (New-Object System.Drawing.PointF(178, 206)),
        (New-Object System.Drawing.PointF(194, 170)),
        (New-Object System.Drawing.PointF(226, 170)))
    $g.DrawLines($pulsePen, $pulsePts)

    # ── 로봇 팔: 받침대 + 2절 링크 + 관절 ──
    $armBrush = New-Object System.Drawing.SolidBrush($white)
    $base = New-RoundedRectPath 50 190 68 24 10
    $g.FillPath($armBrush, $base)

    $seg1 = New-Object System.Drawing.Pen($white, 26)
    $seg1.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $seg1.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($seg1, 84, 198, 112, 120)
    $seg2 = New-Object System.Drawing.Pen($white, 21)
    $seg2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $seg2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($seg2, 112, 120, 170, 94)

    $pinBrush = New-Object System.Drawing.SolidBrush($pinCol)
    $g.FillEllipse($pinBrush, 112 - 6.5, 120 - 6.5, 13, 13)

    # 손목 말단: 청록 링 + 흰 점 (상태 표시등 느낌)
    $accBrush = New-Object System.Drawing.SolidBrush($accent)
    $g.FillEllipse($accBrush, 170 - 14, 94 - 14, 28, 28)
    $dotBrush = New-Object System.Drawing.SolidBrush($white)
    $g.FillEllipse($dotBrush, 170 - 5.5, 94 - 5.5, 11, 11)

    $g.Dispose()
    return $bmp
}

# ── ICO 패킹 ──────────────────────────────────────────────────────────────────
# 256 엔트리는 PNG 압축(Vista+ 표준), 그 이하는 호환성을 위해 고전 32bpp DIB(BGRA+AND마스크).
function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskRow = [int]([math]::Ceiling($w / 32.0) * 4)   # AND 마스크 행은 32비트 정렬
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))   # BITMAPINFOHEADER, 높이는 XOR+AND 합
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * 4 * $h + $maskRow * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $data.Stride, $w * 4) }   # 하단부터(bottom-up)
    $bw.Write((New-Object byte[] ($maskRow * $h)))   # 알파를 쓰므로 AND 마스크는 전부 0
    $bw.Flush()
    return $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $entries = foreach ($sz in $sizes) {
        $bmp = New-IconBitmap $sz
        # 함수 반환 때 byte[] 가 Object[] 로 풀리므로 여기서 되돌린다 (BinaryWriter 오버로드 바인딩용)
        $bytes = [byte[]]$(if ($sz -ge 256) { Get-PngBytes $bmp } else { Get-DibBytes $bmp })
        $bmp.Dispose()
        @{ Size = $sz; Bytes = $bytes }
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)   # ICONDIR
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {                                                    # ICONDIRENTRY
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$e.Bytes.Length); $bw.Write([int]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
}

$ico = Join-Path $assets "app.ico"
Write-Ico $ico @(256, 64, 48, 32, 24, 16)

$png = New-IconBitmap 256
$png.Save((Join-Path $assets "app-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

Copy-Item $ico (Join-Path $root "Collector\wwwroot\favicon.ico") -Force

Get-Item $ico, (Join-Path $assets "app-256.png"), (Join-Path $root "Collector\wwwroot\favicon.ico") |
    ForEach-Object { "{0}  ({1:N1} KB)" -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1KB) }
