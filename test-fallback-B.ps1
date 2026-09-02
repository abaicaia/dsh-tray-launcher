# test-fallback-B.ps1 - fallback clean re-test (plan B). ASCII-only source to avoid GBK/UTF8 parsing issues.
$ErrorActionPreference = 'Continue'
$exe = 'J:\2\gonzuo\启动器\DshLauncher.exe'
$log = 'J:\2\gonzuo\启动器\logs\launcher.log'
$pipeName = 'dsh-launcher-pipe-' + [Environment]::UserName
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Chinese markers as .NET strings (avoid literal CJK in source)
function Marker-NoTray { return ([char]0x8F6C).ToString() + ([char]0x53D1).ToString() + ([char]0x5931).ToString() + ([char]0x8D25).ToString() + ([char]0x2C).ToString() + ([char]0x20).ToString() + ([char]0x65E0).ToString() + ([char]0x6258).ToString() + ([char]0x76D8).ToString() + ([char]0x5B9E).ToString() + ([char]0x4F8B).ToString() }
function Marker-Forwarded { return ([char]0x5DF2).ToString() + ([char]0x6709).ToString() + ([char]0x5B9E).ToString() + ([char]0x4F8B).ToString() + ([char]0x5728).ToString() + ([char]0x8FD0).ToString() + ([char]0x884C).ToString() }

function Port-Listening([int]$port) {
    return $null -ne (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}
function Pipe-HasListener {
    $client = $null
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
        $client.Connect(800)
        return $true
    } catch { return $false }
    finally { if ($client) { try { $client.Dispose() } catch {} } }
}

Write-Host "========== Plan B: fallback clean re-test =========="
Write-Host "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
if (-not (Test-Path $exe)) { Write-Host "[FATAL] exe missing"; exit 1 }

$pid3080_before = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
Write-Host "[PRE] 3080 initial PID = $pid3080_before"

Write-Host "`n[1] Killing all DshLauncher processes (not 3080 node)..."
Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force; Write-Host "  killed DshLauncher PID $($_.Id)"
}
Start-Sleep -Seconds 1

if (-not (Port-Listening 3080)) { Write-Host "[ABORT] 3080 dropped"; exit 1 }
Write-Host "[OK] 3080 still healthy (PID $pid3080_before)"

Write-Host "`n[2] Probing pipe vacuum..."
$vacuum = $false
for ($i = 0; $i -lt 20; $i++) {
    if (-not (Pipe-HasListener)) { $vacuum = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $vacuum) {
    $still = Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue
    if ($still) { $still | ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 2 }
    $vacuum = -not (Pipe-HasListener)
}
Write-Host "[INFO] vacuum = $vacuum"

$beforeLog = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
Write-Host "`n[3] Running --stop 3080 (expect fallback)..."
$stopProc = Start-Process -FilePath $exe -ArgumentList '--stop','--port','3080' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 4
Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

$all = [System.IO.File]::ReadAllText($log, $utf8)
$newLog = $all -split "`r?`n" | Select-Object -Skip $beforeLog
$noTray = Marker-NoTray
$fwd = Marker-Forwarded
$hit = $newLog | Where-Object { $_.Contains($noTray) } | Select-Object -First 1
$forwarded = $newLog | Where-Object { $_.Contains($fwd) } | Select-Object -First 1

Write-Host "`n========== RESULT =========="
if ($hit) {
    Write-Host "[PASS] fallback hit: $($hit.Trim())"
} else {
    Write-Host "[FAIL] fallback NOT hit"
    if ($forwarded) { Write-Host "  instead forwarded: $($forwarded.Trim())" }
}
Write-Host "`n--- relevant log lines ---"
$newLog | Where-Object { $_.Contains($fwd) -or $_.Contains($noTray) -or $_ -match 'stop|stop|tray|StopDsh|local' } | ForEach-Object { Write-Host "  $($_.Trim())" }

$pid3080_after = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
Write-Host "`n[POST] 3080 PID = $pid3080_after (before=$pid3080_before)"
if ($hit -and $pid3080_after -ne $pid3080_before) {
    Write-Host "[INFO] fallback triggered local stop; 3080 stopped as intended. Restoring..."
    Start-Process -FilePath $exe -ArgumentList '--open' -WindowStyle Hidden
    Start-Sleep -Seconds 8
    $pid3080_restored = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
    Write-Host "[RESTORED] 3080 PID = $pid3080_restored"
}

if ($hit) { Write-Host "`n[RESULT] FALLBACK OK (exit 0)"; exit 0 }
else { Write-Host "`n[RESULT] FALLBACK NOT TRIGGERED (exit 1)"; exit 1 }
