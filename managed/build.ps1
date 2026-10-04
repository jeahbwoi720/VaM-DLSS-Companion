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
#
# The session script under vam\ is not part of the DLL -- VaM compiles it itself, with an older
# compiler -- but it is compiled here too, as C# 3 against VaM's assemblies, so that a mistake in
# it fails the build rather than a headset session.
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

$vamRefs = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.ImageConversionModule.dll'),
    (Join-Path $managed 'UnityEngine.VRModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'Assembly-CSharp.dll')
)
$refs = $vamRefs + @(
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core '0Harmony.dll'),
    $ModDll
)
$sources = @((Join-Path $here 'WorkScale.cs'), (Join-Path $here 'ControlPanel.cs'), (Join-Path $here 'HeadsetUi.cs'), (Join-Path $here 'CameraProbe.cs'), (Join-Path $here 'Passthrough.cs'), (Join-Path $here 'Hands.cs'), (Join-Path $here 'HandDrive.cs'), (Join-Path $here 'HandUi.cs'), (Join-Path $here 'Foveation.cs'), (Join-Path $here 'Presentation.cs'))
$defines = @()

if ($SelfTest) {
    $refs += (Join-Path $managed 'UnityEngine.ScreenCaptureModule.dll')
    $sources += (Join-Path $here 'SelfTest.cs')
    $defines += '-define:VWS_SELFTEST'
}

& dotnet exec $csc -nologo -target:library -nostdlib+ -noconfig -langversion:7.3 -optimize+ -debug- -deterministic `
    -warnaserror- -nowarn:1701,1702 "-out:$(Join-Path $out 'VamDlssNrWorkScale.dll')" @defines @($refs | ForEach-Object { "-r:$_" }) @sources
if ($LASTEXITCODE -ne 0) { throw 'managed build failed' }

# The preloader patcher (BepInEx\patchers): the part that runs before the game has a window.
$early = Join-Path $out 'VamDlssNrWorkScale.Early.dll'
& dotnet exec $csc -nologo -target:library -nostdlib+ -noconfig -langversion:7.3 -optimize+ -debug- -deterministic -nowarn:1701,1702 "-out:$early" `
    "-r:$(Join-Path $managed 'mscorlib.dll')" "-r:$(Join-Path $managed 'System.dll')" "-r:$(Join-Path $core 'BepInEx.dll')" "-r:$(Join-Path $core 'Mono.Cecil.dll')" (Join-Path $here 'Early.cs')
if ($LASTEXITCODE -ne 0) { throw 'the preloader patcher does not compile' }

# The session script, as VaM's own compiler would be asked to take it.
$scripts = Get-ChildItem (Join-Path (Split-Path $here) 'vam\Custom\Scripts') -Recurse -Filter *.cs
$check = Join-Path $out 'script-check.dll'
& dotnet exec $csc -nologo -target:library -nostdlib+ -noconfig -langversion:3 -nowarn:1701,1702 "-out:$check" `
    @($vamRefs | ForEach-Object { "-r:$_" }) @($scripts.FullName)
if ($LASTEXITCODE -ne 0) { throw 'the VaM session script does not compile' }
Remove-Item $check -Force -Confirm:$false

Get-Item (Join-Path $out 'VamDlssNrWorkScale.dll') | Select-Object Name, Length, LastWriteTime
