# Third-party parts

The plugin's own code is MIT (see the licence file). Hand tracking is done with three files that
are not this project's; they sit beside the plugin in `BepInEx\plugins\VamDlssNrWorkScale\` and are
only loaded when hand tracking is switched on. Without them everything else works as before.

| File | What it is | Licence | From |
|---|---|---|---|
| `onnxruntime.dll` | ONNX Runtime 1.30.0, the library that runs the two models on the processor | MIT, © Microsoft Corporation | https://github.com/microsoft/onnxruntime (release `v1.30.0`, `onnxruntime-win-x64-1.30.0.zip`, unchanged) |
| `hand-palm.onnx` | MediaPipe's palm detector, as converted for OpenCV's model zoo (`palm_detection_mediapipe_2023feb.onnx`, renamed) | Apache-2.0 | https://github.com/opencv/opencv_zoo/tree/main/models/palm_detection_mediapipe |
| `hand-points.onnx` | MediaPipe's hand landmark model, as converted for OpenCV's model zoo (`handpose_estimation_mediapipe_2023feb.onnx`, renamed) | Apache-2.0 | https://github.com/opencv/opencv_zoo/tree/main/models/handpose_estimation_mediapipe |

Foveated shading is done through NVIDIA's NVAPI (headers and `nvapi64.lib`, MIT, © NVIDIA Corporation, https://github.com/NVIDIA/nvapi at commit `70d337db`): the library is linked into `VamDlssNrWorkScaleNative.dll` and only finds the NVIDIA driver already on the machine; nothing of it is shipped as a file.

ONNX Runtime's own third-party notices are in its release archive (`ThirdPartyNotices.txt`). The
models were made by Google for MediaPipe (https://github.com/google-ai-edge/mediapipe, Apache-2.0).

The source build fetches all three with `native\fetch-deps.ps1`, which checks each against a fixed
SHA-256.
