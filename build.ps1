# Builds everything and stages it as it is installed, under dist\:
#
#   BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScale.dll        the plugin
#   BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScaleNative.dll  its D3D11 half
#   BepInEx\patchers\VamDlssNrWorkScale.Early.dll                    the part that runs before the game has a window
#   AddonPackages\jeahbwoi720.VaMVrNrControl.<n>.var                 the in-headset panel's session script
#
#   pwsh build.ps1 [-VamDir D:\Games\VaM_Updater] [-Zip]
#
# Needs: the VS 2022 Build Tools (C++), the Windows SDK (fxc), the .NET SDK (for Roslyn), Python 3,
# and a VaM install with BepInEx 5 and VaM DLSS in it -- the managed half is compiled against VaM's
# own assemblies and VamDlssNrPlugin.dll's public types.
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
& (Join-Path $here 'native\build.ps1') | Out-Null
& (Join-Path $here 'managed\build.ps1') -VamDir $VamDir | Out-Null

$version = (Select-String -Path (Join-Path $here 'managed\WorkScale.cs') -Pattern 'public const string Version = "([^"]+)"').Matches[0].Groups[1].Value
$dist = Join-Path $here 'dist'
$plugin = Join-Path $dist 'BepInEx\plugins\VamDlssNrWorkScale'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $plugin | Out-Null

Copy-Item (Join-Path $here 'managed\out\VamDlssNrWorkScale.dll'), (Join-Path $here 'native\out\VamDlssNrWorkScaleNative.dll') $plugin
$patchers = Join-Path $dist 'BepInEx\patchers'
New-Item -ItemType Directory -Force $patchers | Out-Null
Copy-Item (Join-Path $here 'managed\out\VamDlssNrWorkScale.Early.dll') $patchers
# The hand tracker's outside parts, beside the native DLL that loads them, with their licences.
Copy-Item (Join-Path $here 'native\out\onnxruntime.dll'), (Join-Path $here 'native\out\hand-palm.onnx'), (Join-Path $here 'native\out\hand-points.onnx') $plugin
$fullModel = Join-Path $here 'native\out\hand-points-full.onnx'
if (Test-Path $fullModel) { Copy-Item $fullModel $plugin }
$mercuryModel = Join-Path $here 'native\out\hand-mercury.onnx'
if (Test-Path $mercuryModel) { Copy-Item $mercuryModel $plugin }
$colourModel = Join-Path $here 'native\out\colour.onnx'
if (Test-Path $colourModel) { Copy-Item $colourModel $plugin }
Copy-Item (Join-Path $here 'THIRD-PARTY.md') (Join-Path $dist 'VamDlssNrWorkScale-THIRD-PARTY.md')
Copy-Item (Join-Path $here 'README.md') (Join-Path $dist 'VamDlssNrWorkScale-README.md')
Copy-Item (Join-Path $here 'LICENSE') (Join-Path $dist 'VamDlssNrWorkScale-LICENSE.txt')

& python (Join-Path $here 'tools\make-var.py') (Join-Path $dist 'AddonPackages') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'the .var package could not be built' }

if ($Zip) {
    # The release does not carry the models the build only takes along when they have been put
    # in native\deps by hand (see the README, Building): the zip is made from a copy without them.
    $zipPath = Join-Path $here "VamDlssNrWorkScale-v$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force -Confirm:$false }
    $stage = Join-Path ([IO.Path]::GetTempPath()) "VamDlssNrWorkScale-zip-$PID"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
    Copy-Item $dist $stage -Recurse
    foreach ($optional in 'hand-points-full.onnx', 'hand-mercury.onnx', 'colour.onnx') {
        $path = Join-Path $stage "BepInEx\plugins\VamDlssNrWorkScale\$optional"
        if (Test-Path $path) { Remove-Item $path -Force -Confirm:$false }
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
    Remove-Item $stage -Recurse -Force -Confirm:$false
    Get-Item $zipPath | Select-Object Name, Length
}

"version $version"
Get-ChildItem $dist -Recurse -File | Select-Object @{ n = 'Path'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length,
    @{ n = 'SHA256'; e = { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16) } } | Format-Table -AutoSize
