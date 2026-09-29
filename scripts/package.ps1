# package.ps1 - assemble release folder + zip for community distribution.
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = (Get-Location).Path }
# v1.2 目录分类（src / assets / scripts），并兼容"脚本与产物同级"的旧布局
$rootDir = Split-Path $scriptDir -Parent
# 自适应布局：开发目录是 scripts\ + src\ + assets\；发布包是扁平放置（全部同级）
if (-not (Test-Path (Join-Path $rootDir 'src'))) { $rootDir = $scriptDir }
$srcDir   = if (Test-Path (Join-Path $rootDir 'src'))    { Join-Path $rootDir 'src' }    else { $rootDir }
$assetDir = if (Test-Path (Join-Path $rootDir 'assets')) { Join-Path $rootDir 'assets' } else { $rootDir }
$version = '1.0.0'
$stage = Join-Path $rootDir ('release\DSHLauncher-v' + $version)
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
# 打包清单改成"完整源路径"，因为文件现在分散在根 / src / assets / scripts
$packList = @(
    (Join-Path $rootDir 'DshLauncher.exe'),
    (Join-Path $srcDir  'DshLauncher.cs'),
    (Join-Path $srcDir  'AssemblyInfo.cs'),
    (Join-Path $srcDir  'LauncherCore.cs'),
    (Join-Path $srcDir  'LauncherConfig.cs'),
    (Join-Path $srcDir  'LauncherLog.cs'),
    (Join-Path $srcDir  'ModeGate.cs'),
    (Join-Path $srcDir  'RescueMode.cs'),
    (Join-Path $rootDir 'build.cmd'),
    (Join-Path $scriptDir 'build-launcher.ps1'),
    (Join-Path $assetDir 'pwa-logo.ico'),
    (Join-Path $assetDir 'dsh-launcher.ico')
)
$fromDocs = @('README.md','LOGS.md','CHANGELOG.md','LICENSE','install.cmd','install.ps1')
foreach ($p in $packList) {
    if (-not (Test-Path $p)) { throw ('missing package file: ' + $p) }
    Copy-Item -LiteralPath $p -Destination $stage -Force
}
foreach ($f in $fromDocs) {
    $src = Join-Path $rootDir ('package-src\' + $f)
    if (-not (Test-Path $src)) { throw ('missing doc file: ' + $f) }
    Copy-Item -LiteralPath $src -Destination $stage -Force
}
$zip = Join-Path $rootDir ('release\DSHLauncher-v' + $version + '.zip')
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host 'Package OK:'
Write-Host ('  ' + $stage)
Write-Host ('  ' + $zip + '  (' + (Get-Item $zip).Length + ' bytes)')
if (-not $NoPause) { Read-Host 'Press Enter to close' }
