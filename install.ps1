# Copies the built plugin (dist\, from build.ps1) into a VaM install. VaM must not be running.
#
#   pwsh install.ps1 [-VamDir D:\Games\VaM_Updater] [-Uninstall]
#
# The whole plugin is one folder, BepInEx\plugins\VamDlssNrWorkScale. Nothing of VaM's or of
# VaM DLSS's is touched; -Uninstall removes that folder (the .cfg in BepInEx\config is left).
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

if (Get-Process -Name VaM -ErrorAction SilentlyContinue) { throw 'VaM is running -- close it first' }

$target = Join-Path $VamDir 'BepInEx\plugins\VamDlssNrWorkScale'

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force -Confirm:$false; "removed $target" } else { 'not installed' }
    return
}

if (-not (Test-Path (Join-Path $VamDir 'BepInEx\plugins\VamDlssNr\VamDlssNrPlugin.dll'))) {
    throw "VaM DLSS is not installed under $VamDir -- this plugin does nothing without it"
}

$source = Join-Path $PSScriptRoot 'dist\BepInEx\plugins\VamDlssNrWorkScale'
if (-not (Test-Path $source)) { throw 'nothing built -- run build.ps1 first' }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source '*') $target -Force
Get-ChildItem $target | Select-Object Name, Length, LastWriteTime
