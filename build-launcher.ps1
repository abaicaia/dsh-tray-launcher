# build-launcher.ps1 - Build DshLauncher.exe (C# tray app) and install desktop shortcuts.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File build-launcher.ps1 [-NoPause]
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = (Get-Location).Path }

$src  = Join-Path $scriptDir 'DshLauncher.cs'
$asminfo = Join-Path $scriptDir 'AssemblyInfo.cs'
$out  = Join-Path $scriptDir 'DshLauncher.exe'
$ico  = Join-Path $scriptDir 'dsh-launcher.ico'

# --- 1. locate csc.exe (.NET Framework 4.x) ---
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x required.' }
Write-Host "[1/5] csc: $csc"

# --- 2. generate launcher icon ---
# preferred: blue circle badge with white DeepSeek whale (from DSH PWA logo);
# fallback: plain blue circle with white D.
Add-Type -AssemblyName System.Drawing
$whaleSource = Join-Path $scriptDir 'pwa-logo.ico'
if (-not (Test-Path $whaleSource)) {
    $whaleSource = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Google\Chrome\User Data\Default\Web Applications\_crx_hgiemfgfjhalibdoboikeiepnnjapnpc\DeepSeek Harness.ico'
}
$useWhale = Test-Path $whaleSource
$badges = @()
if ($useWhale) {
    $srcIcon = New-Object System.Drawing.Icon($whaleSource, 128, 128)
    $whaleBmp = $srcIcon.ToBitmap()
    $srcIcon.Dispose()
    # alpha bounding box of the whale
    $minX=128; $minY=128; $maxX=-1; $maxY=-1
    for ($y=0; $y -lt 128; $y++) {
        for ($x=0; $x -lt 128; $x++) {
            if ($whaleBmp.GetPixel($x,$y).A -gt 32) {
                if ($x -lt $minX) {$minX=$x}; if ($x -gt $maxX) {$maxX=$x}
                if ($y -lt $minY) {$minY=$y}; if ($y -gt $maxY) {$maxY=$y}
            }
        }
    }
    $cw = $maxX - $minX + 1; $ch = $maxY - $minY + 1
    # color matrix: black -> white (invert), alpha preserved
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $cm.Matrix00 = -1; $cm.Matrix11 = -1; $cm.Matrix22 = -1
    $cm.Matrix40 = 1; $cm.Matrix41 = 1; $cm.Matrix42 = 1
    $attrs = New-Object System.Drawing.Imaging.ImageAttributes
    $attrs.SetColorMatrix($cm)
    $badgeBlue = [System.Drawing.Color]::FromArgb(255, 77, 107, 254)  # DeepSeek blue
    function New-WhaleBadge([int]$bsize) {
        $bmp = New-Object System.Drawing.Bitmap $bsize, $bsize
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $b = New-Object System.Drawing.SolidBrush $badgeBlue
        $g.FillEllipse($b, 0, 0, $bsize, $bsize)
        $scale = [math]::Min((0.62 * $bsize) / $cw, (0.62 * $bsize) / $ch)
        $dw = [int]($cw * $scale); $dh = [int]($ch * $scale)
        $dx = [int](($bsize - $dw) / 2); $dy = [int](($bsize - $dh) / 2)
        $dest = New-Object System.Drawing.Rectangle $dx, $dy, $dw, $dh
        $srcRect = New-Object System.Drawing.Rectangle $minX, $minY, $cw, $ch
        $g.DrawImage($whaleBmp, $dest, $srcRect.X, $srcRect.Y, $srcRect.Width, $srcRect.Height, [System.Drawing.GraphicsUnit]::Pixel, $attrs)
        $b.Dispose(); $g.Dispose()
        return ,$bmp
    }
    $badges += New-WhaleBadge 16
    $badges += New-WhaleBadge 32
    Write-Host "[2/5] icon: whale badge (source: $whaleSource)"
}
if ($badges.Count -eq 0) {
    # fallback: blue circle with white D
    $bmp = New-Object System.Drawing.Bitmap 32,32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 59, 130, 246))
    $g.FillEllipse($blue, 0, 0, 32, 32)
    $font = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 4, 2, 24, 28
    $g.DrawString('D', $font, $white, $rect, $fmt)
    $g.Dispose()
    $badges += $bmp
    Write-Host "[2/5] icon: fallback D badge"
}
function Get-XorData($bmp) {
    $s = $bmp.Width
    $xor = New-Object byte[] ($s*$s*4)
    for ($y=0; $y -lt $s; $y++) {
        for ($x=0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $s-1-$y)
            $i = ($y*$s + $x)*4
            $xor[$i] = $c.B; $xor[$i+1] = $c.G; $xor[$i+2] = $c.R; $xor[$i+3] = $c.A
        }
    }
    return ,$xor
}
# write classic multi-entry ICO (csc-compatible)
$entries = @()
foreach ($bmp in $badges) {
    $entries += @{ size=$bmp.Width; xor=(Get-XorData $bmp) }
    $bmp.Dispose()
}
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$offset = 6 + 16*$entries.Count
foreach ($e in $entries) {
    $e.and = New-Object byte[] (($e.size+7)/8*$e.size)
    $e.offset = $offset
    $e.imgSize = 40 + $e.xor.Length + $e.and.Length
    $offset += $e.imgSize
}
foreach ($e in $entries) {
    $w.Write([byte]$e.size); $w.Write([byte]$e.size); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$e.imgSize); $w.Write([uint32]$e.offset)
}
foreach ($e in $entries) {
    $w.Write([uint32]40); $w.Write([int32]$e.size); $w.Write([int32]($e.size*2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]$e.xor.Length); $w.Write([int32]0); $w.Write([int32]0)
    $w.Write([uint32]0); $w.Write([uint32]0)
    $w.Write($e.xor); $w.Write($e.and)
}
$w.Flush()
[IO.File]::WriteAllBytes($ico, $ms.ToArray())
$w.Dispose(); $ms.Dispose()
Write-Host "[2/5] icon: $ico ($((Get-Item $ico).Length) bytes)"

# --- 3. source must be UTF-8 WITH BOM, otherwise csc decodes Chinese as GBK ---
$bytes = [IO.File]::ReadAllBytes($src)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    $bytes = $bytes[3..($bytes.Length - 1)]
}
$bom = [byte[]](0xEF, 0xBB, 0xBF)
$tmpSrc = Join-Path $env:TEMP ('DshLauncher-' + [guid]::NewGuid().ToString('N') + '.cs')
[IO.File]::WriteAllBytes($tmpSrc, $bom + $bytes)
Write-Host "[3/5] source prepared (UTF-8 BOM): $tmpSrc"

# --- 4. compile ---
& $csc /nologo /target:winexe /optimize+ "/win32icon:$ico" "/out:$out" "$tmpSrc" "$asminfo" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Remove-Item $tmpSrc -ErrorAction SilentlyContinue
Write-Host "[4/5] compiled: $out ($((Get-Item $out).Length) bytes)"

# --- 5. install / repoint desktop shortcuts ---
$desktop = [Environment]::GetFolderPath('Desktop')
$sh = New-Object -ComObject WScript.Shell
$pwaIcon = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Google\Chrome\User Data\Default\Web Applications\_crx_hgiemfgfjhalibdoboikeiepnnjapnpc\DeepSeek Harness.ico'
$mainIcon = $out
if (Test-Path $pwaIcon) { $mainIcon = $pwaIcon }

# only the main "DeepSeek Harness" shortcut is installed; stray launcher
# shortcuts (old 启动/停止 DSH) are removed so rebuilds never resurrect them
$mainLnk = Join-Path $desktop 'DeepSeek Harness.lnk'
$lnk = $sh.CreateShortcut($mainLnk)
$lnk.TargetPath = $out
$lnk.Arguments = '--open'
$lnk.WorkingDirectory = $scriptDir
$lnk.IconLocation = "$mainIcon,0"
$lnk.Save()
Write-Host "[5/5] shortcut: $mainLnk  ->  $out --open"

Get-ChildItem -LiteralPath $desktop -Filter *.lnk | ForEach-Object {
    $s = $sh.CreateShortcut($_.FullName)
    if ($s.TargetPath -like '*DshLauncher.exe' -and $_.FullName -ne $mainLnk) {
        Remove-Item -LiteralPath $_.FullName -Force
        Write-Host "      cleaned stray shortcut: $($_.Name)"
    }
}

Write-Host ''
Write-Host 'Build OK. Double-click desktop "DeepSeek Harness" to launch.'
$logDir = Join-Path $scriptDir 'logs'
Write-Host "Logs: $logDir"

if (-not $NoPause) { Read-Host 'Press Enter to close' }