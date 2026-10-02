# Runs the in-game self-test: starts VaM in desktop mode with the self-test build of the plugin,
# waits for it to walk through its steps and quit, then puts everything back.
#
#   pwsh test\run-ingame.ps1 [-VamDir D:\Games\VaM_Updater] [-TimeoutSeconds 900]
#
# Needs: managed\build.ps1 -SelfTest and native\build.ps1 to have been run. Touches, and restores:
# BepInEx\config\uncleburrito.vamdlssnr.cfg and this plugin's own .cfg. Leaves the plugin folder
# holding whatever build was there before (or removes it if there was none).
#
# This plugin's settings are set aside for the run and a file is left in their place as an earlier
# build would have named it, holding a value that is not the default: the run starts with the
# plugin taking that file over, and the self-test checks that it did.
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [int]$TimeoutSeconds = 900,
    [string[]]$VamArgs = @('-vrmode', 'None'),
    # Skip the resolution sweep and the timing probe; keep the in-headset panel checks.
    [switch]$Quick
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
$restored = Join-Path $backup 'restored.ok'

# The backup of an interrupted run is the only copy of what it had set aside: never delete it.
if ((Test-Path $backup) -and -not (Test-Path $restored)) {
    throw "the previous run did not put things back -- its copies are in $backup. Restore them by hand (config files to BepInEx\config, plugin\ to BepInEx\plugins\VamDlssNrWorkScale), then delete that folder."
}

foreach ($d in $outDir, $backup) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Force $d | Out-Null
}

$pluginDir = Join-Path $VamDir 'BepInEx\plugins\VamDlssNrWorkScale'
$cfgDir = Join-Path $VamDir 'BepInEx\config'
$modCfg = Join-Path $cfgDir 'uncleburrito.vamdlssnr.cfg'
$ourCfgs = '*.vamdlssnr.workscale.cfg'   # whatever owner prefix the build that wrote it used
$ourBackup = Join-Path $backup 'ours'
$stagedScale = '0.85'
$started = Get-Date
$timedOut = $false
$hadPlugin = Test-Path $pluginDir

try {
    # What was there before, so it can all be put back.
    Copy-Item $modCfg (Join-Path $backup 'uncleburrito.vamdlssnr.cfg')
    New-Item -ItemType Directory -Force $ourBackup | Out-Null
    Get-ChildItem $cfgDir -Filter $ourCfgs | Copy-Item -Destination $ourBackup
    if ($hadPlugin) { Copy-Item $pluginDir (Join-Path $backup 'plugin') -Recurse }

    New-Item -ItemType Directory -Force $pluginDir | Out-Null
    Copy-Item $testDll, $nativeDll $pluginDir -Force

    # An earlier build's settings file, for this one to take over.
    Get-ChildItem $cfgDir -Filter $ourCfgs | Remove-Item -Force -Confirm:$false
    Set-Content (Join-Path $cfgDir 'earlier.vamdlssnr.workscale.cfg') "[Neural Rendering]`n`nModelResolution = $stagedScale`n" -Encoding utf8

    $env:VWS_SELFTEST_DIR = $outDir
    $env:VWS_SELFTEST_QUICK = $Quick ? '1' : '0'
    $env:VWS_SELFTEST_EXPECT_SCALE = $stagedScale

    # The session-script package, if it is installed: the self-test checks VaM took it.
    $var = Get-ChildItem (Join-Path $VamDir 'AddonPackages') -Filter 'jeahbwoi720.VaMVrNrControl.*.var' -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.BaseName -split '\.')[-1] } | Select-Object -Last 1
    $env:VWS_SELFTEST_VAR = $var ? $var.BaseName : ''

    $vam = Start-Process -FilePath (Join-Path $VamDir 'VaM.exe') -ArgumentList $VamArgs -WorkingDirectory $VamDir -PassThru

    if (-not $vam.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        $null = $vam.CloseMainWindow()

        if (-not $vam.WaitForExit(20000)) { $vam.Kill() }
    }
}
finally {
    Remove-Item Env:\VWS_SELFTEST_DIR, Env:\VWS_SELFTEST_QUICK, Env:\VWS_SELFTEST_VAR, Env:\VWS_SELFTEST_EXPECT_SCALE -ErrorAction SilentlyContinue

    # Evidence first, then restore.
    foreach ($f in (Join-Path $VamDir 'BepInEx\LogOutput.log'), (Join-Path $VamDir 'BepInEx\plugins\VamDlssNr\ngx\vdn.log')) {
        if (Test-Path $f) { Copy-Item $f $outDir -Force }
    }

    Get-ChildItem $cfgDir -Filter $ourCfgs | Copy-Item -Destination $outDir -Force

    # Each only if its copy was taken: a run that failed before that has changed nothing of it.
    if (Test-Path (Join-Path $backup 'uncleburrito.vamdlssnr.cfg')) {
        Copy-Item (Join-Path $backup 'uncleburrito.vamdlssnr.cfg') $modCfg -Force
    }

    if (Test-Path $ourBackup) {
        Get-ChildItem $cfgDir -Filter $ourCfgs | Remove-Item -Force -Confirm:$false
        Get-ChildItem $ourBackup | Copy-Item -Destination $cfgDir -Force
    }

    if (-not $hadPlugin) {
        if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force -Confirm:$false }
    }
    elseif (Test-Path (Join-Path $backup 'plugin')) {
        Remove-Item $pluginDir -Recurse -Force -Confirm:$false
        Copy-Item (Join-Path $backup 'plugin') $pluginDir -Recurse
    }

    Set-Content $restored (Get-Date -Format o)
}

"ran for {0:0} s{1}" -f ((Get-Date) - $started).TotalSeconds, ($timedOut ? ' -- TIMED OUT, VaM was closed' : '')
'--- selftest.log ---'
if (Test-Path (Join-Path $outDir 'selftest.log')) { Get-Content (Join-Path $outDir 'selftest.log') } else { '(none written)' }
'--- LogOutput.log: this plugin, errors, and VamDlssNr views ---'
if (Test-Path (Join-Path $outDir 'LogOutput.log')) {
    # VamDlssNr says this when a texture it registered changed under it. With the guides kept
    # between frames it should not appear at all; one per frame was the bug that taught that.
    'texture re-registrations reported by VamDlssNr: ' + @(Select-String -Path (Join-Path $outDir 'LogOutput.log') -Pattern 're-registering').Count
    Get-Content (Join-Path $outDir 'LogOutput.log') | Where-Object { $_ -match '\[vws|Error|Exception|native view|init failed|registration rejected|FRAME COMPOSE|render targets rebuilt|VaMVrNrControl' -and $_ -notmatch 'selftest:' } | Select-Object -First 120
}
'--- screenshots ---'
Get-ChildItem $outDir -Filter *.png | Select-Object Name, Length | Format-Table -AutoSize
