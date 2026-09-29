# test-fallback.ps1 - DSH Launcher v1.1 fallback 端到端测试
# 用途: 验证"无托盘实例时 --stop/--start 本地直接执行(fallback)"修复是否生效。
# 由外部进程(OpenClaw/小白)执行 —— 测试会停/起 3080 托盘, 但绝不杀 3080 的 DSH node。
# 调用: powershell -NoProfile -ExecutionPolicy Bypass -File test-fallback.ps1
# 退出码: 0=全过 1=有失败
# 输出: 每步 PASS/FAIL + 末尾汇总

$ErrorActionPreference = 'Continue'
$exe = 'J:\2\gonzuo\启动器\DshLauncher.exe'
$log = 'J:\2\gonzuo\启动器\logs\launcher.log'
$results = @()   # 记录 PASS/FAIL

function Assert([string]$name, [bool]$cond, [string]$detail) {
    if ($cond) { Write-Host "[PASS] $name" -ForegroundColor Green; $script:results += "PASS|$name" }
    else { Write-Host "[FAIL] $name - $detail" -ForegroundColor Red; $script:results += "FAIL|$name|$detail" }
}

# 小工具: 检查某端口是否有监听
function Port-Listening([int]$port) {
    return $null -ne (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}
# 小工具: 读日志尾部
function Log-Tail([int]$lines) {
    if (Test-Path $log) { return (Get-Content $log -Tail $lines -ErrorAction SilentlyContinue) -join "`n" }
    return "(无日志)"
}

Write-Host "========== DSH Launcher v1.1 fallback 端到端测试 =========="
Write-Host "时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
if (-not (Test-Path $exe)) { Write-Host "[FATAL] exe 不存在: $exe"; exit 1 }

# ---------- 前置: 记录 3080 初始状态 ----------
$pid3080_before = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
Write-Host "`n[前置] 3080 初始 PID: $pid3080_before (测试全程不得改变 = 主会话安全)"
Write-Host "[前置] exe 时间: $((Get-Item $exe).LastWriteTime)"

# ---------- 测试 A: 健康检查回归(有托盘, DSH 在跑, 不应误判重启) ----------
Write-Host "`n---------- 测试 A: 健康检查 ----------"
$trayBefore = Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($trayBefore) {
    Write-Host "托盘在跑 PID=$($trayBefore.Id), 观察 8s 是否被误杀重启"
    $lineBefore = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
    Start-Sleep -Seconds 8
    $pid3080_a = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
    Assert "A1 托盘在跑时 3080 PID 稳定(未被误杀重启)" ($pid3080_a -eq $pid3080_before) "before=$pid3080_before after=$pid3080_a"
    $newLog = Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $lineBefore
    $startFail = $newLog | Where-Object { $_ -match '启动失败|未运行, 清理并自动启动' }
    Assert "A2 无新增'启动失败/清理重启'日志(健康检查不误判)" ($null -eq $startFail) ($startFail -join ' | ')
} else {
    Write-Host "[SKIP] A: 无托盘在跑(先由 C 段拉起)"
}

# ---------- 测试 B: 无托盘 --stop fallback(核心) ----------
# 做法: 停 3080 托盘(不杀 node) -> mutex 释放 -> 起一次性 --start --port 3099 占 mutex
#       -> 立即 --stop --port 3099 触发 !created+转发失败 -> 应走 fallback 本地直停
Write-Host "`n---------- 测试 B: 无托盘 --stop fallback ----------"
Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force; Write-Host "已停托盘 PID $($_.Id) (3080 node 不受影响)" }
Start-Sleep -Seconds 2
Assert "B0 停托盘后 3080 仍健康(测试自保)" (Port-Listening 3080) "3080 掉了说明测试把主会话杀了, 立即中止!"
if (-not (Port-Listening 3080)) { Write-Host "[ABORT] 3080 掉了, 测试失败中止"; exit 1 }

$fallbackHit = $false
for ($i = 1; $i -le 3; $i++) {
    Write-Host "`n--- B 第 $i 次尝试 ---"
    # 清残留并等完全干净(前一次 start/stop 进程必须彻底退出, 否则 mutex/管道互相干扰)
    Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'port 3099' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    # 等所有 DshLauncher 退出 (最多 8s)
    for ($w = 0; $w -lt 8; $w++) {
        if (-not (Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Milliseconds 500
    $beforeLog = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object -Line).Lines

    # 起一次性 --start 3099 占 mutex (后台). 它 created=true 拿 mutex, 进入 StartDsh 就绪轮询(持锁)
    $startProc = Start-Process -FilePath $exe -ArgumentList '--start','--port','3099','--noopen' -WindowStyle Hidden -PassThru

    # 给 2s 让它拿 mutex 并进入就绪等待; 确认进程还活着(= mutex 确实被它占着)
    Start-Sleep -Seconds 2
    $startAlive = Get-Process -Id $startProc.Id -ErrorAction SilentlyContinue
    if (-not $startAlive) {
        Write-Host "  [warn] --start 进程提前退出, 本次跳过"
        Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'port 3099' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        continue
    }

    # 此时 mutex 必被 startProc 占 -> --stop 一定 !created -> 转发失败(SendCommand 带 ACK, 无管道收不到回执)
    # -> 应走 fallback 本地直停. 不再弹窗(已修), --stop 正常退出.
    $stopProc = Start-Process -FilePath $exe -ArgumentList '--stop','--port','3099' -WindowStyle Hidden -PassThru
    try { Wait-Process -Id $stopProc.Id -Timeout 15 -ErrorAction Stop } catch { Write-Host "  [warn] --stop 未在 15s 内退出, 强制清理" }
    Get-Process -Id $stopProc.Id -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1

    # 判定: 日志出现 fallback 标记
    $newLog2 = Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $beforeLog
    $hitLine = $newLog2 | Where-Object { $_ -match '转发失败, 无托盘实例' } | Select-Object -First 1
    if ($hitLine) { $fallbackHit = $true; Write-Host "  命中 fallback 日志: $hitLine" }
    else {
        Write-Host "  未命中. 相关日志:"
        $newLog2 | Where-Object { $_ -match '启动|转发|stop|失败|ACK|收到命令' } | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" }
    }

    # 清理: start 进程 + 3099 node, 等完全退出再进下一轮
    Get-Process -Id $startProc.Id -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'port 3099' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    for ($w = 0; $w -lt 8; $w++) {
        if (-not (Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    if ($fallbackHit) { break }
}
Assert "B1 --stop 无托盘时触发 fallback(本地直停)" $fallbackHit "3 次尝试均未命中, 日志: $(Log-Tail 20)"
Assert "B2 测试后 3099 已清理" (-not (Port-Listening 3099)) "3099 仍被占"

# ---------- 测试 C: 恢复 3080 托盘 ----------
Write-Host "`n---------- 测试 C: 恢复 3080 托盘 ----------"
$restore = Start-Process -FilePath $exe -ArgumentList '--open' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 10
$trayAfter = Get-Process -Name 'DshLauncher' -ErrorAction SilentlyContinue | Select-Object -First 1
Assert "C1 托盘已恢复运行" ($null -ne $trayAfter) "托盘未起来"
$pid3080_after = (Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue).OwningProcess
Assert "C2 3080 PID 全程未变(主会话安全)" ($pid3080_after -eq $pid3080_before) "before=$pid3080_before after=$pid3080_after"
$logTail = Log-Tail 6
Assert "C3 恢复后日志含'DSH 已在运行'(健康检查正常)" ($logTail -match '已在运行') $logTail

# ---------- 汇总 ----------
Write-Host "`n========== 测试结果汇总 =========="
$passCount = ($results | Where-Object { $_ -like 'PASS*' }).Count
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
foreach ($r in $results) { Write-Host "  $r" }
Write-Host "通过: $passCount  失败: $failCount"
if ($failCount -gt 0) { Write-Host "[RESULT] FAIL"; exit 1 }
Write-Host "[RESULT] ALL PASS"; exit 0
