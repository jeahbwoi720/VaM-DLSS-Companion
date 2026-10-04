# Copies the built plugin (dist\, from build.ps1) into a VaM install. VaM must not be running.
#
#   pwsh install.ps1 [-VamDir D:\Games\VaM_Updater] [-LooseScript] [-Uninstall]
#
# Installs the plugin folder BepInEx\plugins\VamDlssNrWorkScale, BepInEx\patchers\VamDlssNrWorkScale.Early.dll
# (the part that runs before the game has a window) and the session-script package
# AddonPackages\jeahbwoi720.VaMVrNrControl.<n>.var. Nothing of VaM's or of VaM DLSS's is touched.
#
# -LooseScript also puts the session script at Custom\Scripts\jeahbwoi720\VaMVrNrControl\, for an
#  install whose session-plugin defaults already point there. Someone else's file already at that
#  path is kept in BepInEx\Custom_Experiments_Backup\ first; an earlier copy of this script is not.
# -Uninstall removes the plugin folder, the patcher and the package (settings in BepInEx\config are left).
param(
    [string]$VamDir = 'D:\Games\VaM_Updater',
    [switch]$LooseScript,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

if (Get-Process -Name VaM -ErrorAction SilentlyContinue) { throw 'VaM is running -- close it first' }

$target = Join-Path $VamDir 'BepInEx\plugins\VamDlssNrWorkScale'
$packages = Join-Path $VamDir 'AddonPackages'
$patcher = Join-Path $VamDir 'BepInEx\patchers\VamDlssNrWorkScale.Early.dll'
$dist = Join-Path $PSScriptRoot 'dist'

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force -Confirm:$false; "removed $target" } else { 'plugin not installed' }
    if (Test-Path $patcher) { Remove-Item $patcher -Force -Confirm:$false; "removed $patcher" }
    Get-ChildItem (Join-Path $dist 'AddonPackages') -Filter *.var -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Join-Path $packages $_.Name
        if (Test-Path $p) { Remove-Item $p -Force -Confirm:$false; "removed $p" }
    }
    return
}

if (-not (Test-Path (Join-Path $VamDir 'BepInEx\plugins\VamDlssNr\VamDlssNrPlugin.dll'))) {
    throw "VaM DLSS is not installed under $VamDir -- this plugin does nothing without it"
}

$source = Join-Path $dist 'BepInEx\plugins\VamDlssNrWorkScale'
if (-not (Test-Path $source)) { throw 'nothing built -- run build.ps1 first' }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source '*') $target -Force
New-Item -ItemType Directory -Force (Split-Path $patcher) | Out-Null
Copy-Item (Join-Path $dist 'BepInEx\patchers\VamDlssNrWorkScale.Early.dll') $patcher -Force
Copy-Item (Join-Path $dist 'AddonPackages\*.var') $packages -Force

if ($LooseScript) {
    $scriptSource = Join-Path $PSScriptRoot 'vam\Custom\Scripts\jeahbwoi720\VaMVrNrControl'
    $scriptTarget = Join-Path $VamDir 'Custom\Scripts\jeahbwoi720\VaMVrNrControl'
    New-Item -ItemType Directory -Force $scriptTarget | Out-Null

    # Whether what is there now is an earlier copy of this same script (it carries the marker the
    # plugin looks for). Then it is simply replaced; anyone else's files at that path are kept.
    $installedScript = Join-Path $scriptTarget 'VaMVrNrControl.cs'
    $ours = (Test-Path $installedScript) -and [bool](Select-String -Path $installedScript -Pattern 'VamDlssControlHost' -Quiet)

    foreach ($file in Get-ChildItem $scriptSource -File) {
        $existing = Join-Path $scriptTarget $file.Name

        if (-not $ours -and (Test-Path $existing) -and (Get-FileHash $existing).Hash -ne (Get-FileHash $file.FullName).Hash) {
            $keep = Join-Path $VamDir 'BepInEx\Custom_Experiments_Backup'
            New-Item -ItemType Directory -Force $keep | Out-Null
            $name = '{0}.replaced-{1:yyyyMMdd-HHmmss}' -f $file.Name, (Get-Date)
            Copy-Item $existing (Join-Path $keep $name)
            "kept the previous $($file.Name) as BepInEx\Custom_Experiments_Backup\$name"
        }

        Copy-Item $file.FullName $existing -Force
    }
}

Get-ChildItem $target | Select-Object Name, Length, LastWriteTime
Get-Item $patcher | Select-Object Name, Length, LastWriteTime
Get-ChildItem $packages -Filter 'jeahbwoi720.VaMVrNrControl.*.var' | Select-Object Name, Length, LastWriteTime
