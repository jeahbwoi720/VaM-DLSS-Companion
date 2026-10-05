# Builds VamDlssNrWorkScaleNative.dll (and the offline test harness) with the VS 2022 Build Tools.
#   pwsh native\build.ps1            -> out\VamDlssNrWorkScaleNative.dll, out\vws_test.exe
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force $out | Out-Null

$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter fxc.exe |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc) { throw 'fxc.exe not found (Windows SDK)' }

$vsRoot = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -latest -property installationPath
$vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsRoot" }

# fxc, not dxc: the passes run on VaM's D3D11 device, which takes DXBC.
$shaders = @(
    @{ Entry = 'VSMain';    Profile = 'vs_5_0'; Name = 'g_vwsVs';        File = 'vws_vs.h' },
    @{ Entry = 'PSDown';    Profile = 'ps_5_0'; Name = 'g_vwsPsDown';    File = 'vws_ps_down.h' },
    @{ Entry = 'PSResolve'; Profile = 'ps_5_0'; Name = 'g_vwsPsResolve'; File = 'vws_ps_resolve.h' },
    @{ Entry = 'PSGuide';   Profile = 'ps_5_0'; Name = 'g_vwsPsGuide';   File = 'vws_ps_guide.h' },
    @{ Entry = 'PSSharpen'; Profile = 'ps_5_0'; Name = 'g_vwsPsSharpen'; File = 'vws_ps_sharpen.h' },
    @{ Entry = 'PSPassthrough'; Profile = 'ps_5_0'; Name = 'g_vwsPsPass'; File = 'vws_ps_pass.h' },
    @{ Entry = 'PSMatte'; Profile = 'ps_5_0'; Name = 'g_vwsPsMatte'; File = 'vws_ps_matte.h' },
    @{ Entry = 'PSOverlay'; Profile = 'ps_5_0'; Name = 'g_vwsPsOverlay'; File = 'vws_ps_overlay.h' },
    @{ Entry = 'VSOverlay'; Profile = 'vs_5_0'; Name = 'g_vwsVsOverlay'; File = 'vws_vs_overlay.h' }
)

foreach ($s in $shaders) {
    & $fxc.FullName /nologo /T $s.Profile /E $s.Entry /O3 /Vn $s.Name /Fh (Join-Path $here $s.File) (Join-Path $here 'vws.hlsl')
    if ($LASTEXITCODE -ne 0) { throw "fxc failed on $($s.Entry)" }
}

# The hand tracker's outside parts (ONNX Runtime's header and DLL, the two models): fetched once.
$deps = Join-Path $here 'deps'
if (-not ((Test-Path (Join-Path $deps 'onnxruntime_c_api.h')) -and (Test-Path (Join-Path $deps 'onnxruntime.dll')) -and (Test-Path (Join-Path $deps 'nvapi\nvapi64.lib')))) {
    & (Join-Path $here 'fetch-deps.ps1') | Out-Null
}

$common = '/nologo /std:c++17 /O2 /W4 /EHsc /MT /DUNICODE /D_UNICODE'
$dll = "cl $common /LD `"$here\vws.cpp`" /Fo`"$out\\`" /Fe`"$out\VamDlssNrWorkScaleNative.dll`" /link /NOLOGO d3d11.lib dxgi.lib dxguid.lib user32.lib `"$here\deps\nvapi\nvapi64.lib`""
$test = "cl $common `"$here\vws_test.cpp`" /Fo`"$out\\`" /Fe`"$out\vws_test.exe`" /link /NOLOGO d3d11.lib dxguid.lib user32.lib d3dcompiler.lib"

cmd /c "`"$vcvars`" >nul && $dll"
if ($LASTEXITCODE -ne 0) { throw 'native DLL build failed' }

if (Test-Path (Join-Path $here 'vws_test.cpp')) {
    cmd /c "`"$vcvars`" >nul && $test"
    if ($LASTEXITCODE -ne 0) { throw 'test harness build failed' }
}

# Beside the DLL, under the names it looks for.
Copy-Item (Join-Path $deps 'onnxruntime.dll') (Join-Path $out 'onnxruntime.dll') -Force
Copy-Item (Join-Path $deps 'palm.onnx') (Join-Path $out 'hand-palm.onnx') -Force
Copy-Item (Join-Path $deps 'handpose.onnx') (Join-Path $out 'hand-points.onnx') -Force

Get-ChildItem $out -Include *.dll, *.exe -Recurse | Select-Object Name, Length, LastWriteTime
