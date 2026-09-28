# ask-xiaod.ps1 —— 「叫小D」急救入口（双击 ask-xiaod.cmd 运行）
#
# 用途：DSH 界面打不开 / 启动报错 / 插件挂掉，老板没法在网页里说话时，
#       用这个入口把现场交给一个**独立的一次性 agent**（dsh headless）去诊断与修复。
#
# 为什么这样设计：
#   1. headless 是独立 profile（profiles/headless），**不吃 web profile 的插件树** →
#      网页挂掉时它照样能起来。
#   2. 沙箱：本机 windows-acl 沙箱后端起不来（实测三种 TEMP 组合全失败，见 2026-09-28 教训），
#      所以这里显式设 DSH_PERMISSION_MODE=danger-full-access（= 本会话同款策略，无沙箱、无审批）。
#   3. 它会把现场证据（启动器日志 / stderr / 端口状态 / dump-config）一并喂给 agent，
#      老板只需要一句话描述现象。
#
# 输出：控制台 + J:\2\gonzuo\archive\叫小D-<时间戳>.md

[CmdletBinding()]
param([string]$Task = '')

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$CoreBin   = 'C:\Users\wan\.dsh\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js'
$Archive   = 'J:\2\gonzuo\archive'
$LogDir    = 'J:\2\gonzuo\启动器\logs'
$MemoryDir = 'C:\Users\wan\.memory'
$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile   = Join-Path $Archive "叫小D-$stamp.md"

function Say([string]$Text, [string]$Color = 'Gray') { Write-Host $Text -ForegroundColor $Color }

function Get-Evidence {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine("### 采集时间 $stamp")
  [void]$sb.AppendLine('')

  # 1) 端口与实例
  $conn = Get-NetTCPConnection -State Listen -LocalPort 3080 -ErrorAction SilentlyContinue
  if ($conn) {
    $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    [void]$sb.AppendLine("- 端口 3080: 监听中，PID $($conn.OwningProcess)，进程启动于 $($proc.StartTime)")
  } else {
    [void]$sb.AppendLine('- 端口 3080: **没有监听**（实例没起来或已退出）')
  }

  # 2) 启动器日志尾部（谁在托管、怎么起的、有没有报错）
  $launcherLog = Join-Path $LogDir 'launcher.log'
  if (Test-Path -LiteralPath $launcherLog) {
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### 启动器日志（末尾 25 行）')
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine(((Get-Content -LiteralPath $launcherLog -Encoding UTF8 -Tail 25) -join "`n"))
    [void]$sb.AppendLine('```')
  }

  # 3) 实例 stderr（"did not activate" 这类启动失败就写在这里）
  foreach ($name in @('dsh-web-3080.stderr.log', 'dsh-web-3080.stderr.log.prev')) {
    $p = Join-Path $LogDir $name
    if (Test-Path -LiteralPath $p) {
      $body = (Get-Content -LiteralPath $p -Encoding UTF8 -Tail 30) -join "`n"
      [void]$sb.AppendLine('')
      [void]$sb.AppendLine("### 实例 stderr: $name")
      [void]$sb.AppendLine('```')
      [void]$sb.AppendLine($body)
      [void]$sb.AppendLine('```')
    }
  }

  # 4) 实例 stdout（token 打码；只用来判断"实例有没有起来"）
  $stdout = Join-Path $LogDir 'dsh-web-3080.stdout.log'
  if (Test-Path -LiteralPath $stdout) {
    $body = ((Get-Content -LiteralPath $stdout -Encoding UTF8) -join "`n") -replace 'token=[^&\s]+', 'token=<masked>'
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('### 实例 stdout（token 已打码）')
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine($body)
    [void]$sb.AppendLine('```')
  }

  # 5) 离线体检：profile 组合树能不能加载（零副作用，实测过）
  if (Test-Path -LiteralPath $CoreBin) {
    $prev = [Console]::OutputEncoding
    try {
      [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
      $dump = (& node $CoreBin --profile web --dump-config 2>&1 | Out-String)
      $rc = $LASTEXITCODE
    } finally { [Console]::OutputEncoding = $prev }
    $bad = ($dump -split "`n" | Where-Object { $_ -match 'error|Error|failed' } | Select-Object -First 15) -join "`n"
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("### --dump-config --profile web : exit=$rc，输出 $((($dump -split "`n").Count)) 行")
    if ($bad) { [void]$sb.AppendLine('```'); [void]$sb.AppendLine($bad); [void]$sb.AppendLine('```') }
    else { [void]$sb.AppendLine('（没有 error/failed 字样 = 组合树本身能加载）') }
  }

  return $sb.ToString()
}

Say ''
Say '==============================================' 'Cyan'
Say ' 叫小D —— 把 DSH 的现场交给小D去修（独立通道）' 'Cyan'
Say '==============================================' 'Cyan'
Say ''
Say '网页打不开也没关系：这里是命令行入口，不依赖网页。' 'Gray'
Say '直接回车 = 让小D自己看日志自动诊断；也可以先打一句话描述现象再回车。' 'Gray'
Say ''

if (-not $Task) {
  $Task = Read-Host '现象描述（可留空）'
}
if (-not $Task) { $Task = '（老板没描述现象，请自己从现场证据里判断 DSH 出了什么问题）' }

Say ''
Say '正在采集现场证据 ...' 'DarkGray'
$evidence = Get-Evidence
Say '证据采集完成，正在叫小D（一次性 agent，通常 1-3 分钟）...' 'DarkGray'
Say ''

$preamble = @"
你是小D（DSH 的长期 agent）。现在通过 headless 一次性通道被唤醒：老板的 DSH 界面出问题了，
他没法在网页里跟你说话，所以走了这个急救入口。

第一步，先用 read 工具恢复人格与知识：
- $MemoryDir\SOUL.md
- $MemoryDir\MEMORY.md
- $MemoryDir\index.md
- $MemoryDir\skills\dsh-plugin-updates.md

铁律（老板 2026-08-25 定）：不擅自删东西；大动作先请示；不碰敏感凭证。
本通道没有交互审批 —— 所以：只做**可回滚**的修复（改前备份），
凡是"停/重启生产实例""删东西""升级/降级核心"这类动作，**不要自己动手**，
在结论里写清"需要老板做什么"。

老板的现象描述：
$Task

自动采集的现场证据：
$evidence

请给出：
1) 结论：故障点是什么（带证据行）。
2) 能安全修的，就修完并验证（说清改了什么、备份在哪）。
3) 最后给老板一段中文总结：现在能不能用 / 还需要他做什么。不要用 markdown 表格。
"@

$taskFile = Join-Path $env:TEMP "ask-xiaod-task-$stamp.txt"
[IO.File]::WriteAllText($taskFile, $preamble, (New-Object System.Text.UTF8Encoding($false)))

$env:DSH_PERMISSION_MODE = 'danger-full-access'
# 用 cmd 的文件重定向跑 node：任务文件字节（UTF-8）直接进 stdin、node 的 stdout/stderr 直接落文件，
# 全程不经过 PowerShell 的管道编码（PS 5.1 的 $OutputEncoding 默认不是 UTF-8，会把中文喂坏 —— 2026-09-28 实测踩过）。
$outTmp = Join-Path $env:TEMP "ask-xiaod-out-$stamp.txt"
$errTmp = Join-Path $env:TEMP "ask-xiaod-err-$stamp.txt"
$cmdLine = 'node "' + $CoreBin + '" --profile headless - < "' + $taskFile + '" > "' + $outTmp + '" 2> "' + $errTmp + '"'
& cmd.exe /c $cmdLine | Out-Null
$stdout = ''
$stderr = ''
if (Test-Path -LiteralPath $outTmp) { $stdout = ([IO.File]::ReadAllText($outTmp, [Text.Encoding]::UTF8)).Trim() }
if (Test-Path -LiteralPath $errTmp) { $stderr = ([IO.File]::ReadAllText($errTmp, [Text.Encoding]::UTF8)).Trim() }
if (-not $stdout) { $stdout = '（headless 没有输出，见下面的诊断）' }
$answer = $stdout
if ($stderr) { $answer = $answer + "`r`n`r`n---- 诊断输出（stderr，含 agent 思考过程） ----`r`n" + $stderr }
[IO.File]::WriteAllText($outFile, $answer, (New-Object System.Text.UTF8Encoding($false)))

Say '---- 小D 的回答 ----' 'Green'
Say $answer
Say '--------------------' 'Green'
Say ''
Say "完整记录已存: $outFile" 'Cyan'
Say '（把上面这段直接说给网页里的小D听也行；他下次会话还能读这个文件）' 'DarkGray'
Say ''

# 修完常要重启才生效（客户端清单、插件模块都是 boot 时定的）。
# 走启动器 --restart：保持"只有启动器一个托管方"，它会清理旧进程树、拉起新实例、并打开界面。
$launcher = 'J:\2\gonzuo\启动器\DshLauncher.exe'
if (Test-Path -LiteralPath $launcher) {
  Say ''
  $ans = Read-Host '要不要现在重启 DSH 让修改生效？(Y/N)'
  if ($ans -match '^[Yy]') {
    Say '正在重启 DSH（经启动器，约 10-30 秒）...' 'DarkGray'
    & $launcher --restart | Out-Null
    Start-Sleep -Seconds 2
    $tok = $null
    $stdoutLog = Join-Path $LogDir 'dsh-web-3080.stdout.log'
    if (Test-Path -LiteralPath $stdoutLog) {
      $m = Select-String -LiteralPath $stdoutLog -Pattern 'token=([^\s]+)' | Select-Object -Last 1
      if ($m) { $tok = $m.Matches[0].Groups[1].Value }
    }
    $up = $false
    for ($i = 0; $i -lt 30; $i++) {
      try { $null = Invoke-WebRequest -Uri 'http://127.0.0.1:3080/' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop; $up = $true; break }
      catch { if ($_.Exception.Response.StatusCode.value__ -eq 401) { $up = $true; break } }
      Start-Sleep -Seconds 2
    }
    if ($up) {
      Say '重启完成，网页已回来。' 'Green'
      if ($tok) { Say ("带 token 的地址: http://127.0.0.1:3080/?token=" + $tok) 'Cyan' }
    } else {
      Say '等不到 3080 就绪 —— 别关这个窗口，把上面「小D 的回答」发给老板看，或再跑一次本入口。' 'Yellow'
    }
  } else {
    Say '没重启。改动会在你下次重启 DSH 时生效。' 'DarkGray'
  }
}
Say ''
