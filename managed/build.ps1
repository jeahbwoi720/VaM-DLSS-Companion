# Builds VamDlssNrWorkScale.dll for VaM's runtime (Unity 2018.1, legacy Mono / .NET 3.5 profile).
#
# Compiled with Roslyn against VaM's own mscorlib and Unity assemblies and BepInEx's HarmonyX, so
# there is nothing to restore from NuGet. VamDlssNrPlugin.dll is referenced for its public types
# only; everything private is reached by reflection at run time.
#
#   pwsh managed\build.ps1 [-VamDir D:\Games\VaM_Updater] [-SelfTest]
#
# -SelfTest builds the in-game self-test driver in as well (see SelfTest.cs), into out-selftest\.
# That build is for test\run-ingame.ps1 only and is never the one installed.
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [string]$ModDll = '',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$out = Join-Path $here ($SelfTest ? 'out-selftest' : 'out')
New-Item -ItemType Directory -Force $out | Out-Null

$managed = Join-Path $VamDir 'VaM_Data\Managed'
$core = Join-Path $VamDir 'BepInEx\core'
if (-not $ModDll) { $ModDll = Join-Path $VamDir 'BepInEx\plugins\VamDlssNr\VamDlssNrPlugin.dll' }
foreach ($p in $managed, $core, $ModDll) { if (-not (Test-Path $p)) { throw "not found: $p" } }

$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -replace '^(\S+) \[(.+)\]$', '$2\$1'
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "csc.dll not found under $sdk" }

$refs = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core '0Harmony.dll'),
    $ModDll
)
$sources = @((Join-Path $here 'WorkScale.cs'))
$defines = @()

if ($SelfTest) {
    $refs += (Join-Path $managed 'UnityEngine.ScreenCaptureModule.dll')
    $sources += (Join-Path $here 'SelfTest.cs')
    $defines += '-define:VWS_SELFTEST'
}

& dotnet exec $csc -nologo -target:library -nostdlib+ -noconfig -langversion:7.3 -optimize+ -debug- -deterministic `
    -warnaserror- -nowarn:1701,1702 "-out:$(Join-Path $out 'VamDlssNrWorkScale.dll')" @defines @($refs | ForEach-Object { "-r:$_" }) @sources
if ($LASTEXITCODE -ne 0) { throw 'managed build failed' }

Get-Item (Join-Path $out 'VamDlssNrWorkScale.dll') | Select-Object Name, Length, LastWriteTime
