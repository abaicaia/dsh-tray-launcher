# package.ps1 - assemble release folder + zip for community distribution.
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = (Get-Location).Path }
$version = '1.0.0'
$stage = Join-Path $scriptDir ('release\DSHLauncher-v' + $version)
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$fromRoot = @('DshLauncher.exe','DshLauncher.cs','AssemblyInfo.cs','build.cmd','build-launcher.ps1','pwa-logo.ico','dsh-launcher.ico')
$fromDocs = @('README.md','LOGS.md','CHANGELOG.md','LICENSE','install.cmd','install.ps1')
foreach ($f in $fromRoot) {
    $src = Join-Path $scriptDir $f
    if (-not (Test-Path $src)) { throw ('missing package file: ' + $f) }
    Copy-Item -LiteralPath $src -Destination $stage -Force
}
foreach ($f in $fromDocs) {
    $src = Join-Path $scriptDir ('package-src\' + $f)
    if (-not (Test-Path $src)) { throw ('missing doc file: ' + $f) }
    Copy-Item -LiteralPath $src -Destination $stage -Force
}
$zip = Join-Path $scriptDir ('release\DSHLauncher-v' + $version + '.zip')
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host 'Package OK:'
Write-Host ('  ' + $stage)
Write-Host ('  ' + $zip + '  (' + (Get-Item $zip).Length + ' bytes)')
if (-not $NoPause) { Read-Host 'Press Enter to close' }
