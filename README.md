# VaM DLSS – Model Resolution

A companion plugin for [UncleBurrito's VaM DLSS](https://www.patreon.com/UncleBurrito) that adds one
control to its panel: **Model resolution** — how much of the frame, per axis, DLSS Neural Rendering
works at. It is the same idea as *WorkingScale* / "Model resolution" in
[Dagherbou's OptiScaler_DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR), brought to
Virt-A-Mate.

Neural Rendering's cost is per pixel, and at full size it is most of the frame. At 50% the network
sees a quarter of the pixels.

**The frame itself is never reduced.** The network is shown a filtered shrink of the frame, and only
what the network *changed* is enlarged and laid back over the untouched full-size frame. The picture
underneath keeps all of its own detail whatever the slider says; what softens is the network's own
fine structure, which is synthesised small. Its broader work — tone, shading, skin — survives.

![The slider in VaM DLSS's panel](docs/panel.png)

## What it buys

Measured in VaM's start scene at 2560x1440 on an RTX 5060 Laptop, Neural Rendering only (no DLSS
upscaling), one pass:

| Model resolution | Network runs at | Frame time | Frames/s |
|---|---|---|---|
| Neural Rendering off | – | 3.2 ms | 309 |
| 100% (VaM DLSS as it ships) | 2560x1440 | 25.2 ms | 39–40 |
| 75% | 1920x1080 | 17.7 ms | 55–57 |
| 50% | 1280x720 | 9.7 ms | 103–104 |
| 25% | 640x360 | 6.1 ms | 157–169 |

What Neural Rendering adds to the frame falls with the square of the slider, as it should: 22 ms at
full size, about 6.5 at 50%, about 3 at 25%. A millisecond or two of it does not shrink with the
model, so the last steps of the slider buy less than the first.

Those are free-running numbers. Where the frame is held to the display — and in a headset it
always is — the frame rate moves in steps instead: an earlier run of the same test, synchronised to
a 180 Hz monitor, gave 36, 45, 90 and 90 frames/s for 100%, 75%, 50% and 25%. There the question
is only whether the whole frame fits the next step, and the slider is how you make it fit. Your
numbers will differ with the scene, the card and VaM DLSS's other settings; the shape will not.

## Requirements

- **VaM DLSS 1.0.3** by UncleBurrito, installed and working. It is a paid mod and nothing of it is
  included here. This plugin does nothing without it.
- BepInEx 5 (which VaM DLSS already needs), an RTX card.

## Installing

Unzip the release into your VaM folder, giving you:

```
<VaM>\BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScale.dll
<VaM>\BepInEx\plugins\VamDlssNrWorkScale\VamDlssNrWorkScaleNative.dll
<VaM>\AddonPackages\jeahbwoi720.VaMVrNrControl.2.var
```

The folder is the plugin; the `.var` is the session script for the [in-headset panel](#in-headset-controls)
and can be left out if you only want the slider. No file of VaM's or of VaM DLSS's is touched or
replaced. To uninstall, delete the folder and the package.

## Using it

Press **F10** for VaM DLSS's panel, open the **DLSS-NR** tab. Under *Passes* there is a new row:

- **Model resolution** — 0.25 to 1.00. 1.00 is VaM DLSS exactly as it ships. The change is applied
  when you let go of the slider, because every distinct value rebuilds the network. The line under
  it says what the network is actually running at.

It works the same on the monitor and in a headset, where it is applied per eye (and where the
[in-headset panel](#in-headset-controls) has the same slider). It multiplies with
everything else that decides the network's input size: with *Run before DLSS* on and an upscaling
quality mode selected, it is a fraction of the DLSS render extent. Screenshot cameras (the
screenshot key, thumbnails, SuperShot) use it too, which is where the saving is largest.

Settings live in `BepInEx\config\jeahbwoi720.vamdlssnr.workscale.cfg` (1.0.0's settings file is
renamed to that on the first start):

| Setting | Default | |
|---|---|---|
| `ModelResolution` | 1.0 | the slider |
| `ApplyToScreenshots` | true | off = stills always run the network at full size |
| `Enlargement` | 0 | 0 = matched residual; 1 = classic (the network's small picture simply enlarged — softer, for comparison) |
| `AllowSupersampling` | false | lets the slider go to 2.0: the network runs on an *enlarged* frame. Experimental, expensive |
| `DebugView` | 0 | 2 = show the network's edit on its own; 3 = show the frame with the edit left out |

## In-headset controls

VaM DLSS's own panel is a desktop overlay: in a headset it sits on the mirror window and takes the
mouse. The package `jeahbwoi720.VaMVrNrControl` puts the same settings into a **session plugin's
UI**, as VaM's own sliders, toggles and popups, where the controllers' pointer reaches them:

- Neural Rendering on/off, **model resolution**, intensity, local tone, local structure, style,
  passes, run-before-DLSS
- the per-region mask: on/off, edge softness, and intensity / tone / structure for whichever region
  you pick from a list (head, torso, limbs, genitals, clothing, scene)
- DLSS Super Resolution on/off, quality, model, texture sharpening
- frame generation on/off, multiplier, pacing (monitor only, as in VaM DLSS)
- a live status box — what is running, at what size, the frame pacing — and reset buttons

![The panel in a session plugin's UI](docs/session-panel.png)

To add it: in Edit mode, *Main UI → Session Plugins → Add Plugin → Select File*, pick
`VaMVrNrControl.cslist` from the package, then *Open Custom UI*. Save it into your session-plugin
defaults to have it every session.

These are the *same* settings as the F10 panel, not copies: moving either one moves the other, and
they are saved in VaM DLSS's own `.cfg` a moment after you let go. Nothing is stored with the plugin
or in a preset, so a preset can never overwrite them.

The script in the package is only the panel's frame — a status box and a marker. VaM compiles
scripts in a sandbox with no reflection and no file access, so a script cannot reach a BepInEx
plugin's settings; the BepInEx plugin above fills the panel in. Without it the panel shows a note
saying so.

It costs nothing while you play. The plugin is told by VaM's own plugin loader when the script is
created, so nothing is searched for (a search of the scene for it takes 8 ms here, in VaM's empty
start scene — a dropped frame in a headset each time), and a panel that is not open is not kept up
to date; it catches up when you open it.

## How it works

VaM DLSS is not modified. Its own code does all the Neural Rendering work, unchanged — it is simply
handed a smaller frame. Three [Harmony](https://github.com/BepInEx/HarmonyX) hooks do that:

1. **Before** `NrCapture.RunNeuralRendering(w, h, input, …)`: the frame is shrunk into a texture of
   the model's size — an exact area average, in linear light — and the method is given that texture
   and that size instead. It then builds its guides, its view and its evaluate at the size it was
   told, exactly as it does for any other extent.
2. **After** it: the result is resolved back to the frame's size. For every pixel,
   `result = frame + (model output − model input)`, in the sRGB-encoded space the network itself
   reads and writes, with the edit shortened where it would push a colour out of range. This is the
   "matched residual" from Dagherbou's fork (hhkbble's idea): the two pictures being compared are
   the same size, and the only thing that crosses from the small raster is the edit.
3. `NrCapture.Compose` is rewritten to read the resolved frame where it read the network's output.

The two image passes are in a small native DLL and run on Unity's render thread, in command order,
around the evaluate VaM DLSS issues. VaM's D3D11 device is single-threaded, so the managed side
never touches it: it only queues descriptions of work.

If a different build of VaM DLSS lacks anything this reaches for, the plugin says so in
`BepInEx\LogOutput.log`, patches nothing, and VaM DLSS runs as it ships.

## Building

Needs the VS 2022 Build Tools (C++), the Windows SDK, the .NET SDK, Python 3, and a VaM install with
BepInEx 5 and VaM DLSS — the managed half is compiled against VaM's own assemblies.

```
pwsh build.ps1 -VamDir D:\path\to\VaM -Zip     # native + managed + the .var -> dist\ and a release zip
pwsh install.ps1 -VamDir D:\path\to\VaM        # copy dist\ into the game
```

The session script under `vam\` is compiled by VaM itself, with an older compiler; the build compiles
it too, as C# 3 against VaM's assemblies, so a mistake in it fails the build rather than a session.

## Testing

Three layers, none of which needs the others:

- `native\out\vws_test.exe <dll> [warp] [timing]` — the two passes against a CPU reference on a real
  D3D11 device (or the software rasterizer): the area average, the edit landing at full strength and
  only where it was made, the stereo seam, odd ratios, the device left exactly as it was found.
- `pwsh test\run-hosttest.ps1` — the managed half on VaM's own Mono runtime without Unity: the hooks
  go onto the real `VamDlssNrPlugin.dll`, the rewritten method compiles, the native calls marshal.
- `pwsh test\run-ingame.ps1` — VaM itself, driven through every model resolution with the scene
  frozen and a screenshot per step; `python test\analyze-ingame.py` turns those into numbers. It also
  times the network's answer against its input, frame by frame, and works the in-headset panel the
  way a hand would: moves its controls and checks the settings followed, changes the settings and
  checks the controls followed. `-Quick` keeps only the panel checks.

## Credits

- [UncleBurrito](https://www.patreon.com/UncleBurrito) — VaM DLSS, which does all the real work.
- [Dagherbou](https://github.com/Dagherbou/OptiScaler_DLSSNR) and hhkbble — the working-scale and
  matched-residual technique this reimplements for Unity/D3D11.

MIT licensed. Not affiliated with any of the above, nor with NVIDIA or MeshedVR.
