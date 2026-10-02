# Builds and runs the managed checks on VaM's own Mono runtime (no Unity, no GPU).
#   pwsh test\run-hosttest.ps1 [-VamDir D:\Games\VaM_Updater]
param([string]$VamDir = 'D:\Games\VaM_Updater')
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = Split-Path $here
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$managed = Join-Path $VamDir 'VaM_Data\Managed'
$core = Join-Path $VamDir 'BepInEx\core'
$modDir = Join-Path $VamDir 'BepInEx\plugins\VamDlssNr'
$plugin = Join-Path $root 'managed\out\VamDlssNrWorkScale.dll'
$native = Join-Path $root 'native\out\VamDlssNrWorkScaleNative.dll'
foreach ($p in $plugin, $native) { if (-not (Test-Path $p)) { throw "build first: $p" } }

# The plugin folder as it will be installed: the managed and the native half side by side.
$stage = Join-Path $out 'plugin'
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $plugin, $native $stage -Force

$vsRoot = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -latest -property installationPath
$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vcvars`" >nul 2>&1 && cl /nologo /O2 /W3 /MT `"$here\monohost.c`" /Fo`"$out\\`" /Fe`"$out\monohost.exe`" /link /NOLOGO" | Where-Object { $_ -notmatch '^monohost\.c$' }
if ($LASTEXITCODE -ne 0) { throw 'monohost build failed' }

$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -replace '^(\S+) \[(.+)\]$', '$2\$1'
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
$refs = @(
    (Join-Path $managed 'mscorlib.dll'), (Join-Path $managed 'System.dll'), (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'), (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'),
    (Join-Path $modDir 'VamDlssNrPlugin.dll'), $plugin
) | ForEach-Object { "-r:$_" }

& dotnet exec $csc -nologo -target:exe -nostdlib+ -noconfig -langversion:7.3 -optimize+ -debug- -nowarn:1701,1702 `
    "-out:$(Join-Path $out 'VwsHostTest.exe')" @refs (Join-Path $here 'HostTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'host test build failed' }

# BepInEx's folder first: VaM ships an old Mono.Cecil (0.9.6) in Managed, and in the game BepInEx's
# own 0.10.4 is already loaded by the time anything asks for it.
$searchPath = @($core, $managed, $modDir, $stage) -join ';'
& (Join-Path $out 'monohost.exe') $VamDir $searchPath (Join-Path $out 'VwsHostTest.exe') $stage
exit $LASTEXITCODE
