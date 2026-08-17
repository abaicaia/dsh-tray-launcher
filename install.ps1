# install.ps1 - create desktop shortcut for DSH Launcher (prebuilt exe).
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
if ([string]::IsNullOrEmpty($dir)) { $dir = (Get-Location).Path }
$exe = Join-Path $dir 'DshLauncher.exe'
if (-not (Test-Path $exe)) { throw 'DshLauncher.exe not found in this folder.' }
$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop 'DeepSeek Harness.lnk'
$pwaIcon = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Google\Chrome\User Data\Default\Web Applications\_crx_hgiemfgfjhalibdoboikeiepnnjapnpc\DeepSeek Harness.ico'
if (-not (Test-Path $pwaIcon)) { $pwaIcon = $exe }
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.Arguments = '--open'
$lnk.WorkingDirectory = $dir
$lnk.IconLocation = "$pwaIcon,0"
$lnk.Save()
Write-Host 'OK: desktop shortcut created: ' -NoNewline
Write-Host $lnkPath
Write-Host 'Double-click it to launch. Right-click the tray icon for restart / stop / logs.'
