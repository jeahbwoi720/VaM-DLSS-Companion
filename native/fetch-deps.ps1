# Fetches what the native half is built and shipped with into native\deps\ (not in the repo):
#   ONNX Runtime (MIT)            - its C header for the build, onnxruntime.dll for the release
#   NVAPI (MIT)                   - NVIDIA's headers and library, for variable rate shading
#   two hand models (Apache-2.0)  - MediaPipe's palm detector and hand landmarks, as converted for
#                                   OpenCV's model zoo (github.com/opencv/opencv_zoo)
#   pwsh native\fetch-deps.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$deps = Join-Path $PSScriptRoot 'deps'
New-Item -ItemType Directory -Force $deps | Out-Null

$ortVersion = '1.30.0'
$ortName = "onnxruntime-win-x64-$ortVersion"
$files = @(
    @{ Name = 'palm.onnx'; Hash = '78FF51C38496B7FC8B8EBDB6CC8C1ABB02FA6C38427C6848254CDABA57FCCE7C'
       Url = 'https://github.com/opencv/opencv_zoo/raw/main/models/palm_detection_mediapipe/palm_detection_mediapipe_2023feb.onnx' },
    @{ Name = 'handpose.onnx'; Hash = 'DB0898AE717B76B075D9BF563AF315B29562E11F8DF5027A1EF07B02BEF6D81C'
       Url = 'https://github.com/opencv/opencv_zoo/raw/main/models/handpose_estimation_mediapipe/handpose_estimation_mediapipe_2023feb.onnx' }
)

function Test-Hash($path, $hash) {
    (Test-Path $path) -and ((Get-FileHash $path -Algorithm SHA256).Hash -eq $hash)
}

foreach ($f in $files) {
    $path = Join-Path $deps $f.Name

    if (-not (Test-Hash $path $f.Hash)) {
        Invoke-WebRequest $f.Url -OutFile $path
        if (-not (Test-Hash $path $f.Hash)) { throw "$($f.Name): not the file expected (SHA-256 differs)" }
    }
}

$ortHash = 'C6BA983BAF5681AF108599675D2A89C2D145512D02DE28AED0BFF177CD0BA949'
$dll = Join-Path $deps 'onnxruntime.dll'
$header = Join-Path $deps 'onnxruntime_error_code.h'

if (-not ((Test-Path $dll) -and (Test-Path $header) -and (Get-Item $dll).VersionInfo.ProductVersion -like "$ortVersion*")) {
    $zip = Join-Path $deps "$ortName.zip"

    if (-not (Test-Hash $zip $ortHash)) {
        Invoke-WebRequest "https://github.com/microsoft/onnxruntime/releases/download/v$ortVersion/$ortName.zip" -OutFile $zip
        if (-not (Test-Hash $zip $ortHash)) { throw "$ortName.zip: not the file expected (SHA-256 differs)" }
    }

    $tmp = Join-Path $deps 'unpack'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    Copy-Item (Join-Path $tmp "$ortName\lib\onnxruntime.dll") $dll -Force
    Copy-Item (Join-Path $tmp "$ortName\include\*.h") $deps -Force
    Get-ChildItem (Join-Path $tmp $ortName) -Filter 'LICENSE*' | Copy-Item -Destination (Join-Path $deps 'onnxruntime-LICENSE.txt') -Force
    Get-ChildItem (Join-Path $tmp $ortName) -Filter 'ThirdPartyNotices*' | Copy-Item -Destination (Join-Path $deps 'onnxruntime-ThirdPartyNotices.txt') -Force
    Remove-Item $tmp -Recurse -Force
    Remove-Item $zip -Force
}

# NVAPI (MIT): NVIDIA's interface to its driver, for variable rate shading. Headers and the 64-bit
# library, from one commit of github.com/NVIDIA/nvapi; the library is checked against its hash.
$nvCommit = '70d337db9186e968eab622f7e786de7e437faf3d'
$nvLibHash = '901E479E548D41C30688C7481F0C6353C0BE7FDEE978796D27D87C32E19CE172'
$nv = Join-Path $deps 'nvapi'
New-Item -ItemType Directory -Force $nv | Out-Null

if (-not (Test-Hash (Join-Path $nv 'nvapi64.lib') $nvLibHash) -or -not (Test-Path (Join-Path $nv 'nvapi.h'))) {
    foreach ($f in 'nvapi.h', 'nvapi_lite_common.h', 'nvapi_lite_d3dext.h', 'nvapi_lite_salend.h', 'nvapi_lite_salstart.h', 'nvapi_lite_sli.h',
                   'nvapi_lite_stereo.h', 'nvapi_lite_surround.h', 'nvapi_interface.h', 'nvShaderExtnEnums.h', 'License.txt', 'amd64/nvapi64.lib') {
        Invoke-WebRequest "https://raw.githubusercontent.com/NVIDIA/nvapi/$nvCommit/$f" -OutFile (Join-Path $nv (Split-Path $f -Leaf))
    }

    if (-not (Test-Hash (Join-Path $nv 'nvapi64.lib') $nvLibHash)) { throw 'nvapi64.lib: not the file expected (SHA-256 differs)' }
}

Get-ChildItem $deps | Select-Object Name, Length
