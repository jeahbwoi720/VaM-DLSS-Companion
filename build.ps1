# Builds both halves and stages the plugin folder as it is installed:
#
#   dist\BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScale.dll
#   dist\BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScaleNative.dll
#
#   pwsh build.ps1 [-VamDir D:\Games\VaM_Updater] [-Zip]
#
# Needs: the VS 2022 Build Tools (C++), the Windows SDK (fxc), the .NET SDK (for Roslyn), and a VaM
# install with BepInEx 5 and VaM DLSS in it -- the managed half is compiled against VaM's own
# assemblies and VamDlssNrPlugin.dll's public types.
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
Copy-Item (Join-Path $here 'README.md') (Join-Path $dist 'VamDlssNrWorkScale-README.md')
Copy-Item (Join-Path $here 'LICENSE') (Join-Path $dist 'VamDlssNrWorkScale-LICENSE.txt')

if ($Zip) {
    $zipPath = Join-Path $here "VamDlssNrWorkScale-v$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force -Confirm:$false }
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zipPath
    Get-Item $zipPath | Select-Object Name, Length
}

"version $version"
Get-ChildItem $dist -Recurse -File | Select-Object @{ n = 'Path'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length,
    @{ n = 'SHA256'; e = { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16) } } | Format-Table -AutoSize
