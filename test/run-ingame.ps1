# Runs the in-game self-test: starts VaM in desktop mode with the self-test build of the plugin,
# waits for it to walk through its steps and quit, then puts everything back.
#
#   pwsh test\run-ingame.ps1 [-VamDir D:\Games\VaM_Updater] [-TimeoutSeconds 900]
#
# Needs: managed\build.ps1 -SelfTest and native\build.ps1 to have been run. Touches, and restores:
# BepInEx\config\uncleburrito.vamdlssnr.cfg and this plugin's own .cfg. Leaves the plugin folder
# holding whatever build was there before (or removes it if there was none).
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [int]$TimeoutSeconds = 900,
    [string[]]$VamArgs = @('-vrmode', 'None')
)
$ErrorActionPreference = 'Stop'

if (Get-Process -Name VaM -ErrorAction SilentlyContinue) { throw 'VaM is already running -- close it first' }

$here = $PSScriptRoot
$root = Split-Path $here
$testDll = Join-Path $root 'managed\out-selftest\VamDlssNrWorkScale.dll'
$nativeDll = Join-Path $root 'native\out\VamDlssNrWorkScaleNative.dll'
foreach ($p in $testDll, $nativeDll) { if (-not (Test-Path $p)) { throw "build first: $p" } }

$outDir = Join-Path $here 'out\ingame'
$backup = Join-Path $here 'out\ingame-backup'
foreach ($d in $outDir, $backup) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force $d | Out-Null
}

$pluginDir = Join-Path $VamDir 'BepInEx\plugins\VamDlssNrWorkScale'
$cfgDir = Join-Path $VamDir 'BepInEx\config'
$modCfg = Join-Path $cfgDir 'uncleburrito.vamdlssnr.cfg'
$ourCfg = Join-Path $cfgDir 'jeahbwoi720.vamdlssnr.workscale.cfg'

# What was there before, so it can all be put back.
Copy-Item $modCfg (Join-Path $backup 'uncleburrito.vamdlssnr.cfg')
$hadOurCfg = Test-Path $ourCfg
if ($hadOurCfg) { Copy-Item $ourCfg (Join-Path $backup 'jeahbwoi720.vamdlssnr.workscale.cfg') }
$hadPlugin = Test-Path $pluginDir
if ($hadPlugin) { Copy-Item $pluginDir (Join-Path $backup 'plugin') -Recurse }

New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item $testDll, $nativeDll $pluginDir -Force

$started = Get-Date
$env:VWS_SELFTEST_DIR = $outDir
$timedOut = $false

try {
    $vam = Start-Process -FilePath (Join-Path $VamDir 'VaM.exe') -ArgumentList $VamArgs -WorkingDirectory $VamDir -PassThru

    if (-not $vam.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        $null = $vam.CloseMainWindow()

        if (-not $vam.WaitForExit(20000)) { $vam.Kill() }
    }
}
finally {
    Remove-Item Env:\VWS_SELFTEST_DIR -ErrorAction SilentlyContinue

    # Evidence first, then restore.
    foreach ($f in (Join-Path $VamDir 'BepInEx\LogOutput.log'), (Join-Path $VamDir 'BepInEx\plugins\VamDlssNr\ngx\vdn.log'), $ourCfg) {
        if (Test-Path $f) { Copy-Item $f $outDir -Force }
    }

    Copy-Item (Join-Path $backup 'uncleburrito.vamdlssnr.cfg') $modCfg -Force

    if ($hadOurCfg) { Copy-Item (Join-Path $backup 'jeahbwoi720.vamdlssnr.workscale.cfg') $ourCfg -Force }
    elseif (Test-Path $ourCfg) { Remove-Item $ourCfg -Force -Confirm:$false }

    Remove-Item $pluginDir -Recurse -Force -Confirm:$false
    if ($hadPlugin) { Copy-Item (Join-Path $backup 'plugin') $pluginDir -Recurse }
}

"ran for {0:0} s{1}" -f ((Get-Date) - $started).TotalSeconds, ($timedOut ? ' -- TIMED OUT, VaM was closed' : '')
'--- selftest.log ---'
if (Test-Path (Join-Path $outDir 'selftest.log')) { Get-Content (Join-Path $outDir 'selftest.log') } else { '(none written)' }
'--- LogOutput.log: this plugin, errors, and VamDlssNr views ---'
if (Test-Path (Join-Path $outDir 'LogOutput.log')) {
    # VamDlssNr says this when a texture it registered changed under it. With the guides kept
    # between frames it should not appear at all; one per frame was the bug that taught that.
    'texture re-registrations reported by VamDlssNr: ' + @(Select-String -Path (Join-Path $outDir 'LogOutput.log') -Pattern 're-registering').Count
    Get-Content (Join-Path $outDir 'LogOutput.log') | Where-Object { $_ -match '\[vws|Error|Exception|native view|init failed|registration rejected|FRAME COMPOSE|render targets rebuilt' -and $_ -notmatch 'selftest:' } | Select-Object -First 120
}
'--- screenshots ---'
Get-ChildItem $outDir -Filter *.png | Select-Object Name, Length | Format-Table -AutoSize
