// VaM DLSS -- Model Resolution.
//
// A companion to UncleBurrito's VaM DLSS (VamDlssNr). It adds one thing: Neural Rendering can run on
// a smaller raster than the frame it edits, the way "WorkingScale" / "Model resolution" does in
// Dagherbou's OptiScaler_DLSSNR fork. The frame is never reduced. A filtered shrink of it is shown to
// the network, and only the network's EDIT is enlarged and laid back onto the full-size frame.
//
// VamDlssNr itself is not modified. Its own code does all the Neural Rendering work, unchanged; it is
// simply handed a smaller frame:
//
//   RunNeuralRendering(w, h, input, ...)      <- prefix: shrink `input`, pass the smaller size on
//       ... VamDlssNr builds its view at that size, runs the network ...
//                                             <- postfix: resolve the result back to w x h
//   Compose() reading NrCapture._output       <- transpiler: reads the resolved frame instead
//
// The image passes live in VamDlssNrWorkScaleNative.dll and run on Unity's render thread; see
// native/vws.cpp for why nothing here touches D3D11 directly.
//
// THE FOCUS WINDOW. The network can also be shown only a window of the view -- the part around the
// lens centre of each eye, where a headset is sharp and where one mostly looks -- and nothing is
// done to the rest. The frame's window is what gets shrunk (or handed over as it is), the edit
// comes back into that window, faded out towards its edge. Two more things have to follow the
// window, and both are done from inside RunNeuralRendering by a transpiler:
//
//   Graphics.Blit(_netMVec, _nrMVec) etc.      -> GuideBlit: the guides are cut to the same window
//   VamDlssNrPlugin.SetParams(.., mx, my, ..)  -> SetParamsWindowed: a motion vector is a fraction
//                                                 of the eye, and the window is a fraction of that

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VamDlssNr;

[assembly: AssemblyTitle("VamDlssNrWorkScale")]
[assembly: AssemblyDescription("Model resolution (working scale) for VaM DLSS Neural Rendering")]
[assembly: AssemblyVersion("1.5.1.0")]
[assembly: AssemblyFileVersion("1.5.1.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VwsHostTest")]

namespace VamDlssNrWorkScale
{
    internal static class Native
    {
        private const string Dll = "VamDlssNrWorkScaleNative";
        private const uint ExpectedAbi = 37;

        internal const uint Frame = 1, Proxy = 2, Model = 4, Result = 8;
        internal const int ReadyDown = 1, ReadyResolve = 2, ReadyGuide = 4; // ReadyGuide << n for guide n

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint AbiFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr EventFuncFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushRegisterFn(uint set, IntPtr frame, IntPtr proxy, IntPtr model, IntPtr result, uint which, uint srgbMask, uint token);

        // `window` is eight numbers (origin and size per eye, as fractions of the eye) or null.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushPassFn(uint resolve, uint set, uint eyes, float strength, uint mode, [In] float[] window, float feather);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushRegisterGuideFn(uint set, uint index, IntPtr source, IntPtr copy, uint token);

        // `previous` is the window as it was a frame ago, eight numbers like `window`, or null.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushGuideFn(uint set, uint index, uint eyes, [In] float[] window, [In] float[] previous, uint topDown);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushReleaseFn(uint set);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PollFn(uint set, out int ready, out int error, out uint token, out int lastHr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DrainFn([In, Out] byte[] buffer, int capacity);

        private static PushRegisterFn _pushRegister;
        private static PushPassFn _pushPass;
        private static PushRegisterGuideFn _pushRegisterGuide;
        private static PushGuideFn _pushGuide;
        private delegate int PushSharpenFn(IntPtr texture, float strength, uint eyes);
        private static PushSharpenFn _pushSharpen;
        private delegate int PushDepthFillFn(IntPtr depth, float u0, float uw, uint flip, float slack, uint reversed);
        private static PushDepthFillFn _pushDepthFill;
        private delegate void DepthFillStatusFn(out uint done, out uint noTarget, out uint failed);
        private static DepthFillStatusFn _depthFillStatus;
        private delegate void CamConfigureFn([In] float[] values, uint count);
        private delegate int CamStartFn(IntPtr getFrameBuffer, ulong handle, uint width, uint height);
        private delegate void CamStopFn();
        private delegate void CamStatusFn(out uint frames, out uint uploads, out int error);
        private delegate int PushPassthroughFn(IntPtr target, uint eyes, uint eye, uint srgb, uint topDown, [In] float[] eyeToRoom);
        private static CamConfigureFn _camConfigure;
        private static CamStartFn _camStart;
        private static CamStopFn _camStop;
        private static CamStatusFn _camStatus;
        private static PushPassthroughFn _pushPassthrough;
        private delegate int PushMatteFn(IntPtr source, uint eyes, uint eye, uint srgb, uint topDown, [In] float[] headPose, uint fromAlpha, IntPtr depth, [In] float[] depthInfo);
        private delegate void OverlayDepthFn(out float room, out float scene);
        private delegate int DepthReadFn([Out] byte[] rgba, uint capacity, out uint side, out uint micros);
        private delegate int CamReadFn([Out] byte[] grey, uint capacity, out uint width, out uint height);
        private delegate int FoveaEventFn(uint on);
        private delegate void FoveaConfigureFn([In] float[] values, uint count, IntPtr anyTexture);
        private delegate void FoveaStatusFn(out int state, out uint width, out uint height, out uint samples, out uint coarse, out uint ons);
        private static FoveaEventFn _foveaEvent;
        private static FoveaConfigureFn _foveaConfigure;
        private static FoveaStatusFn _foveaStatus;
        private delegate void FoveaMissesFn(out uint noTarget, out uint small, out uint byDepth);
        private static FoveaMissesFn _foveaMisses;
        private delegate void FoveaAskedFn(out uint asked, out uint noDevice);
        private static FoveaAskedFn _foveaAsked;
        private delegate int FoveaEventForFn(uint camera, uint opaque);
        private delegate void FoveaMeasuredFn([Out] ulong[] runs, [Out] uint[] times);
        private static FoveaEventForFn _foveaEventFor;
        private static FoveaMeasuredFn _foveaMeasured, _foveaTimed;
        private delegate int SpanEventFn(uint index, uint begin);
        private delegate void SpansFn([Out] ulong[] micros, [Out] uint[] times, uint reset);
        private static SpanEventFn _spanEvent;
        private static SpansFn _spans;
        private delegate void FlipStatusFn(out int state, out uint width, out uint height, out uint presents, out uint tearing);
        private static FlipStatusFn _flipStatus;
        private delegate void FlipPaceFn(int on, uint multiplier);
        private delegate void FlipPaceStatusFn(out float rate, out float refresh, out float each, out float queue, out uint dropped);
        private static FlipPaceFn _flipPace;
        private static FlipPaceStatusFn _flipPaceStatus;
        private delegate int HandLoadFn(IntPtr folder);
        private delegate void HandErrorFn([Out] byte[] text, uint capacity);
        private delegate void HandConfigureFn(uint on, uint every, [In] float[] values, uint count);
        private delegate int HandReadFn([Out] float[] hands, uint capacity, out uint serial, out uint micros, out float ageMs, out uint inRoom);
        private static HandLoadFn _handLoad;
        private delegate void HandPreferFn(uint which);
        private static HandPreferFn _handPrefer;
        private delegate int HandFullFn();
        private static HandFullFn _handFull, _handMercury;
        private delegate void HandCurlsFn([Out] float[] curls);
        private static HandCurlsFn _handCurls;
        private delegate void ResolveOptionsFn(uint follow, float edge, float sharpen, float halo, uint outline);
        private delegate int PushSteadyFn(uint set, uint eyes, IntPtr motion, float take, uint topDown, [In] float[] window, uint pass, uint fresh, uint full, float shiftX, float shiftY, float own);
        private static PushSteadyFn _pushSteady;
        private delegate void DownOptionsFn(float sharpen, float shiftX, float shiftY);
        private static DownOptionsFn _downOptions;
        private static ResolveOptionsFn _resolveOptions;
        private static HandErrorFn _handError;
        private static HandConfigureFn _handConfigure;
        private static HandReadFn _handRead;
        private static OverlayDepthFn _overlayDepth;
        private static DepthReadFn _depthRead;
        private static CamReadFn _camRead;
        private delegate void OverlayStatusFn(out int state, out uint frames, out int error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ColourStatusFn(out int state, out uint pictures, out uint micros);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint ColourErrorFn([Out] byte[] text, uint capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OverlayLostFn(uint inARow, IntPtr pause);
        private static OverlayLostFn _overlayLost;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint LogsFn(uint off, out uint writes, out uint kilobytes);
        private static LogsFn _logs;
        private static ColourStatusFn _colourStatus;
        private static ColourErrorFn _colourError;
        private static PushMatteFn _pushMatte;
        private static OverlayStatusFn _overlayStatus;
        private delegate int OverlayReadFn([Out] byte[] rgba, uint capacity, out uint width, out uint height);
        private static OverlayReadFn _overlayRead;
        private static PushReleaseFn _pushRelease;
        private static PollFn _poll;
        private static DrainFn _drain;

        internal static bool Loaded;
        internal static string Problem = "";
        internal static IntPtr EventFunc = IntPtr.Zero;

        private static readonly byte[] _logBuffer = new byte[4096];

        private static Delegate Bind(IntPtr module, string name, Type type)
        {
            IntPtr address = GetProcAddress(module, name);

            if (address == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException(name);
            }

            return Marshal.GetDelegateForFunctionPointer(address, type);
        }

        // Bound by address rather than by DllImport: the module is loaded by full path, and how a
        // bare library name is then resolved differs between Unity's Mono and Mono on its own.
        // Function pointers do not depend on any of it.
        internal static bool Load(string dir)
        {
            string path = Path.Combine(dir, Dll + ".dll");

            try
            {
                if (!File.Exists(path))
                {
                    Problem = Dll + ".dll is missing from " + dir;
                    return false;
                }

                IntPtr module = LoadLibraryW(path);

                if (module == IntPtr.Zero)
                {
                    Problem = Dll + ".dll would not load (win32 error " + Marshal.GetLastWin32Error() + ")";
                    return false;
                }

                uint abi = ((AbiFn)Bind(module, "vws_abi", typeof(AbiFn)))();

                if (abi != ExpectedAbi)
                {
                    Problem = Dll + ".dll is ABI " + abi + ", this plugin wants " + ExpectedAbi + " -- the two files are from different builds";
                    return false;
                }

                EventFunc = ((EventFuncFn)Bind(module, "vws_event_func", typeof(EventFuncFn)))();

                if (EventFunc == IntPtr.Zero)
                {
                    Problem = Dll + ".dll gave no render callback";
                    return false;
                }

                _pushRegister = (PushRegisterFn)Bind(module, "vws_push_register", typeof(PushRegisterFn));
                _pushPass = (PushPassFn)Bind(module, "vws_push_pass_window", typeof(PushPassFn));
                _pushRegisterGuide = (PushRegisterGuideFn)Bind(module, "vws_push_register_guide", typeof(PushRegisterGuideFn));
                _pushGuide = (PushGuideFn)Bind(module, "vws_push_guide", typeof(PushGuideFn));
                _pushSharpen = (PushSharpenFn)Bind(module, "vws_push_sharpen", typeof(PushSharpenFn));
                _pushDepthFill = (PushDepthFillFn)Bind(module, "vws_push_depth_fill", typeof(PushDepthFillFn));
                _depthFillStatus = (DepthFillStatusFn)Bind(module, "vws_depth_fill_status", typeof(DepthFillStatusFn));
                _camConfigure = (CamConfigureFn)Bind(module, "vws_cam_configure", typeof(CamConfigureFn));
                _camStart = (CamStartFn)Bind(module, "vws_cam_start", typeof(CamStartFn));
                _camStop = (CamStopFn)Bind(module, "vws_cam_stop", typeof(CamStopFn));
                _camStatus = (CamStatusFn)Bind(module, "vws_cam_status", typeof(CamStatusFn));
                _pushPassthrough = (PushPassthroughFn)Bind(module, "vws_push_passthrough", typeof(PushPassthroughFn));
                _pushMatte = (PushMatteFn)Bind(module, "vws_push_matte", typeof(PushMatteFn));
                _overlayStatus = (OverlayStatusFn)Bind(module, "vws_overlay_status", typeof(OverlayStatusFn));
                _colourStatus = (ColourStatusFn)Bind(module, "vws_colour_status", typeof(ColourStatusFn));
                _overlayLost = (OverlayLostFn)Bind(module, "vws_overlay_lost", typeof(OverlayLostFn));
                _logs = (LogsFn)Bind(module, "vws_logs", typeof(LogsFn));
                _colourError = (ColourErrorFn)Bind(module, "vws_colour_error", typeof(ColourErrorFn));
                _overlayRead = (OverlayReadFn)Bind(module, "vws_overlay_read", typeof(OverlayReadFn));
                _overlayDepth = (OverlayDepthFn)Bind(module, "vws_overlay_depth", typeof(OverlayDepthFn));
                _depthRead = (DepthReadFn)Bind(module, "vws_depth_read", typeof(DepthReadFn));
                _camRead = (CamReadFn)Bind(module, "vws_cam_read", typeof(CamReadFn));
                _foveaEvent = (FoveaEventFn)Bind(module, "vws_fovea_event", typeof(FoveaEventFn));
                _foveaConfigure = (FoveaConfigureFn)Bind(module, "vws_fovea_configure", typeof(FoveaConfigureFn));
                _foveaStatus = (FoveaStatusFn)Bind(module, "vws_fovea_status", typeof(FoveaStatusFn));
                _foveaMisses = (FoveaMissesFn)Bind(module, "vws_fovea_misses", typeof(FoveaMissesFn));
                _foveaAsked = (FoveaAskedFn)Bind(module, "vws_fovea_asked", typeof(FoveaAskedFn));
                _foveaEventFor = (FoveaEventForFn)Bind(module, "vws_fovea_event_for", typeof(FoveaEventForFn));
                _foveaMeasured = (FoveaMeasuredFn)Bind(module, "vws_fovea_measured", typeof(FoveaMeasuredFn));
                _foveaTimed = (FoveaMeasuredFn)Bind(module, "vws_fovea_timed", typeof(FoveaMeasuredFn));
                _spanEvent = (SpanEventFn)Bind(module, "vws_span_event", typeof(SpanEventFn));
                _spans = (SpansFn)Bind(module, "vws_spans", typeof(SpansFn));
                _flipStatus = (FlipStatusFn)Bind(module, "vws_flip_status", typeof(FlipStatusFn));
                _flipPace = (FlipPaceFn)Bind(module, "vws_flip_pace", typeof(FlipPaceFn));
                _flipPaceStatus = (FlipPaceStatusFn)Bind(module, "vws_flip_pace_status", typeof(FlipPaceStatusFn));
                _handLoad = (HandLoadFn)Bind(module, "vws_hand_load", typeof(HandLoadFn));
                _handPrefer = (HandPreferFn)Bind(module, "vws_hand_prefer", typeof(HandPreferFn));
                _handFull = (HandFullFn)Bind(module, "vws_hand_full", typeof(HandFullFn));
                _handMercury = (HandFullFn)Bind(module, "vws_hand_mercury", typeof(HandFullFn));
                _handCurls = (HandCurlsFn)Bind(module, "vws_hand_curls", typeof(HandCurlsFn));
                _resolveOptions = (ResolveOptionsFn)Bind(module, "vws_resolve_options", typeof(ResolveOptionsFn));
                _pushSteady = (PushSteadyFn)Bind(module, "vws_push_steady", typeof(PushSteadyFn));
                _downOptions = (DownOptionsFn)Bind(module, "vws_down_options", typeof(DownOptionsFn));
                _handError = (HandErrorFn)Bind(module, "vws_hand_error", typeof(HandErrorFn));
                _handConfigure = (HandConfigureFn)Bind(module, "vws_hand_configure", typeof(HandConfigureFn));
                _handRead = (HandReadFn)Bind(module, "vws_hand_read", typeof(HandReadFn));
                _pushRelease = (PushReleaseFn)Bind(module, "vws_push_release", typeof(PushReleaseFn));
                _poll = (PollFn)Bind(module, "vws_poll", typeof(PollFn));
                _drain = (DrainFn)Bind(module, "vws_drain_log", typeof(DrainFn));

                Loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                Problem = Dll + ".dll: " + ex.GetType().Name + " -- " + ex.Message;
                return false;
            }
        }

        // Hands a queued command to the render thread. Registrations and releases bind nothing;
        // the two passes draw, so Unity is told to forget what it believed was bound (the passes
        // put the device back as they found it, and this covers whatever they cannot see).
        internal static void Issue(int id)
        {
            if (id >= 0)
            {
                GL.IssuePluginEvent(EventFunc, id);
            }
        }

        internal static void IssuePass(int id)
        {
            if (id >= 0)
            {
                GL.IssuePluginEvent(EventFunc, id);
                GL.InvalidateState();
            }
        }

        internal static int PushRegister(uint set, IntPtr frame, IntPtr proxy, IntPtr model, IntPtr result, uint which, uint srgbMask, uint token)
        {
            return _pushRegister(set, frame, proxy, model, result, which, srgbMask, token);
        }

        internal static int PushPass(bool resolve, uint set, int eyes, float strength, int mode, float[] window, float feather)
        {
            return _pushPass(resolve ? 1u : 0u, set, (uint)eyes, strength, (uint)mode, window, feather);
        }

        internal static int PushRegisterGuide(uint set, int index, IntPtr source, IntPtr copy, uint token)
        {
            return _pushRegisterGuide(set, (uint)index, source, copy, token);
        }

        internal static int PushGuide(uint set, int index, int eyes, float[] window, float[] previous, bool topDown)
        {
            return _pushGuide(set, (uint)index, (uint)eyes, window, previous, topDown ? 1u : 0u);
        }

        // How much sharper than its exact average the network's input is made when the frame is shrunk for it.
        // ...and by how much of the frame's pixels the network's raster is shifted in the next shrink pushed.
        internal static void DownOptions(float sharpen, float shiftX, float shiftY)
        {
            _downOptions(sharpen, shiftX, shiftY);
        }

        // Before a resolve of the same set: this pass's edit is blended into what was kept of it from
        // the frames before, carried by the model-size motion vectors (see Steady in the native half).
        // `full`: kept at the frame's size, this frame's shrink having been made shifted by shiftX, shiftY.
        internal static int PushSteady(uint set, int eyes, IntPtr motion, float take, bool topDown, float[] window, int pass, bool fresh, bool full, float shiftX, float shiftY, float own)
        {
            return _pushSteady(set, (uint)eyes, motion, take, topDown ? 1u : 0u, window, (uint)pass, fresh ? 1u : 0u, full ? 1u : 0u, shiftX, shiftY, own);
        }

        // How the resolve enlarges a smaller model's edit: plainly, or along the frame's own edges
        // and sharpened (see EditFollowing in the native half's shader).
        internal static void ResolveOptions(bool follow, float edge, float sharpen, float halo, bool outline)
        {
            _resolveOptions(follow ? 1u : 0u, edge, sharpen, halo, outline ? 1u : 0u);
        }

        // Sharpens that texture where it lies; `eyes` 2 for a double-wide stereo frame.
        internal static int PushSharpen(IntPtr texture, float strength, int eyes)
        {
            return _pushSharpen(texture, strength, (uint)eyes);
        }

        // The scene's depth into the depth target bound when the event runs (see SceneUi.cs): for a
        // camera's command buffer, not for Issue.
        internal static int PushDepthFill(IntPtr depth, float u0, float uw, bool flip, float slack, bool reversed)
        {
            return _pushDepthFill(depth, u0, uw, flip ? 1u : 0u, slack, reversed ? 1u : 0u);
        }

        internal static void DepthFillStatus(out uint done, out uint noTarget, out uint failed)
        {
            _depthFillStatus(out done, out noTarget, out failed);
        }

        // The passthrough pass and the camera worker behind it (see Passthrough.cs).
        internal static void CamConfigure(float[] values)
        {
            _camConfigure(values, (uint)values.Length);
        }

        internal static int CamStart(IntPtr getFrameBuffer, ulong handle, uint width, uint height)
        {
            return _camStart(getFrameBuffer, handle, width, height);
        }

        internal static void CamStop()
        {
            _camStop();
        }

        internal static void CamStatus(out uint frames, out uint uploads, out int error)
        {
            _camStatus(out frames, out uploads, out error);
        }

        internal static int PushPassthrough(IntPtr target, int eyes, int eye, bool srgb, bool topDown, float[] eyeToRoom)
        {
            return _pushPassthrough(target, (uint)eyes, (uint)eye, srgb ? 1u : 0u, topDown ? 1u : 0u, eyeToRoom);
        }

        internal static int PushMatte(IntPtr source, int eyes, int eye, bool srgb, bool topDown, float[] headPose, bool fromAlpha, IntPtr depth, float[] depthInfo)
        {
            return _pushMatte(source, (uint)eyes, (uint)eye, srgb ? 1u : 0u, topDown ? 1u : 0u, headPose, fromAlpha ? 1u : 0u, depth, depth != IntPtr.Zero ? depthInfo : null);
        }

        internal static void OverlayDepth(out float room, out float scene)
        {
            _overlayDepth(out room, out scene);
        }

        internal static bool DepthRead(byte[] rgba, out uint side, out uint micros)
        {
            return _depthRead(rgba, rgba != null ? (uint)rgba.Length : 0u, out side, out micros) != 0;
        }

        // The render event that switches foveated shading on (or off) for what is drawn next.
        internal static int FoveaEvent(bool on)
        {
            return _foveaEvent(on ? 1u : 0u);
        }

        internal static void FoveaConfigure(float[] values, IntPtr anyTexture)
        {
            _foveaConfigure(values, (uint)values.Length, anyTexture);
        }

        internal static void FoveaStatus(out int state, out uint width, out uint height, out uint samples, out uint coarse, out uint ons)
        {
            _foveaStatus(out state, out width, out height, out samples, out coarse, out ons);
        }

        // "On" for the camera with this number (0: the scene's), for its opaque pass or another.
        internal static int FoveaEventFor(int camera, bool opaque)
        {
            return _foveaEventFor((uint)camera, opaque ? 1u : 0u);
        }

        // Four numbers each: the opaque pass with the rates and without, then the transparent pass.
        // A mark for the card's clock between two parts of the frame (see Profile): where span `index` begins or ends.
        internal static void Span(int index, bool begin)
        {
            if (Loaded && EventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(EventFunc, _spanEvent((uint)index, begin ? 1u : 0u));
            }
        }

        internal static void Spans(ulong[] micros, uint[] times, bool reset)
        {
            _spans(micros, times, reset ? 1u : 0u);
        }

        // The card's time over the same four: microseconds summed, and how many passes in each sum.
        internal static void FoveaTimed(ulong[] micros, uint[] times)
        {
            _foveaTimed(micros, times);
        }

        internal static void FoveaMeasured(ulong[] runs, uint[] times)
        {
            _foveaMeasured(runs, times);
        }

        internal static void FoveaAsked(out uint asked, out uint noDevice)
        {
            _foveaAsked(out asked, out noDevice);
        }

        internal static void FoveaMisses(out uint noTarget, out uint small, out uint byDepth)
        {
            _foveaMisses(out noTarget, out small, out byDepth);
        }

        // What became of the flip-model window the preloader patcher armed (vws_flip.h).
        internal static void FlipStatus(out int state, out uint width, out uint height, out uint presents, out uint tearing)
        {
            _flipStatus(out state, out width, out height, out presents, out tearing);
        }

        // Generated frames spread evenly by the flip-model window's own queue.
        internal static void FlipPace(bool on, int multiplier)
        {
            _flipPace(on ? 1 : 0, (uint)multiplier);
        }

        internal static void FlipPaceStatus(out float rate, out float refresh, out float each, out float queue, out uint dropped)
        {
            _flipPaceStatus(out rate, out refresh, out each, out queue, out dropped);
        }

        // ONNX Runtime and the hand models, from beside the native DLL. `full`: the full-size
        // landmark model instead of the small one; `mercury`: Mercury's keypoint network for
        // following a hand once it is found -- each where its file is there.
        internal static bool HandLoad(bool full, bool mercury)
        {
            _handPrefer((full ? 1u : 0u) | (mercury ? 2u : 0u));
            return _handLoad(IntPtr.Zero) != 0;
        }

        internal static bool HandMercury()
        {
            return _handMercury() != 0;
        }

        // How far each finger of each hand is bent, by Mercury: 12 floats, six a hand in HandRead's order.
        internal static void HandCurls(float[] curls)
        {
            _handCurls(curls);
        }

        internal static bool HandFull()
        {
            return _handFull() != 0;
        }

        internal static string HandError()
        {
            byte[] text = new byte[256];
            _handError(text, (uint)text.Length);
            int length = Array.IndexOf(text, (byte)0);
            return System.Text.Encoding.ASCII.GetString(text, 0, length < 0 ? text.Length : length);
        }

        internal static void HandConfigure(bool on, int every, float[] values)
        {
            _handConfigure(on ? 1u : 0u, (uint)Math.Max(1, every), values, values != null ? (uint)values.Length : 0u);
        }

        internal static bool HandRead(float[] hands, out uint serial, out uint micros, out float ageMs, out uint inRoom)
        {
            return _handRead(hands, (uint)hands.Length, out serial, out micros, out ageMs, out inRoom) != 0;
        }

        internal static bool CamRead(byte[] grey, out uint width, out uint height)
        {
            return _camRead(grey, (uint)grey.Length, out width, out height) != 0;
        }

        // The overlay's picture as RGBA bytes: asks for the next one drawn, returns the last one kept.
        internal static bool OverlayRead(byte[] rgba, out uint width, out uint height)
        {
            return _overlayRead(rgba, (uint)rgba.Length, out width, out height) != 0;
        }

        internal static void OverlayStatus(out int state, out uint frames, out int error)
        {
            _overlayStatus(out state, out frames, out error);
        }

        internal static uint Logs(uint off, out uint writes, out uint kilobytes)
        {
            return _logs(off, out writes, out kilobytes);
        }

        internal static uint OverlayLost()
        {
            return _overlayLost(0u, IntPtr.Zero);
        }

        internal static void ColourStatus(out int state, out uint pictures, out uint micros)
        {
            _colourStatus(out state, out pictures, out micros);
        }

        internal static string ColourError()
        {
            byte[] text = new byte[256];
            _colourError(text, (uint)text.Length);
            int length = Array.IndexOf(text, (byte)0);
            return System.Text.Encoding.ASCII.GetString(text, 0, length < 0 ? text.Length : length);
        }

        internal static int PushRelease(uint set)
        {
            return _pushRelease(set);
        }

        internal static void Poll(uint set, out int ready, out int error, out uint token, out int lastHr)
        {
            _poll(set, out ready, out error, out token, out lastHr);
        }

        internal static string DrainLog()
        {
            int n = _drain(_logBuffer, _logBuffer.Length);
            return n > 0 ? Encoding.ASCII.GetString(_logBuffer, 0, n) : null;
        }

        internal static string ErrorName(int error)
        {
            switch (error)
            {
                case 0: return "none";
                case -1: return "a texture pointer was null or not a texture";
                case -2: return "a texture has a shape the passes cannot use";
                case -3: return "a view of a texture could not be created";
                case -4: return "the D3D11 pipeline objects could not be created";
                default: return "error " + error;
            }
        }
    }

    // Where each eye is looking, from SteamVR.
    //
    // Any headset whose driver gives SteamVR eye tracking -- PlayStation VR2 through PSVR2Toolkit
    // among them -- has it answered by one call, IVRSystem::GetEyeTrackedFoveationCenter: a point
    // per eye, in that eye's own projection. It is the call other foveation tools use, and it needs
    // nothing of the headset's maker. VaM is an OpenVR application of 2018 and its openvr_api.dll
    // knows nothing of this, but that DLL only passes interface requests on to the installed
    // runtime: asked for the present-day interface by name, it hands back the runtime's own table.
    internal static class Gaze
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetGenericInterfaceFn(IntPtr version, out int error);

        [StructLayout(LayoutKind.Sequential)]
        private struct Ndc
        {
            public float X, Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool FoveationCentreFn(out Ndc left, out Ndc right);

        // The layout of a named interface never changes, which is the point of naming them: in
        // IVRSystem_026's function table GetEyeTrackedFoveationCenter is the thirty-sixth entry.
        private const string Interface = "FnTable:IVRSystem_026";
        private const int Slot = 35;

        private static GetGenericInterfaceFn _getInterface;
        private static IntPtr _interfaceName = IntPtr.Zero;
        private static IntPtr _fnPointer = IntPtr.Zero;
        private static FoveationCentreFn _fn;
        private static float _retryAt;
        private static int _frame = -1;
        private static bool _valid;
        private static readonly float[] _last = new float[4];

        internal static string Status = "not asked for yet";

        // {left u, left v, right u, right v}: where each eye looks as a fraction of that eye's
        // picture, u from the left and v UP from the bottom. False when SteamVR has nothing to say.
        // Asked once per frame however many callers there are; `frame` and `now` are the caller's
        // (Unity's frame count and unscaled time), so that this class needs nothing of Unity's.
        internal static bool Read(float[] uv, int frame, float now)
        {
            if (frame != _frame)
            {
                _frame = frame;
                _now = now;
                _valid = Ask();
            }

            if (_valid)
            {
                Array.Copy(_last, uv, 4);
            }

            return _valid;
        }

        private static float _now;

        private static bool Later(string why, float seconds)
        {
            Status = why;
            _retryAt = _now + seconds;
            return false;
        }

        private static bool Ask()
        {
            if (_now < _retryAt)
            {
                return false;
            }

            try
            {
                if (_getInterface == null)
                {
                    IntPtr module = GetModuleHandleW("openvr_api.dll");

                    if (module == IntPtr.Zero)
                    {
                        return Later("this is not a SteamVR session", 5f);
                    }

                    IntPtr address = GetProcAddress(module, "VR_GetGenericInterface");

                    if (address == IntPtr.Zero)
                    {
                        return Later("VaM's openvr_api.dll has no VR_GetGenericInterface", 60f);
                    }

                    _interfaceName = Marshal.StringToHGlobalAnsi(Interface);
                    _getInterface = (GetGenericInterfaceFn)Marshal.GetDelegateForFunctionPointer(address, typeof(GetGenericInterfaceFn));
                }

                // Asked for afresh each frame. It is a lookup, and a table kept from before the
                // runtime went away would be a table of calls into nothing.
                int error;
                IntPtr table = _getInterface(_interfaceName, out error);

                if (table == IntPtr.Zero)
                {
                    return Later(error == 0 ? "SteamVR gave no interface" : "this SteamVR has no eye-tracked foveation (it does not know IVRSystem_026) -- update SteamVR", 5f);
                }

                IntPtr fn = Marshal.ReadIntPtr(table, Slot * IntPtr.Size);

                if (fn == IntPtr.Zero)
                {
                    return Later("SteamVR's interface has no eye-tracked foveation call", 5f);
                }

                if (fn != _fnPointer || _fn == null)
                {
                    _fn = (FoveationCentreFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(FoveationCentreFn));
                    _fnPointer = fn;
                }

                Ndc left, right;

                if (!_fn(out left, out right))
                {
                    Status = "SteamVR has no gaze for this headset right now";
                    return false;
                }

                // NaN fails every comparison, so this is also the test for it.
                if (!(Mathf.Abs(left.X) <= 1.5f && Mathf.Abs(left.Y) <= 1.5f && Mathf.Abs(right.X) <= 1.5f && Mathf.Abs(right.Y) <= 1.5f))
                {
                    Status = "SteamVR's gaze is out of range";
                    return false;
                }

                _last[0] = 0.5f + 0.5f * left.X;
                _last[1] = 0.5f + 0.5f * left.Y;
                _last[2] = 0.5f + 0.5f * right.X;
                _last[3] = 0.5f + 0.5f * right.Y;
                Status = "tracked";
                return true;
            }
            catch (Exception ex)
            {
                _getInterface = null;
                _fn = null;
                _fnPointer = IntPtr.Zero;
                return Later("reading it failed (" + ex.GetType().Name + ")", 30f);
            }
        }
    }

    // Turns what the eye tracker says into where the window is aimed. A window that chased every
    // sample would never sit still -- an eye is never still, and a tracker less so -- and each
    // move costs the network the strip of picture that enters the window. So the aim stays put
    // while the gaze is within a dead zone of it and goes to the gaze when it leaves; and when the
    // tracker has nothing to say (a blink) the aim is held for a moment, then let back to where a
    // window without eye tracking sits. Pure arithmetic on what it is given, the time included.
    internal sealed class GazeFilter
    {
        internal const int AtRest = 0, Following = 1, Holding = 2, Returning = 3;

        internal float DeadZone = 0.03f;
        internal float Hold = 0.4f;
        internal float Return = 0.3f;

        // How the aim leaves the dead zone. An eye jumps, and is blind while it does: the aim goes
        // straight to where it landed. A figure on a monitor does not jump and nobody is blind to
        // it: there the aim glides, moved only as far as keeps the target inside the dead zone,
        // the way a camera follows someone walking.
        internal bool Glide;

        private readonly float[] _aim = new float[4];
        private readonly float[] _from = new float[4];
        private bool _have;
        private float _lostAt = -1f;

        internal void Reset()
        {
            _have = false;
            _lostAt = -1f;
        }

        // `gaze` and `rest` are {left u, left v, right u, right v}; `aim` receives the same.
        internal int Update(float now, bool valid, float[] gaze, float[] rest, float[] aim)
        {
            int state;

            if (valid)
            {
                if (!_have || _lostAt >= 0f)
                {
                    Array.Copy(gaze, _aim, 4);
                }
                else if (Glide)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _aim[i] = gaze[i] - Mathf.Clamp(gaze[i] - _aim[i], -DeadZone, DeadZone);
                    }
                }
                else
                {
                    bool moved = false;

                    for (int i = 0; i < 4 && !moved; i++)
                    {
                        moved = Mathf.Abs(gaze[i] - _aim[i]) > DeadZone;
                    }

                    if (moved)
                    {
                        Array.Copy(gaze, _aim, 4);
                    }
                }

                _have = true;
                _lostAt = -1f;
                state = Following;
            }
            else if (!_have)
            {
                Array.Copy(rest, _aim, 4);
                state = AtRest;
            }
            else
            {
                if (_lostAt < 0f)
                {
                    _lostAt = now;
                    Array.Copy(_aim, _from, 4);
                }

                float gone = now - _lostAt;

                if (gone <= Hold)
                {
                    state = Holding;
                }
                else
                {
                    float t = Return > 0f ? Mathf.Clamp01((gone - Hold) / Return) : 1f;

                    for (int i = 0; i < 4; i++)
                    {
                        _aim[i] = _from[i] + (rest[i] - _from[i]) * t;
                    }

                    state = Returning;

                    if (t >= 1f)
                    {
                        _have = false;
                        _lostAt = -1f;
                        state = AtRest;
                    }
                }
            }

            Array.Copy(_aim, aim, 4);
            return state;
        }
    }

    // The monitor's window when it is to FIT the people in view: where it is, how big, what shape.
    //
    // The network's raster is not the window. It has a size of its own, and the window is drawn
    // into it at whatever scale that takes -- so the window can grow and shrink and move from
    // frame to frame for nothing, and what the network costs stays what the settings chose. A
    // small figure gets the network's pixels one for one; a figure that fills the screen gets
    // them spread thinner. What cannot change for nothing is the raster's SHAPE: it has to match
    // the window's, or the network would be shown a squashed picture, and changing it rebuilds
    // the network (a fifth of a second's hitch). So there are three shapes -- upright, square,
    // wide -- of the same area, and one is left for another only when the figures have clearly
    // stopped fitting it and it has been in use a while.
    //
    // Pure arithmetic on what it is given, the time included; all sizes in frame pixels, y up.
    internal sealed class FitTracker
    {
        internal const int AtRest = GazeFilter.AtRest, Fitting = GazeFilter.Following, Holding = GazeFilter.Holding, Returning = GazeFilter.Returning;

        // Width over height of the three shapes.
        internal static readonly float[] Aspects = { 0.6f, 1f, 1f / 0.6f };

        internal float Hold = 0.75f;
        internal float Return = 0.75f;
        internal float Slack = 1.15f;  // how much room to spare before the window shrinks
        internal float Dwell = 2f;     // how long a shape is kept at the least

        // While the picture moves fast (the camera swung with the keys: the figures cross the
        // screen, change size, leave and come back) nothing about them is worth a new shape:
        // each one is a rebuild, a fifth of a second, and the next is already on its way -- which
        // was a stutter for as long as the movement lasted. So a shape is kept while the figures'
        // box moves faster than Fast (screen widths a second) and for a moment after, and a
        // figure lost for under Regain seconds is not a new figure. And where the window was
        // pushed along by the box's leading edge, its faded edge lay on the figure's outline for
        // as long as it moved: while moving, the window is drawn Lead larger and held around the
        // box's middle instead.
        //
        // That was not enough (a session's log: four rebuilds, 150 to 190 ms each, while the camera
        // was flown about with the keys): flying towards a figure or around it hardly moves its
        // box across the screen, it changes the box's SIZE and proportions -- and each new
        // proportion was a new shape as soon as the last one's two seconds were up. So a new
        // shape now waits until the box has been calm -- neither its middle nor its size changing
        // by more than Calm screen widths a second -- for CalmFor seconds: while the view is being
        // changed nothing is rebuilt, and when it comes to rest the shape is chosen once.
        internal float Fast = 0.35f;
        internal float Calm = 0.04f;
        internal float CalmFor = 1f;
        internal float Regain = 1f;
        internal float Lead = 1f;      // 0: pushed along no further than it must, as when at rest

        internal int Shape = 1;
        internal float CentreX, CentreY, Zoom = 1f;

        private bool _have;
        private float _lostAt = -1f;
        private float _fromX, _fromY, _fromZoom;
        private float _shapeSince = -1e9f;
        private bool _haveBox;
        private float _boxX, _boxY, _boxW, _boxH, _boxAt, _fastUntil = -1e9f, _activeAt = -1e9f, _leading;

        internal bool Moving(float now)
        {
            return now < _fastUntil;
        }

        internal void Reset()
        {
            _have = false;
            _lostAt = -1f;
            _haveBox = false;
            _leading = 0f;
        }

        // The shape's size in pixels for an area of `area` pixels, inside a w x h frame.
        internal static void ShapeSize(int shape, float area, int w, int h, out int shapeW, out int shapeH)
        {
            float aspect = Aspects[shape];
            shapeW = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(area * aspect)), 2, w);
            shapeH = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(area / aspect)), 2, h);
        }

        // How large the window of this shape would have to be drawn to hold a box, within what the
        // frame and the limits allow: its zoom, the window's area then (smaller is better -- the
        // same raster on fewer pixels), and how much of the box it would still leave outside.
        private static void Try(int shape, float boxW, float boxH, float area, int w, int h, float zoomMin, float zoomCap, out float zoom, out float windowArea, out float cut)
        {
            int shapeW, shapeH;
            ShapeSize(shape, area, w, h, out shapeW, out shapeH);
            float zoomMax = Mathf.Max(zoomMin, Mathf.Min(zoomCap, Mathf.Min(w / (float)shapeW, h / (float)shapeH)));
            zoom = Mathf.Clamp(Mathf.Max(boxW / shapeW, boxH / shapeH), zoomMin, zoomMax);
            windowArea = shapeW * zoom * (shapeH * zoom);
            cut = Mathf.Max(0f, boxW - shapeW * zoom) / boxW + Mathf.Max(0f, boxH - shapeH * zoom) / boxH;
        }

        // The shape for this box: the one that holds all of it in the smallest window. The one in
        // use is left only for a shape that is clearly better -- it cuts off less, or is a fifth
        // smaller -- so that a box near the boundary does not flip the shape back and forth.
        private int ShapeFor(float boxW, float boxH, float area, int w, int h, float zoomMin, float zoomCap, bool fresh)
        {
            int best = Shape;
            float zoom, bestArea, bestCut, currentArea, currentCut;
            Try(Shape, boxW, boxH, area, w, h, zoomMin, zoomCap, out zoom, out currentArea, out currentCut);
            bestArea = currentArea;
            bestCut = currentCut;

            for (int s = 0; s < Aspects.Length; s++)
            {
                float a, c;
                Try(s, boxW, boxH, area, w, h, zoomMin, zoomCap, out zoom, out a, out c);

                if (c < bestCut - 0.005f || (c <= bestCut + 0.005f && a < bestArea))
                {
                    best = s;
                    bestArea = a;
                    bestCut = c;
                }
            }

            if (best == Shape || fresh)
            {
                return best;
            }

            return bestCut < currentCut - 0.02f || bestArea < currentArea * 0.8f ? best : Shape;
        }

        // `box` is {x0, y0, x1, y1}, what has to be inside the window; `area` the pixels the
        // window's shape is to have at zoom 1; `zoomMin` how far it may shrink below that (to
        // where the raster would be larger than the window) and `zoomCap` how far it may grow
        // beyond what the frame allows anyway. Sets Shape, CentreX/Y and Zoom.
        internal int Update(float now, bool valid, float[] box, float area, int w, int h, float zoomMin, float zoomCap)
        {
            int shapeW, shapeH, state;

            if (valid)
            {
                float boxW = Mathf.Max(box[2] - box[0], 1f), boxH = Mathf.Max(box[3] - box[1], 1f);
                float midX = (box[0] + box[2]) * 0.5f, midY = (box[1] + box[3]) * 0.5f;
                bool fresh = !_have || _lostAt >= 0f;
                bool anew = !_have || (_lostAt >= 0f && now - _lostAt > Regain);
                float step = 0f;

                if (_haveBox && now > _boxAt)
                {
                    step = Mathf.Min(now - _boxAt, 0.1f);
                    float dx = midX - _boxX, dy = midY - _boxY;
                    float speed = Mathf.Sqrt(dx * dx + dy * dy) / (now - _boxAt);
                    float growth = (Mathf.Abs(boxW - _boxW) + Mathf.Abs(boxH - _boxH)) / (now - _boxAt);

                    if (speed > Fast * w)
                    {
                        _fastUntil = now + 0.6f;
                    }

                    if (speed > Calm * w || growth > Calm * w)
                    {
                        _activeAt = now;
                    }
                }

                _haveBox = true;
                _boxX = midX;
                _boxY = midY;
                _boxW = boxW;
                _boxH = boxH;
                _boxAt = now;
                bool moving = now < _fastUntil;
                _leading = Mathf.MoveTowards(_leading, moving ? 1f : 0f, step * (moving ? 6f : 2f));
                float lead = _leading * Mathf.Clamp01(Lead);

                int shape = ShapeFor(boxW, boxH, area, w, h, zoomMin, zoomCap, anew);

                if (shape != Shape && !moving && (anew || (now - _activeAt >= CalmFor && now - _shapeSince >= Dwell)))
                {
                    Shape = shape;
                    _shapeSince = now;
                }

                ShapeSize(Shape, area, w, h, out shapeW, out shapeH);
                float zoomMax = Mathf.Max(zoomMin, Mathf.Min(zoomCap, Mathf.Min(w / (float)shapeW, h / (float)shapeH)));
                float need = Mathf.Clamp(Mathf.Max(boxW / shapeW, boxH / shapeH) * (1f + 0.15f * lead), zoomMin, zoomMax);

                if (fresh)
                {
                    Zoom = need;
                    CentreX = (box[0] + box[2]) * 0.5f;
                    CentreY = (box[1] + box[3]) * 0.5f;
                }
                else if (need > Zoom || need * Slack < Zoom)
                {
                    // Grown at once -- nobody is to be cut off -- and shrunk only when there is
                    // clearly room to spare.
                    Zoom = need;
                }

                Zoom = Mathf.Clamp(Zoom, zoomMin, zoomMax);

                // Moved no further than it takes to keep the box inside; a box too big for the
                // window is centred in it.
                float winW = shapeW * Zoom, winH = shapeH * Zoom;
                CentreX = winW >= boxW ? Mathf.Clamp(CentreX, box[2] - winW * 0.5f, box[0] + winW * 0.5f) : (box[0] + box[2]) * 0.5f;
                CentreY = winH >= boxH ? Mathf.Clamp(CentreY, box[3] - winH * 0.5f, box[1] + winH * 0.5f) : (box[1] + box[3]) * 0.5f;

                // (moving fast: around the box's middle, so that its edges are as far from the window's as they can be)
                CentreX += (midX - CentreX) * lead;
                CentreY += (midY - CentreY) * lead;

                _have = true;
                _lostAt = -1f;
                state = Fitting;
            }
            else
            {
                // Nobody in view: the shape stays (changing it costs a rebuild and gains nothing),
                // the window goes back to the middle at its plain size -- after a moment, in case
                // they are only passing behind something.
                ShapeSize(Shape, area, w, h, out shapeW, out shapeH);
                float restZoom = Mathf.Clamp(1f, zoomMin, Mathf.Max(zoomMin, Mathf.Min(zoomCap, Mathf.Min(w / (float)shapeW, h / (float)shapeH))));

                if (!_have)
                {
                    CentreX = w * 0.5f;
                    CentreY = h * 0.5f;
                    Zoom = restZoom;
                    state = AtRest;
                }
                else
                {
                    if (_lostAt < 0f)
                    {
                        _lostAt = now;
                        _fromX = CentreX;
                        _fromY = CentreY;
                        _fromZoom = Zoom;
                    }

                    float gone = now - _lostAt;

                    if (gone <= Hold)
                    {
                        state = Holding;
                    }
                    else
                    {
                        float t = Return > 0f ? Mathf.Clamp01((gone - Hold) / Return) : 1f;
                        CentreX = _fromX + (w * 0.5f - _fromX) * t;
                        CentreY = _fromY + (h * 0.5f - _fromY) * t;
                        Zoom = _fromZoom + (restZoom - _fromZoom) * t;
                        state = Returning;

                        if (t >= 1f)
                        {
                            _have = false;
                            _lostAt = -1f;
                            state = AtRest;
                        }
                    }
                }
            }

            return state;
        }
    }

    // Where the person is on a monitor's picture -- the nearest thing to a gaze a monitor has.
    //
    // Neural Rendering is about skin and faces, and on a monitor that is where one looks. A VaM
    // Person carries named controls on its head, chest and hips; those, through the camera, say
    // where the figure is on screen. With several people the one nearest the middle is taken, and
    // kept unless another is clearly nearer, so the window does not flit between them.
    internal static class Subject
    {
        private sealed class Person
        {
            internal Atom Atom;
            internal Transform Head, Chest, Hip;

            // Head, chest and hips again, and what sticks out from them: hands, feet, knees, elbows.
            internal readonly Transform[] Points = new Transform[Parts.Length];
        }

        private static readonly string[] Parts =
        {
            "headControl", "chestControl", "hipControl", "lHandControl", "rHandControl", "lFootControl", "rFootControl",
            "lKneeControl", "rKneeControl", "lElbowControl", "rElbowControl",
        };

        private static readonly List<Person> _people = new List<Person>();
        private static float _nextScan;
        private static Atom _chosen;

        internal static string Status = "";

        private static Transform ControlOf(Atom atom, string id)
        {
            JSONStorable control = atom.GetStorableByID(id);
            return control != null ? control.transform : null;
        }

        // {x0, y0, x1, y1} in frame pixels, y up: the part of a w x h picture that the people in
        // view take up -- all of them, since a window that fits one and cuts another in half is
        // worse than a larger one -- with room for what the control points do not reach (hair,
        // fingers, shoulders). False when nobody is in view.
        internal static bool FindBox(Camera camera, float now, int w, int h, float[] box)
        {
            if (camera == null)
            {
                Status = "no camera";
                return false;
            }

            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                Scan();
            }

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool any = false;

            for (int i = 0; i < _people.Count; i++)
            {
                Person p = _people[i];

                if (p.Atom == null || p.Head == null || p.Hip == null || !p.Atom.on)
                {
                    continue;
                }

                float px0 = float.MaxValue, py0 = float.MaxValue, px1 = float.MinValue, py1 = float.MinValue;
                bool seen = false;

                for (int k = 0; k < p.Points.Length; k++)
                {
                    if (p.Points[k] == null)
                    {
                        continue;
                    }

                    Vector3 at = camera.WorldToViewportPoint(p.Points[k].position);

                    // Behind the camera a point projects to nonsense.
                    if (at.z <= 0.05f)
                    {
                        continue;
                    }

                    px0 = Mathf.Min(px0, at.x);
                    py0 = Mathf.Min(py0, at.y);
                    px1 = Mathf.Max(px1, at.x);
                    py1 = Mathf.Max(py1, at.y);
                    seen = true;
                }

                if (!seen || px1 < 0f || px0 > 1f || py1 < 0f || py0 > 1f)
                {
                    continue;
                }

                // Room all round, by the figure's own measure: head to hips is about 65 cm, and a
                // fifth of that reaches the shoulders, the fingertips and the top of the head --
                // with as much again above, for hair.
                Vector3 head = camera.WorldToViewportPoint(p.Head.position);
                Vector3 hip = camera.WorldToViewportPoint(p.Hip.position);
                float dx = (head.x - hip.x) * camera.aspect, dy = head.y - hip.y;
                float reach = head.z > 0.05f && hip.z > 0.05f ? Mathf.Sqrt(dx * dx + dy * dy) : (py1 - py0) * 0.4f;
                float pad = Mathf.Clamp(reach * 0.25f, 0.01f, 0.25f);

                x0 = Mathf.Min(x0, px0 - pad / camera.aspect);
                x1 = Mathf.Max(x1, px1 + pad / camera.aspect);
                y0 = Mathf.Min(y0, py0 - pad);
                y1 = Mathf.Max(y1, py1 + pad * 2f);
                any = true;
            }

            if (!any)
            {
                Status = _people.Count == 0 ? "no person in the scene" : "nobody in view";
                return false;
            }

            box[0] = Mathf.Clamp01(x0) * w;
            box[1] = Mathf.Clamp01(y0) * h;
            box[2] = Mathf.Clamp01(x1) * w;
            box[3] = Mathf.Clamp01(y1) * h;
            Status = "fitting";
            return true;
        }

        private static void Scan()
        {
            _people.Clear();
            SuperController vam = SuperController.singleton;

            if (vam == null)
            {
                return;
            }

            List<Atom> atoms = vam.GetAtoms();

            for (int i = 0; atoms != null && i < atoms.Count; i++)
            {
                Atom atom = atoms[i];

                if (atom == null || !atom.on || atom.type != "Person")
                {
                    continue;
                }

                Person p = new Person();
                p.Atom = atom;

                for (int k = 0; k < Parts.Length; k++)
                {
                    p.Points[k] = ControlOf(atom, Parts[k]);
                }

                p.Head = p.Points[0];
                p.Chest = p.Points[1];
                p.Hip = p.Points[2];

                if (p.Head != null && p.Hip != null)
                {
                    _people.Add(p);
                }
            }
        }

        // {u, v, u, v}: where to aim a window `windowHeight` of the picture tall, as fractions of
        // the picture, v upwards. False when nobody is in view.
        internal static bool Find(Camera camera, float now, float windowHeight, float[] uv)
        {
            if (camera == null)
            {
                Status = "no camera";
                return false;
            }

            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                Scan();
            }

            Person best = null;
            Vector3 bestHead = Vector3.zero, bestHip = Vector3.zero;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _people.Count; i++)
            {
                Person p = _people[i];

                // An atom removed since the last scan: its transforms went with it.
                if (p.Atom == null || p.Head == null || p.Hip == null || !p.Atom.on)
                {
                    continue;
                }

                Vector3 head = camera.WorldToViewportPoint(p.Head.position);
                Vector3 hip = camera.WorldToViewportPoint(p.Hip.position);

                if (head.z <= 0.05f || hip.z <= 0.05f)
                {
                    continue;
                }

                float x = (head.x + hip.x) * 0.5f, y = (head.y + hip.y) * 0.5f;

                if (x < -0.25f || x > 1.25f || y < -0.25f || y > 1.25f)
                {
                    continue;
                }

                float score = (x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f);

                if (ReferenceEquals(p.Atom, _chosen))
                {
                    score *= 0.5f;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                    bestHead = head;
                    bestHip = hip;
                }
            }

            if (best == null)
            {
                _chosen = null;
                Status = _people.Count == 0 ? "no person in the scene" : "nobody in view";
                return false;
            }

            _chosen = best.Atom;

            // Head to hips is about two fifths of a standing figure. One that fits the window is
            // centred in it, on the hips; one that does not -- a close-up -- is aimed at between
            // head and chest, since it is the face that has to be inside.
            float dx = (bestHead.x - bestHip.x) * camera.aspect, dy = bestHead.y - bestHip.y;
            float figure = Mathf.Sqrt(dx * dx + dy * dy) * 2.5f;
            float closeUp = Mathf.Clamp01((figure - windowHeight) / Mathf.Max(windowHeight, 0.05f));
            Vector3 upper = best.Chest != null ? (bestHead + camera.WorldToViewportPoint(best.Chest.position)) * 0.5f : bestHead;
            Vector3 aim = Vector3.Lerp(bestHip, upper, closeUp);

            uv[0] = uv[2] = Mathf.Clamp01(aim.x);
            uv[1] = uv[3] = Mathf.Clamp01(aim.y);
            Status = "following";
            return true;
        }
    }

    // What one NrCapture needs to run its network small: the reduced copy of the frame, the
    // full-size result, and the native set that holds the four textures the passes work on.
    public sealed class View
    {
        internal readonly NrCapture Capture;
        internal readonly uint Set;

        internal RenderTexture Down;
        internal RenderTexture Result;
        internal int FrameW, FrameH, WorkW, WorkH;
        internal int Eyes = 1;
        private int _steadyFrame = -1, _steadyPass;

        // The shift of the network's raster this frame, in the frame's pixels (see PSGather): for a
        // network at 1/n of the frame each way, n x n places half... a whole of the frame's pixels
        // apart, taken in turn, so that after n x n frames every one of the frame's pixels has
        // been at the middle of a network pixel once.
        internal float ShiftX, ShiftY;
        internal bool Detail;

        internal void Shift(bool wanted, int frameW, int workW)
        {
            int n = workW > 0 ? Mathf.RoundToInt(frameW / (float)workW) : 1;
            Detail = wanted && n >= 2 && n <= 4;
            ShiftX = ShiftY = 0f;

            if (!Detail)
            {
                return;
            }

            int phase = Time.frameCount % (n * n), x = phase % n, y = phase / n;

            // (for two by two: the diagonal first, so that two frames already cover both rows and both columns)
            if (n == 2)
            {
                x = phase == 1 || phase == 2 ? 1 : 0;
                y = phase == 1 || phase == 3 ? 1 : 0;
            }

            ShiftX = x - (n - 1) * 0.5f;
            ShiftY = y - (n - 1) * 0.5f;
        }
        private readonly int[] _steadyLast = { -100, -100, -100, -100 };
        private bool _steadyFull;
        internal bool Live;
        internal int LiveFrame = -1;
        internal float LastUsed;
        internal bool Broken;

        // The focus window, when the model is shown only part of each eye: where it is (origin and
        // size per eye, as fractions of the eye -- what the passes take), how wide the fade at its
        // edge is, and what a motion vector is worth more for the model's raster covering less.
        internal bool Windowed;
        internal readonly float[] Window = new float[8];
        internal float Feather;
        internal float GainX = 1f, GainY = 1f;
        internal float WindowFractionX = 1f, WindowFractionY = 1f;
        internal int WindowW, WindowH;

        // What the window is aimed at -- nothing in particular (it rests), the eye, or the person
        // on a monitor -- and how that is going (GazeFilter's states); and how far the window
        // moved since the last frame, x and y per eye in the motion vectors' own terms, which is
        // what has to come off them.
        internal const int AimAtRest = 0, AimAtGaze = 1, AimAtPerson = 2, AimFitPeople = 3;
        internal int AimMode;
        internal int GazeState;

        // A window that is not where, or what size, it was a frame ago: the window as it was (in
        // the same terms as Window), for the motion vectors, and which way up the frame is stored.
        internal bool Moved;
        internal bool TopDown;
        internal readonly float[] Previous = new float[8];

        private Camera _camera;
        private float _nextCamera;

        private readonly float[] _centre = new float[4];
        private readonly float[] _centreNow = new float[4];
        private bool _haveCentre;
        private float _nextCentre;

        private readonly GazeFilter _filter = new GazeFilter();
        private readonly FitTracker _fit = new FitTracker();
        private readonly float[] _gaze = new float[4];
        private readonly float[] _aim = new float[4];
        private readonly float[] _box = new float[4];
        private readonly float[] _lastWindow = new float[8];
        private bool _haveLast;
        private int _lastFrame = -1;
        private int _lastKey;

        // Which of the three shapes a fitted window has, for the status line.
        internal int FitShape
        {
            get { return _fit.Shape; }
        }

        // VamDlssNr's guides, as met inside its method: what it has and the copy the network gets.
        private readonly Texture[] _guideSrc = new Texture[3];
        private readonly RenderTexture[] _guideDst = new RenderTexture[3];
        private readonly IntPtr[] _pGuideSrc = new IntPtr[3];
        private readonly IntPtr[] _pGuideDst = new IntPtr[3];
        private readonly bool[] _sentGuide = new bool[3];
        private int _guideIndex;

        private static uint _tokens;

        private Texture _regFrame;
        private RenderTexture _regOutput;
        private IntPtr _pFrame, _pDown, _pOutput, _pResult;
        private bool _inDirty = true, _outDirty = true;
        private bool _sentIn, _sentOut;
        private bool _stereo;
        private bool _recheck;
        private uint _token;
        private int _behind;
        private float _nextCheck;
        private int _screenW, _screenH;
        private bool _fullScreen;

        internal View(NrCapture capture, uint set)
        {
            Capture = capture;
            Set = set;
        }

        private static RenderTexture Make(int w, int h, bool stereo, string name)
        {
            // The same texture VamDlssNr makes for the network's own input and output, so every blit
            // it does from or to these behaves exactly as it does with its own.
            RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.name = name;
            rt.filterMode = FilterMode.Point;

            if (stereo)
            {
                rt.vrUsage = VRTextureUsage.TwoEyes;
            }

            rt.Create();
            return rt;
        }

        private static void Drop(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }

            rt = null;
        }

        // Unity replaces a render texture's native resource behind the managed object on a few
        // events (a fullscreen switch, a device reset), and a native pointer is a sync with the
        // render thread, so it is asked for on a cadence rather than every frame -- the same
        // cadence and the same triggers VamDlssNr uses for its own registrations.
        private bool RecheckDue()
        {
            bool due = Time.unscaledTime >= _nextCheck || Screen.width != _screenW || Screen.height != _screenH || Screen.fullScreen != _fullScreen;

            if (due)
            {
                _nextCheck = Time.unscaledTime + 0.5f;
                _screenW = Screen.width;
                _screenH = Screen.height;
                _fullScreen = Screen.fullScreen;
            }

            return due;
        }

        // False when the native side refused something it was sent. While it has not caught up with
        // the last registration the answer is "nothing known against it", which is the usual state
        // for a frame or two after one.
        private bool NativeHealthy()
        {
            if (_token == 0)
            {
                return true;
            }

            int ready, error, hr;
            uint token;
            Native.Poll(Set, out ready, out error, out token, out hr);

            if (token != _token)
            {
                // A registration that never arrives was lost with the set (a device change does
                // that). Send both halves again rather than waiting on it for ever.
                if (++_behind > 240)
                {
                    _behind = 0;
                    _inDirty = _outDirty = true;
                    _sentIn = _sentOut = false;
                    _sentGuide[0] = _sentGuide[1] = _sentGuide[2] = false;
                }

                return true;
            }

            _behind = 0;

            // It has seen everything sent, so what it reports is the verdict on it. The error alone
            // is not enough: it describes the last registration only, and a first half that was
            // refused is hidden by a second half that went through. What is ready is the whole truth.
            int expected = (_sentIn ? Native.ReadyDown : 0) | (_sentIn && _sentOut ? Native.ReadyResolve : 0);
            int guides = 0;

            for (int i = 0; i < 3; i++)
            {
                guides |= _sentGuide[i] ? (Native.ReadyGuide << i) : 0;
            }

            if (error == 0 && (ready & (expected | guides)) == (expected | guides))
            {
                return true;
            }

            Hooks.Fail(this, error != 0
                ? Native.ErrorName(error) + (hr != 0 ? " (hr=0x" + hr.ToString("X8") + ")" : "")
                : "the " + ((ready & Native.ReadyDown) == 0 ? "frame or model input" : ((ready & expected) != expected ? "model output or result" : "motion, depth or mask")) + " texture was refused (the [vws native] lines in the log say why)");
            return false;
        }

        // The monitor's window when it is to fit the people in view: finds them, and decides the
        // window's place, size and shape for this frame. Called before the model's raster is
        // chosen, because the raster takes the window's shape; `area` is how many pixels that
        // shape has at its plain size, `scale` the model resolution. Gives the shape's size back.
        internal void PlanFit(NrCapture capture, int w, int h, float area, float scale, out int shapeW, out int shapeH)
        {
            float now = Time.unscaledTime;

            if (_camera == null || now >= _nextCamera)
            {
                _nextCamera = now + 1f;
                _camera = Hooks.CameraOf(capture);
            }

            if (AimMode != AimFitPeople)
            {
                _fit.Reset();
                _filter.Reset();
            }

            AimMode = AimFitPeople;

            // The window is never drawn smaller than the raster (the network would be shown an
            // enlargement), nor so much larger that the shrink would skip texels.
            float zoomMin = Mathf.Clamp(scale, Hooks.MinScale, 1f);
            GazeState = _fit.Update(now, Subject.FindBox(_camera, now, w, h, _box), _box, area, w, h, zoomMin, zoomMin * 8f);
            FitTracker.ShapeSize(_fit.Shape, area, w, h, out shapeW, out shapeH);
        }

        // Where the model's window is this frame: `winW` x `winH` frame pixels of each eye, kept
        // inside the eye. It rests on the lens centre in a headset and the middle of the screen on
        // a monitor, and is aimed from there at what is being looked at, where that can be known.
        // `fitted` says PlanFit has already decided this frame's window, of which winW x winH is
        // the shape at its plain size.
        internal void SetWindow(NrCapture capture, int w, int h, int eyes, int winW, int winH, float sizeX, float sizeY, bool fitted)
        {
            int eyeW = w / eyes;
            float now = Time.unscaledTime;

            // The centre is looked up once a second, and held unless it has really moved: a
            // projection that is jittered from frame to frame (as DLSS upscaling does) must not
            // walk the window about by a pixel, which would put the network's history out of step
            // with its input.
            if (!_haveCentre || now >= _nextCentre)
            {
                _nextCentre = now + 1f;
                _camera = Hooks.CameraOf(capture);
                Hooks.LensCentres(capture, eyes, _centreNow);
                bool moved = !_haveCentre;

                for (int i = 0; i < 4; i++)
                {
                    moved = moved || Mathf.Abs(_centreNow[i] - _centre[i]) > 0.01f;
                }

                if (moved)
                {
                    Array.Copy(_centreNow, _centre, 4);
                    _haveCentre = true;
                }
            }

            float[] aim = _centre;

            if (fitted)
            {
                // The shape, drawn as much larger as fits everyone in; what the status line is
                // told is what that comes to of the screen.
                winW = Mathf.Clamp(Mathf.RoundToInt(winW * _fit.Zoom), 2, eyeW);
                winH = Mathf.Clamp(Mathf.RoundToInt(winH * _fit.Zoom), 2, h);
                _aim[0] = _aim[2] = _fit.CentreX / w;
                _aim[1] = _aim[3] = _fit.CentreY / h;
                aim = _aim;
                sizeX = winW / (float)eyeW;
                sizeY = winH / (float)h;
            }
            else
            {
                // In a headset with eye tracking the window goes where the eye looks. On a monitor
                // nobody says where the eye is, but what it is on is not hard to guess: the person.
                int mode = AimAtRest;

                if (eyes == 2 && Hooks.CfgWindowGaze != null && Hooks.CfgWindowGaze.Value)
                {
                    mode = AimAtGaze;
                }
                else if (eyes == 1 && Hooks.CfgMonitorFollow != null && Hooks.CfgMonitorFollow.Value)
                {
                    mode = AimAtPerson;
                }

                if (mode != AimMode)
                {
                    _filter.Reset();
                    _fit.Reset();
                }

                AimMode = mode;
                GazeState = GazeFilter.AtRest;

                if (mode == AimAtGaze)
                {
                    _filter.Glide = false;
                    _filter.DeadZone = Hooks.CfgGazeDeadZone.Value;
                    _filter.Hold = Hooks.CfgGazeHold.Value;
                    _filter.Return = Hooks.CfgGazeReturn.Value;
                    GazeState = _filter.Update(now, Gaze.Read(_gaze, Time.frameCount, now), _gaze, _centre, _aim);
                    aim = _aim;
                }
                else if (mode == AimAtPerson)
                {
                    _filter.Glide = true;
                    _filter.DeadZone = Hooks.CfgMonitorDeadZone.Value;
                    _filter.Hold = 0.75f;
                    _filter.Return = 0.75f;
                    GazeState = _filter.Update(now, Subject.Find(_camera, now, sizeY, _gaze), _gaze, _centre, _aim);
                    aim = _aim;
                }
            }

            // The centres are fractions of the picture, v upwards. The frame this works on is
            // VamDlssNr's copy of the camera's, which it stores top row first when its FlipY is on
            // (the default: the network is to see the picture upright) and bottom row first when
            // it is off.
            bool topDown = Hooks.NetIsTopDown();

            for (int e = 0; e < 2; e++)
            {
                float v = aim[e * 2 + 1];
                int x0 = Mathf.Clamp(Mathf.RoundToInt(aim[e * 2] * eyeW - winW * 0.5f), 0, eyeW - winW);
                int y0 = Mathf.Clamp(Mathf.RoundToInt((topDown ? 1f - v : v) * h - winH * 0.5f), 0, h - winH);
                Window[e * 4] = x0 / (float)eyeW;
                Window[e * 4 + 1] = y0 / (float)h;
                Window[e * 4 + 2] = winW / (float)eyeW;
                Window[e * 4 + 3] = winH / (float)h;
            }

            // Not where, or not the size, it was a frame ago: the motion vectors are told what it
            // was, so that the network's history can follow the window (see PSGuide).
            int key = (eyeW * 31 + h) * 2 + (topDown ? 1 : 0);
            Moved = false;

            if (_haveLast && key == _lastKey && Time.frameCount - _lastFrame <= 2)
            {
                for (int i = 0; i < 8; i++)
                {
                    Moved = Moved || Window[i] != _lastWindow[i];
                }

                if (Moved)
                {
                    Array.Copy(_lastWindow, Previous, 8);
                }
            }

            Array.Copy(Window, _lastWindow, 8);
            _haveLast = true;
            _lastFrame = Time.frameCount;
            _lastKey = key;
            TopDown = topDown;

            // A motion vector is a fraction of the whole eye. VamDlssNr turns it into model pixels
            // by the model raster's size, which now covers only the window.
            GainX = eyeW / (float)winW;
            GainY = h / (float)winH;
            Feather = Mathf.Clamp(Hooks.CfgWindowFeather != null ? Hooks.CfgWindowFeather.Value : 0.35f, 0.02f, 1f);
            WindowW = winW;
            WindowH = winH;
            WindowFractionX = sizeX;
            WindowFractionY = sizeY;
            Windowed = true;
        }

        internal void ClearWindow()
        {
            Windowed = false;
            GainX = GainY = 1f;
            WindowFractionX = WindowFractionY = 1f;
            Moved = false;
            AimMode = AimAtRest;
            GazeState = GazeFilter.AtRest;
            _haveLast = false;
            _filter.Reset();
            _fit.Reset();

            // The guides are VamDlssNr's again; let go of what was held of them.
            for (int i = 0; i < 3; i++)
            {
                if (_sentGuide[i])
                {
                    _token = NextToken();
                    Native.Issue(Native.PushRegisterGuide(Set, i, IntPtr.Zero, IntPtr.Zero, _token));
                    _sentGuide[i] = false;
                    _guideSrc[i] = null;
                    _guideDst[i] = null;
                }
            }
        }

        // One of VamDlssNr's guide copies, made inside its RunNeuralRendering in the order motion,
        // depth, mask. Cuts the window out of `source` into `dest`. False leaves the caller to make
        // the plain copy.
        internal bool CutGuide(Texture source, RenderTexture dest)
        {
            int i = _guideIndex++;

            if (i >= 3 || source == null || dest == null)
            {
                return false;
            }

            if (!_sentGuide[i] || _recheck || !ReferenceEquals(source, _guideSrc[i]) || !ReferenceEquals(dest, _guideDst[i]))
            {
                IntPtr pSource = source.GetNativeTexturePtr();
                IntPtr pDest = dest.GetNativeTexturePtr();

                if (pSource == IntPtr.Zero || pDest == IntPtr.Zero)
                {
                    return false;
                }

                if (!_sentGuide[i] || pSource != _pGuideSrc[i] || pDest != _pGuideDst[i])
                {
                    _token = NextToken();
                    Native.Issue(Native.PushRegisterGuide(Set, i, pSource, pDest, _token));
                    _pGuideSrc[i] = pSource;
                    _pGuideDst[i] = pDest;
                    _sentGuide[i] = true;
                }

                _guideSrc[i] = source;
                _guideDst[i] = dest;
            }

            // The motion vectors (the first guide) also lose the window's own movement.
            Native.IssuePass(Native.PushGuide(Set, i, Eyes, Window, i == 0 && Moved ? Previous : null, TopDown));
            return true;
        }

        private static uint SrgbBit(Texture t, uint bit)
        {
            RenderTexture rt = t as RenderTexture;
            return rt != null && rt.sRGB ? bit : 0u;
        }

        // Before VamDlssNr sets its evaluate up: our own targets, and the frame and model input
        // registered with the native side. False leaves the caller to run at full size.
        internal bool Prepare(Texture frame, int w, int h, int ww, int wh, int eyes)
        {
            bool stereo = eyes == 2;

            // A new size for either makes the other half's registration stale: the model output is
            // about to be rebuilt by VamDlssNr at the new size, and is sent again once it has been.
            if (Down == null || Down.width != ww || Down.height != wh || _stereo != stereo)
            {
                Drop(ref Down);
                Down = Make(ww, wh, stereo, "VdnWorkScale.ModelInput");
                _inDirty = true;
                _sentOut = false;
            }

            if (Result == null || Result.width != w || Result.height != h || _stereo != stereo)
            {
                Drop(ref Result);
                Result = Make(w, h, stereo, "VdnWorkScale.Result");
                _outDirty = true;
                _sentOut = false;
            }

            _stereo = stereo;

            if (!Down.IsCreated())
            {
                Down.Create();
                _inDirty = true;
            }

            if (!Result.IsCreated())
            {
                Result.Create();
                _outDirty = true;
            }

            if (!Down.IsCreated() || !Result.IsCreated())
            {
                Hooks.Fail(this, "a " + ww + "x" + wh + " or " + w + "x" + h + " render texture could not be created");
                return false;
            }

            if (!NativeHealthy())
            {
                return false;
            }

            _recheck = RecheckDue();

            if (_inDirty || _recheck || !ReferenceEquals(frame, _regFrame))
            {
                IntPtr pFrame = frame.GetNativeTexturePtr();
                IntPtr pDown = Down.GetNativeTexturePtr();

                if (pFrame == IntPtr.Zero || pDown == IntPtr.Zero)
                {
                    return false;
                }

                if (_inDirty || pFrame != _pFrame || pDown != _pDown)
                {
                    _token = NextToken();
                    Native.Issue(Native.PushRegister(Set, pFrame, pDown, IntPtr.Zero, IntPtr.Zero, Native.Frame | Native.Proxy,
                        SrgbBit(frame, Native.Frame) | SrgbBit(Down, Native.Proxy), _token));
                    _pFrame = pFrame;
                    _pDown = pDown;
                    _inDirty = false;
                    _sentIn = true;
                }

                _regFrame = frame;
            }

            FrameW = w;
            FrameH = h;
            WorkW = ww;
            WorkH = wh;
            Eyes = eyes;
            LastUsed = Time.unscaledTime;
            _guideIndex = 0;
            return true;
        }

        internal void Downsample()
        {
            float[] window = Windowed ? Window : null;

            // TestFlat is the self-test's: it replaces the model's input with a flat grey so the
            // timing of the model's answer can be read off the screen. Never set in normal use.
            if (Hooks.TestFlat >= 0f)
            {
                Native.IssuePass(Native.PushPass(false, Set, Eyes, Hooks.TestFlat, 9, window, 0f));
            }
            else
            {
                Native.IssuePass(Native.PushPass(false, Set, Eyes, 1f, 0, window, 0f));
            }
        }

        // The guides -- motion vectors, depth and the control mask, resampled to the model's size --
        // are VamDlssNr's own textures, and it lets go of them every frame whenever DLSS is not
        // upscaling: its LateUpdate releases them, because in its own use they only ever exist
        // while upscaling. Asked for on every frame regardless, they would be rebuilt, registered
        // again and the network's temporal history reset on every frame.
        //
        // So between frames they are taken out of its hands: the fields are cleared (its release
        // then finds nothing) and the textures kept here, and they are put back for the length of
        // its own method. It still creates them, sizes them, fills them and, when the size changes,
        // releases them -- all in code that runs while it holds them.
        private readonly RenderTexture[] _guides = new RenderTexture[3];

        internal void HandGuidesBack()
        {
            bool any = false;

            for (int i = 0; i < 3; i++)
            {
                any = any || _guides[i] != null;
            }

            if (!any)
            {
                return;
            }

            // It has made its own in the meantime (it does while DLSS is upscaling): ours are surplus.
            bool surplus = false;

            for (int i = 0; i < 3; i++)
            {
                surplus = surplus || (Hooks.GuideFields[i].GetValue(Capture) as RenderTexture) != null;
            }

            for (int i = 0; i < 3; i++)
            {
                if (surplus)
                {
                    Drop(ref _guides[i]);
                }
                else
                {
                    Hooks.GuideFields[i].SetValue(Capture, _guides[i]);
                    _guides[i] = null;
                }
            }
        }

        internal void TakeGuides()
        {
            for (int i = 0; i < 3; i++)
            {
                RenderTexture held = Hooks.GuideFields[i].GetValue(Capture) as RenderTexture;

                if (_guides[i] != null && !ReferenceEquals(_guides[i], held))
                {
                    Drop(ref _guides[i]);
                }

                _guides[i] = held;
                Hooks.GuideFields[i].SetValue(Capture, null);
            }
        }

        // After VamDlssNr's evaluate has been issued: the model output it wrote and our result
        // registered, and the resolve queued behind the evaluate.
        internal bool Finish(RenderTexture output, int mode)
        {
            if (output.width != WorkW || output.height != WorkH)
            {
                Hooks.Fail(this, "VamDlssNr built its output at " + output.width + "x" + output.height + ", not the " + WorkW + "x" + WorkH + " it was asked for");
                return false;
            }

            if (_outDirty || _recheck || !ReferenceEquals(output, _regOutput))
            {
                IntPtr pOutput = output.GetNativeTexturePtr();
                IntPtr pResult = Result.GetNativeTexturePtr();

                if (pOutput == IntPtr.Zero || pResult == IntPtr.Zero)
                {
                    return false;
                }

                if (_outDirty || pOutput != _pOutput || pResult != _pResult)
                {
                    _token = NextToken();
                    Native.Issue(Native.PushRegister(Set, IntPtr.Zero, IntPtr.Zero, pOutput, pResult, Native.Model | Native.Result,
                        SrgbBit(output, Native.Model) | SrgbBit(Result, Native.Result), _token));
                    _pOutput = pOutput;
                    _pResult = pResult;
                    _outDirty = false;
                    _sentOut = true;
                }

                _regOutput = output;
            }

            // The edit kept over frames. The model's own motion vectors (the first of its guides, at
            // its size) say where each of its pixels was a frame ago; each of its passes over a
            // frame has an edit of its own to keep.
            if ((Hooks.CfgEditSteady != null && Hooks.CfgEditSteady.Value) || Detail)
            {
                if (_steadyFrame != Time.frameCount)
                {
                    _steadyFrame = Time.frameCount;
                    _steadyPass = 0;
                }

                int pass = Mathf.Min(_steadyPass++, _steadyLast.Length - 1);
                RenderTexture motion = Hooks.GuideFields != null && Hooks.GuideFields[0] != null ? Hooks.GuideFields[0].GetValue(Capture) as RenderTexture : null;
                motion = motion ?? _guides[0];
                IntPtr pMotion = motion != null && motion.IsCreated() ? motion.GetNativeTexturePtr() : IntPtr.Zero;

                if (pMotion != IntPtr.Zero)
                {
                    float take = Hooks.CfgEditSteadyTake != null ? Hooks.CfgEditSteadyTake.Value : 0.2f;
                    bool full = Detail && !Windowed;
                    Native.IssuePass(Native.PushSteady(Set, Eyes, pMotion, take, TopDown, Windowed ? Window : null, pass, Time.frameCount - _steadyLast[pass] > 1 || full != _steadyFull, full, ShiftX, ShiftY,
                        Hooks.CfgEditDetailOwn != null ? Hooks.CfgEditDetailOwn.Value : 0.6f));
                    _steadyFull = full;
                    _steadyLast[pass] = Time.frameCount;
                }
            }

            // A smaller model's edit: enlarged along the frame's edges, and sharpened by how far it is enlarged.
            float scale = Hooks.CfgScale != null ? Mathf.Clamp(Hooks.Quantise(Hooks.CfgScale.Value, 2f), 0.1f, 1f) : 1f;
            Native.ResolveOptions(Hooks.CfgEditFollow != null && Hooks.CfgEditFollow.Value, Hooks.CfgEditEdge != null ? Hooks.CfgEditEdge.Value : 0.08f,
                (Hooks.CfgEditSharpen != null ? Hooks.CfgEditSharpen.Value : 1f) * (1f / scale - 1f) - (scale < 1f && Hooks.CfgEditDenoise != null ? Hooks.CfgEditDenoise.Value : 0f),
                Hooks.CfgEditHalo != null ? Hooks.CfgEditHalo.Value : 0.5f,
                Hooks.CfgWindowOutline != null && Hooks.CfgWindowOutline.Value);
            Native.IssuePass(Native.PushPass(true, Set, Eyes, 1f, mode, Windowed ? Window : null, Feather));
            Live = true;
            LiveFrame = Time.frameCount;
            LastUsed = Time.unscaledTime;
            return true;
        }

        private static uint NextToken()
        {
            _tokens++;

            if (_tokens == 0)
            {
                _tokens = 1;
            }

            return _tokens;
        }

        internal void Dispose()
        {
            Live = false;
            Drop(ref Down);
            Drop(ref Result);

            // Guides still held here are ones VamDlssNr no longer uses: it stopped running the
            // network small, and registered its full-size ones in their place when it did.
            for (int i = 0; i < 3; i++)
            {
                Drop(ref _guides[i]);
            }

            _regFrame = null;
            _regOutput = null;

            if (Native.Loaded)
            {
                Native.Issue(Native.PushRelease(Set));
            }
        }
    }

    public static class Hooks
    {
        internal const int MaxSets = 16;
        internal const int MinShortSide = 256;
        internal const float MinScale = 0.25f;
        internal const float MinWindow = 0.25f;

        internal static Action<string> Info = delegate { };
        internal static Action<string> Warn = delegate { };
        internal static Action<string> Error = delegate { };

        internal static ConfigEntry<float> CfgScale;
        internal static ConfigEntry<bool> CfgEditFollow, CfgEditSteady, CfgEditDetail, CfgWindowOutline;
        internal static ConfigEntry<float> CfgEditDetailOwn, CfgEditDenoise;
        internal static ConfigEntry<float> CfgEditSteadyTake, CfgInputSharpen;
        internal static ConfigEntry<float> CfgEditEdge, CfgEditSharpen, CfgEditHalo;
        internal static ConfigEntry<bool> CfgStills;
        internal static ConfigEntry<int> CfgEnlargement;
        internal static ConfigEntry<int> CfgDebugView;

        // The focus window: how much of each eye the network works on (1 = all of it), how soft its
        // edge is, where it sits relative to the lens centre, and whether the monitor gets one too.
        internal static ConfigEntry<float> CfgWindow;
        internal static ConfigEntry<float> CfgWindowFeather;
        internal static ConfigEntry<float> CfgWindowOffsetX;
        internal static ConfigEntry<float> CfgWindowOffsetY;
        internal static ConfigEntry<bool> CfgWindowMonitor;

        // The window following the eye, where SteamVR has eye tracking to offer.
        internal static ConfigEntry<bool> CfgWindowGaze;
        internal static ConfigEntry<float> CfgGazeDeadZone;
        internal static ConfigEntry<float> CfgGazeHold;
        internal static ConfigEntry<float> CfgGazeReturn;

        // The monitor's window: a shape of its own (a screen is wide, a figure is tall), aimed at
        // the person in view rather than at the middle of the screen.
        internal static ConfigEntry<float> CfgMonitorWidth;
        internal static ConfigEntry<float> CfgMonitorHeight;
        internal static ConfigEntry<bool> CfgMonitorFollow;
        internal static ConfigEntry<float> CfgMonitorDeadZone;
        internal static ConfigEntry<bool> CfgMonitorFit;

        // VamDlssNr's [Orientation] FlipY: which way up its copy of the frame is stored.
        private static ConfigEntry<bool> _modFlipY;

        // Whether the frame hook is in place. Without it the slider is shown but explains itself.
        internal static bool Hooked;
        internal static string Problem = "";
        internal static string PanelProblem = "";

        // Whether the two calls inside RunNeuralRendering that a window needs redirected were found
        // and redirected. Without that the window stays off and everything else works as before.
        internal static bool WindowHooked;
        internal static string WindowProblem = "";
        internal static int GuideSites, ParamSites;

        private static float _applied = 1f;
        private static float _appliedWindow = 1f;
        private static float _appliedMonitorW = 1f, _appliedMonitorH = 1f;
        private static int _failures;
        private static int _faults;
        private static int _composeSites;

        private static readonly Dictionary<int, View> _views = new Dictionary<int, View>();
        private static readonly List<int> _scratch = new List<int>();

        // The view whose RunNeuralRendering is running right now with a window in force.
        private static View _current;

        private delegate void SetParamsFn(int view, float i, float lt, float ls, float ss, float mx, float my, int preset, int style, int inv, int uiCorrection, int controlMask, int reset);

        private static SetParamsFn _setParams;
        private static PropertyInfo P_camera;
        private static MethodInfo M_setParams, M_guideBlit, M_setParamsWindowed;

        private static FieldInfo F_output, F_primary, F_failed;
        private static MethodInfo M_run, M_compose, M_outputFor;

        // NrCapture._nrMVec, _nrDepth, _nrMask -- see View.HandGuidesBack.
        internal static readonly FieldInfo[] GuideFields = new FieldInfo[3];

        // Self-test only: >= 0 replaces the model's input with that flat grey level.
        internal static float TestFlat = -1f;

        private static FieldInfo F_tab, F_rows, F_heldCfg, F_rowRt;
        private static MethodInfo M_slider, M_live, M_liveWarn, M_reflow, M_renderTab, M_saveNow, M_toggle;

        internal static float Applied
        {
            get { return _applied; }
        }

        internal static float AppliedWindow
        {
            get { return _appliedWindow; }
        }

        internal static int ComposeSites
        {
            get { return _composeSites; }
        }

        // ---- setup ---------------------------------------------------------------------------

        // Everything this plugin reaches into VamDlssNr for, looked up once. Returns what is
        // missing, or null. A build of VamDlssNr that has moved any of it is left alone entirely.
        internal static string Resolve()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type capture = typeof(NrCapture);

            F_output = capture.GetField("_output", any);
            F_primary = capture.GetField("Primary", any);
            F_failed = capture.GetField("_failed", any);
            M_run = capture.GetMethod("RunNeuralRendering", any, null, new[] { typeof(int), typeof(int), typeof(Texture), typeof(bool) }, null);
            M_compose = capture.GetMethod("Compose", any, null, new[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
            M_outputFor = typeof(Hooks).GetMethod("OutputFor", BindingFlags.Static | BindingFlags.Public);

            if (F_output == null || F_output.FieldType != typeof(RenderTexture))
            {
                return "NrCapture._output";
            }

            if (M_run == null || M_run.ReturnType != typeof(bool))
            {
                return "NrCapture.RunNeuralRendering(int, int, Texture, bool)";
            }

            if (M_compose == null)
            {
                return "NrCapture.Compose(RenderTexture, RenderTexture)";
            }

            string[] guides = { "_nrMVec", "_nrDepth", "_nrMask" };

            for (int i = 0; i < guides.Length; i++)
            {
                GuideFields[i] = capture.GetField(guides[i], any);

                if (GuideFields[i] == null || GuideFields[i].FieldType != typeof(RenderTexture))
                {
                    return "NrCapture." + guides[i];
                }
            }

            // The panel and the config save are conveniences: without them the setting still works
            // from this plugin's own .cfg file.
            Type panel = typeof(VamDlssNrPanel);
            F_tab = panel.GetField("_tab", any);
            F_rows = panel.GetField("_rows", any);
            F_heldCfg = panel.GetField("_heldCfg", any);
            M_renderTab = panel.GetMethod("RenderTab", any, null, Type.EmptyTypes, null);
            M_reflow = panel.GetMethod("Reflow", any, null, Type.EmptyTypes, null);
            M_slider = panel.GetMethod("Slider", any, null, new[] { typeof(float).MakeByRefType(), typeof(string), typeof(ConfigEntry<float>) }, null);
            M_live = panel.GetMethod("Live", any, null, new[] { typeof(float).MakeByRefType(), typeof(Func<string>) }, null);
            M_toggle = panel.GetMethod("Toggle", any, null, new[] { typeof(float).MakeByRefType(), typeof(string), typeof(ConfigEntry<bool>), typeof(bool), typeof(string), typeof(bool) }, null);
            M_liveWarn = panel.GetMethod("LiveWarn", any, null, new[] { typeof(float).MakeByRefType(), typeof(Func<string>) }, null);
            Type row = panel.GetNestedType("RowEntry", any);
            F_rowRt = row != null ? row.GetField("Rt", any) : null;
            M_saveNow = typeof(VamDlssNrPlugin).GetMethod("SaveNow", any, null, Type.EmptyTypes, null);

            if (F_tab == null || F_rows == null || M_renderTab == null || M_reflow == null || M_slider == null || M_live == null)
            {
                PanelProblem = "VamDlssNr's panel has changed shape -- the slider cannot be added to it; set ModelResolution in " + WorkScalePlugin.Guid + ".cfg instead";
            }

            // The focus window needs two more things of RunNeuralRendering: how it copies its guides
            // and how it tells the network what a motion vector is worth. Without them the window
            // stays off and nothing else is affected.
            try
            {
                P_camera = capture.GetProperty("Camera", any);
                FieldInfo flipY = typeof(VamDlssNrPlugin).GetField("CfgFlipY", any);
                _modFlipY = flipY != null ? flipY.GetValue(null) as ConfigEntry<bool> : null;
                M_guideBlit = typeof(Hooks).GetMethod("GuideBlit", BindingFlags.Static | BindingFlags.Public);
                M_setParamsWindowed = typeof(Hooks).GetMethod("SetParamsWindowed", BindingFlags.Static | BindingFlags.Public);
                M_setParams = typeof(VamDlssNrPlugin).GetMethod("SetParams", any, null, new[]
                {
                    typeof(int), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                }, null);

                if (M_setParams == null || !M_setParams.IsStatic)
                {
                    WindowProblem = "the focus window is not available: this build of VamDlssNr has no SetParams(view, .., mx, my, ..)";
                }
                else
                {
                    _setParams = (SetParamsFn)Delegate.CreateDelegate(typeof(SetParamsFn), M_setParams);
                }
            }
            catch (Exception ex)
            {
                WindowProblem = "the focus window is not available (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            return null;
        }

        internal static void Apply(Harmony harmony)
        {
            // The reader first: if VamDlssNr's frame code cannot be redirected to the resolved
            // frame, shrinking the network's input would only shrink the picture.
            _composeSites = 0;
            harmony.Patch(M_compose, null, null, new HarmonyMethod(typeof(Hooks).GetMethod("ComposeTranspiler", BindingFlags.Static | BindingFlags.Public)));

            if (_composeSites == 0)
            {
                throw new InvalidOperationException("NrCapture.Compose never reads _output -- nothing to redirect");
            }

            harmony.Patch(M_run,
                new HarmonyMethod(typeof(Hooks).GetMethod("RunPrefix", BindingFlags.Static | BindingFlags.Public)),
                new HarmonyMethod(typeof(Hooks).GetMethod("RunPostfix", BindingFlags.Static | BindingFlags.Public)));
            Hooked = true;

            // The focus window's two redirections, on their own: if they cannot go in, the hooks
            // above stay as they are and only the window is unavailable.
            if (WindowProblem.Length == 0)
            {
                MethodInfo transpiler = typeof(Hooks).GetMethod("RunTranspiler", BindingFlags.Static | BindingFlags.Public);

                try
                {
                    harmony.Patch(M_run, null, null, new HarmonyMethod(transpiler));

                    if (!WindowHooked)
                    {
                        WindowProblem = "the focus window is not available: RunNeuralRendering makes " + GuideSites + " guide copies and " + ParamSites + " SetParams calls where 3 and 1 were expected";
                    }
                }
                catch (Exception ex)
                {
                    WindowHooked = false;
                    WindowProblem = "the focus window is not available (" + ex.GetType().Name + ": " + ex.Message + ")";

                    try
                    {
                        harmony.Unpatch(M_run, transpiler);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            if (PanelProblem.Length == 0)
            {
                try
                {
                    harmony.Patch(M_renderTab, null, new HarmonyMethod(typeof(Hooks).GetMethod("RenderTabPostfix", BindingFlags.Static | BindingFlags.Public)));
                }
                catch (Exception ex)
                {
                    PanelProblem = "the slider could not be added to VamDlssNr's panel (" + ex.GetType().Name + ": " + ex.Message + ")";
                }
            }

            if (M_saveNow != null)
            {
                try
                {
                    harmony.Patch(M_saveNow, null, new HarmonyMethod(typeof(Hooks).GetMethod("SaveNowPostfix", BindingFlags.Static | BindingFlags.Public)));
                }
                catch (Exception ex)
                {
                    Warn("config save hook failed (" + ex.GetType().Name + ": " + ex.Message + ") -- the setting is saved when VaM exits instead");
                }
            }
        }

        // ---- the setting ---------------------------------------------------------------------

        internal static float Quantise(float v, float max)
        {
            return Mathf.Clamp(Mathf.Round(v * 100f) / 100f, MinScale, max);
        }

        private static float MaxScale()
        {
            AcceptableValueRange<float> range = CfgScale != null && CfgScale.Description != null ? CfgScale.Description.AcceptableValues as AcceptableValueRange<float> : null;
            return range != null ? range.MaxValue : 1f;
        }

        internal static float QuantiseWindow(float v)
        {
            return Mathf.Clamp(Mathf.Round(v * 100f) / 100f, MinWindow, 1f);
        }

        private static bool SliderHeld()
        {
            try
            {
                object held = F_heldCfg != null ? F_heldCfg.GetValue(null) : null;
                return held != null && (ReferenceEquals(held, CfgScale) || ReferenceEquals(held, CfgWindow) || ReferenceEquals(held, CfgMonitorWidth) || ReferenceEquals(held, CfgMonitorHeight));
            }
            catch
            {
                return false;
            }
        }

        // The value the passes use follows the setting only once it has stopped moving. Every
        // distinct value is a different model size, and a different size rebuilds the network --
        // committing on each pixel of a drag would be dozens of rebuilds a second.
        //
        // VamDlssNr's own slider says when it is held, and that is honoured. A slider in VaM's UI
        // (the in-headset panel) says nothing of the kind -- it only reports values -- so the value
        // also has to have stood still for a moment, which is what letting go looks like from here.
        //
        // The focus window's size goes the same way, for the same reason: it too sets the size of
        // the model's raster.
        private const float SettleSeconds = 0.35f;
        private static float _wanted = 1f;
        private static float _wantedWindow = 1f;
        private static float _wantedMonitorW = 1f, _wantedMonitorH = 1f;
        private static float _wantedSince;

        internal static void Tick()
        {
            if (CfgScale == null)
            {
                return;
            }

            float want = Quantise(CfgScale.Value, MaxScale());
            float wantWindow = CfgWindow != null ? QuantiseWindow(CfgWindow.Value) : 1f;
            float wantW = CfgMonitorWidth != null ? QuantiseWindow(CfgMonitorWidth.Value) : 1f;
            float wantH = CfgMonitorHeight != null ? QuantiseWindow(CfgMonitorHeight.Value) : 1f;

            if (want == _applied && wantWindow == _appliedWindow && wantW == _appliedMonitorW && wantH == _appliedMonitorH)
            {
                _wanted = want;
                _wantedWindow = wantWindow;
                _wantedMonitorW = wantW;
                _wantedMonitorH = wantH;
                return;
            }

            if (want != _wanted || wantWindow != _wantedWindow || wantW != _wantedMonitorW || wantH != _wantedMonitorH)
            {
                _wanted = want;
                _wantedWindow = wantWindow;
                _wantedMonitorW = wantW;
                _wantedMonitorH = wantH;
                _wantedSince = Time.unscaledTime;
            }

            if (SliderHeld() || Time.unscaledTime - _wantedSince < SettleSeconds)
            {
                return;
            }

            float was = _applied, wasWindow = _appliedWindow, wasW = _appliedMonitorW, wasH = _appliedMonitorH;
            _applied = want;
            _appliedWindow = wantWindow;
            _appliedMonitorW = wantW;
            _appliedMonitorH = wantH;

            if (CfgScale.Value != want)
            {
                CfgScale.Value = want;
            }

            if (CfgWindow != null && CfgWindow.Value != wantWindow)
            {
                CfgWindow.Value = wantWindow;
            }

            if (CfgMonitorWidth != null && CfgMonitorWidth.Value != wantW)
            {
                CfgMonitorWidth.Value = wantW;
            }

            if (CfgMonitorHeight != null && CfgMonitorHeight.Value != wantH)
            {
                CfgMonitorHeight.Value = wantH;
            }

            _failures = 0;
            Problem = Hooked && Native.Loaded ? "" : Problem;

            if (was != want)
            {
                Info("model resolution " + Percent(was) + " -> " + Percent(want));
            }

            if (wasWindow != wantWindow)
            {
                Info("focus window " + (wasWindow >= 1f ? "off" : Percent(wasWindow)) + " -> " + (wantWindow >= 1f ? "off" : Percent(wantWindow)));
            }

            if (wasW != wantW || wasH != wantH)
            {
                Info("monitor focus window " + Percent(wasW) + " x " + Percent(wasH) + " -> " + Percent(wantW) + " x " + Percent(wantH));
            }

            // A size the network refused to start at leaves VamDlssNr holding the feature off until
            // something it watches changes. This is such a change; let it try again.
            try
            {
                if (F_failed != null)
                {
                    NrCapture[] captures = UnityEngine.Object.FindObjectsOfType<NrCapture>();

                    for (int i = 0; i < captures.Length; i++)
                    {
                        F_failed.SetValue(captures[i], false);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("could not clear VamDlssNr's failure latch: " + ex.Message);
            }
        }

        internal static string Percent(float scale)
        {
            return Mathf.RoundToInt(scale * 100f) + "%";
        }

        // The focus window's size in frame pixels, per eye, for a w x h frame. False when there is
        // none: the setting is 1, the frame's shape is not one the passes handle, or the window
        // would be too small for the network to say anything useful about.
        internal static bool WindowExtent(int w, int h, float sizeX, float sizeY, int eyes, out int winW, out int winH)
        {
            winW = winH = 0;

            if ((sizeX >= 1f && sizeY >= 1f) || eyes < 1 || w < eyes * 2 || h < 2 || (w % eyes) != 0)
            {
                return false;
            }

            int eyeW = w / eyes;
            winW = Mathf.Clamp(Mathf.RoundToInt(eyeW * sizeX), 2, eyeW);
            winH = Mathf.Clamp(Mathf.RoundToInt(h * sizeY), 2, h);
            return Mathf.Min(winW, winH) >= MinShortSide && (winW < eyeW || winH < h);
        }

        // Whether the monitor's window is to fit the people in view rather than keep one size.
        private static bool MonitorFits(bool monitor)
        {
            return monitor && CfgMonitorFollow != null && CfgMonitorFollow.Value && CfgMonitorFit != null && CfgMonitorFit.Value;
        }

        // Whether any window is asked for at all: the headset's, or the monitor's when that is on.
        private static bool AnyWindow()
        {
            return _appliedWindow < 1f || (CfgWindowMonitor != null && CfgWindowMonitor.Value && (_appliedMonitorW < 1f || _appliedMonitorH < 1f || MonitorFits(true)));
        }

        // The capture's camera, for whoever needs to know where things are on its picture.
        internal static Camera CameraOf(NrCapture capture)
        {
            try
            {
                return P_camera != null ? P_camera.GetValue(capture, null) as Camera : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Where the middle of each eye's window goes, as fractions of the eye's picture: {left x,
        // left y, right x, right y}, x from the left and y UP from the bottom of the picture.
        // (Which row of the texture that is depends on how the frame is stored; View.SetWindow
        // sorts that out.)
        //
        // In a headset that is the lens centre, where the optics are sharpest and straight ahead
        // is; it is not the middle of the eye's image, because the frustum reaches further out
        // than in. The projection says where: the view axis lands at NDC (-m02, -m12).
        internal static void LensCentres(NrCapture capture, int eyes, float[] c)
        {
            c[0] = c[1] = c[2] = c[3] = 0.5f;

            if (eyes == 2)
            {
                try
                {
                    Camera cam = P_camera != null ? P_camera.GetValue(capture, null) as Camera : null;

                    if (cam != null && cam.stereoEnabled)
                    {
                        Matrix4x4 left = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                        Matrix4x4 right = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                        c[0] = AxisAt(left.m02);
                        c[1] = AxisAt(left.m12);
                        c[2] = AxisAt(right.m02);
                        c[3] = AxisAt(right.m12);
                    }
                }
                catch (Exception)
                {
                }
            }

            // The nudge: X towards the nose (the two eyes move opposite ways), Y up.
            float dx = CfgWindowOffsetX != null ? CfgWindowOffsetX.Value : 0f;
            float dy = CfgWindowOffsetY != null ? CfgWindowOffsetY.Value : 0f;
            c[0] += dx;
            c[2] -= eyes == 2 ? dx : 0f;
            c[1] += dy;
            c[3] += dy;

            for (int i = 0; i < 4; i++)
            {
                c[i] = Mathf.Clamp(c[i], 0.1f, 0.9f);
            }
        }

        private static float AxisAt(float m)
        {
            float v = 0.5f - 0.5f * m;
            return float.IsNaN(v) || v < 0.2f || v > 0.8f ? 0.5f : v;
        }

        // Which way up the frame RunNeuralRendering is given lies in memory. It is VamDlssNr's own
        // copy of the camera's picture, made with its FlipY: on (its default) the copy is upright
        // as Direct3D counts -- first row at the top -- and off it is as Unity keeps a render
        // texture, first row at the bottom.
        internal static bool NetIsTopDown()
        {
            return _modFlipY == null || _modFlipY.Value;
        }

        // The model's raster for a w x h frame. False when the frame should run as it is: the
        // scale is 1, the frame is too small to be worth a second raster, or nothing would change.
        internal static bool WorkExtent(int w, int h, float scale, int eyes, out int ww, out int wh)
        {
            ww = w;
            wh = h;

            if (eyes < 1 || w < eyes * 2 || h < 2 || (w % eyes) != 0)
            {
                return false;
            }

            int eyeW = w / eyes;
            float s = scale;

            if (s < 1f)
            {
                // Never below a raster the network can still say something useful about.
                float floor = MinShortSide / (float)Mathf.Min(eyeW, h);

                if (floor >= 1f)
                {
                    return false;
                }

                s = Mathf.Max(s, floor);
            }

            int ew = Mathf.Max(2, Mathf.RoundToInt(eyeW * s));
            int eh = Mathf.Max(2, Mathf.RoundToInt(h * s));

            // Above the frame's size the cost grows with the area, and a screenshot camera can be
            // 8K already: refuse what a D3D11 texture cannot hold or no card could afford. (Below
            // the frame's size neither can happen -- the frame itself is the larger texture.)
            if (s > 1f && ((long)ew * eyes > 16384L || eh > 16384 || (long)ew * eyes * eh > 40000000L))
            {
                return false;
            }

            ww = ew * eyes;
            wh = eh;
            return ww != w || wh != h;
        }

        // ---- views ---------------------------------------------------------------------------

        private static View ViewFor(NrCapture capture)
        {
            int id = capture.GetInstanceID();
            View view;

            if (_views.TryGetValue(id, out view))
            {
                return view;
            }

            bool[] used = new bool[MaxSets];

            foreach (KeyValuePair<int, View> kv in _views)
            {
                used[kv.Value.Set] = true;
            }

            for (uint set = 0; set < MaxSets; set++)
            {
                if (!used[set])
                {
                    // Whatever the previous owner of this set left behind goes first.
                    Native.Issue(Native.PushRelease(set));
                    view = new View(capture, set);
                    _views[id] = view;
                    return view;
                }
            }

            return null;
        }

        internal static void Fail(View view, string why)
        {
            view.Broken = true;
            view.Live = false;
            _failures++;
            Problem = "model resolution is off: " + why;
            Error(Problem + (_failures >= 3 ? " -- third failure, not trying again until the setting changes" : ""));
        }

        private static void Fault(string where, Exception ex)
        {
            _faults++;

            if (_faults <= 3)
            {
                Error(where + " failed: " + ex.GetType().Name + " -- " + ex.Message + "\n" + ex.StackTrace);
            }

            if (_faults >= 3)
            {
                Problem = "model resolution is off: " + where + " keeps failing (" + ex.GetType().Name + ") -- see the log";
            }
        }

        // One line on what every view is doing, for the log.
        internal static string Describe()
        {
            StringBuilder sb = new StringBuilder("views=" + _views.Count);

            foreach (KeyValuePair<int, View> kv in _views)
            {
                View v = kv.Value;
                int ready = 0, error = 0, hr = 0;
                uint token = 0;

                if (Native.Loaded)
                {
                    Native.Poll(v.Set, out ready, out error, out token, out hr);
                }

                sb.Append(" [set ").Append(v.Set).Append(' ').Append(v.FrameW).Append('x').Append(v.FrameH).Append(" -> ").Append(v.WorkW).Append('x').Append(v.WorkH)
                    .Append(" eyes=").Append(v.Eyes).Append(v.Windowed ? " window=" + v.WindowW + "x" + v.WindowH + "@" + v.Window[0].ToString("0.000") + "," + v.Window[1].ToString("0.000") : "")
                    .Append(v.Live && Time.frameCount - v.LiveFrame <= 2 ? " live" : " idle").Append(v.Broken ? " BROKEN" : "")
                    .Append(" ready=").Append(ready).Append(" err=").Append(error).Append(']');
            }

            sb.Append(" failures=").Append(_failures).Append(" faults=").Append(_faults);
            return sb.ToString();
        }

        // Releases what a capture that has stopped composing was holding. VamDlssNr tears its own
        // side down on several paths; watching for disuse covers all of them without hooking any.
        internal static void Sweep()
        {
            if (_views.Count == 0)
            {
                return;
            }

            _scratch.Clear();

            foreach (KeyValuePair<int, View> kv in _views)
            {
                View v = kv.Value;

                if (v.Capture == null || Time.unscaledTime - v.LastUsed > 2f)
                {
                    _scratch.Add(kv.Key);
                }
            }

            for (int i = 0; i < _scratch.Count; i++)
            {
                View v = _views[_scratch[i]];
                _views.Remove(_scratch[i]);
                v.Dispose();
            }
        }

        internal static void ReleaseAll()
        {
            foreach (KeyValuePair<int, View> kv in _views)
            {
                kv.Value.Dispose();
            }

            _views.Clear();
        }

        // ---- the frame hook ------------------------------------------------------------------

        // Runs ahead of NrCapture.RunNeuralRendering(w, h, input, outputExtentCopies). When the
        // model is to run small, the frame is shrunk into our own texture and the method is handed
        // that and its size instead; it then builds everything else -- guides, view, evaluate -- at
        // the size it was told, exactly as it does for any other extent.
        public static void RunPrefix(NrCapture __instance, ref int __0, ref int __1, ref Texture __2, ref bool __3, out View __state)
        {
            __state = null;
            _current = null;

            if (!Hooked || !Native.Loaded || _faults >= 3 || _failures >= 3 || (_applied == 1f && !AnyWindow()))
            {
                return;
            }

            try
            {
                Texture frame = __2;

                if (frame == null || __instance == null)
                {
                    return;
                }

                bool primary = F_primary == null || (bool)F_primary.GetValue(__instance);

                if (CfgStills != null && !CfgStills.Value && !primary)
                {
                    return;
                }

                RenderTexture frameRt = frame as RenderTexture;
                int eyes = frameRt != null && frameRt.vrUsage == VRTextureUsage.TwoEyes ? 2 : 1;
                int w = __0, h = __1, ww, wh, winW = 0, winH = 0;

                if (frame.width != w || frame.height != h)
                {
                    return;
                }

                // The window is for the live view -- a still is wanted whole. A headset's is one
                // size both ways (an eye's picture is about square); the monitor's has a width
                // and a height of its own and is only used when asked for.
                bool monitor = eyes == 1;
                float sizeX = monitor ? _appliedMonitorW : _appliedWindow;
                float sizeY = monitor ? _appliedMonitorH : _appliedWindow;
                bool wanted = WindowHooked && primary && (!monitor || (CfgWindowMonitor != null && CfgWindowMonitor.Value));
                bool fit = wanted && MonitorFits(monitor);
                View view = null;
                bool windowed;

                if (fit)
                {
                    view = ViewFor(__instance);

                    if (view == null || view.Broken)
                    {
                        return;
                    }

                    // A window that fits the people in view. What MonitorWidth x MonitorHeight of
                    // the screen comes to is the AREA the network is given; where the window is,
                    // how big, and which of three shapes, is the figures' doing.
                    view.PlanFit(__instance, w, h, (w * sizeX) * (h * sizeY), _applied, out winW, out winH);
                    windowed = fit = Mathf.Min(winW, winH) >= MinShortSide;
                }
                else
                {
                    windowed = wanted && WindowExtent(w, h, sizeX, sizeY, eyes, out winW, out winH);
                }

                if (windowed)
                {
                    // The model's raster is the window (for a fitted one, its shape at plain
                    // size), shrunk by the model resolution if that is below 1 -- and the window
                    // itself, pixel for pixel, if it is not.
                    if (!WorkExtent(winW * eyes, winH, _applied, eyes, out ww, out wh))
                    {
                        ww = winW * eyes;
                        wh = winH;
                    }
                }
                else if (!WorkExtent(w, h, _applied, eyes, out ww, out wh))
                {
                    return;
                }

                view = view ?? ViewFor(__instance);

                if (view == null || view.Broken)
                {
                    return;
                }

                if (windowed)
                {
                    view.SetWindow(__instance, w, h, eyes, winW, winH, sizeX, sizeY, fit);
                }
                else
                {
                    view.ClearWindow();
                }

                if (!view.Prepare(frame, w, h, ww, wh, eyes))
                {
                    return;
                }

                view.HandGuidesBack();
                // Detail built up over frames: the network's raster shifted by a part of one of its
                // pixels, differently every frame (not with a window, which has its own way of moving).
                view.Shift(CfgEditDetail != null && CfgEditDetail.Value && !windowed, w, ww);
                Native.DownOptions(CfgInputSharpen != null ? CfgInputSharpen.Value : 0f, view.ShiftX, view.ShiftY);
                view.Downsample();

                // Only now, with nothing left that can fail, does the method see the smaller frame.
                __2 = view.Down;
                __0 = ww;
                __1 = wh;
                __3 = true; // the guides are a different size from the model: have them resampled
                __state = view;
                _current = windowed ? view : null;
            }
            catch (Exception ex)
            {
                _current = null;
                Fault("the model-resolution prefix", ex);
            }
        }

        // Stands in for the three Graphics.Blit calls with which RunNeuralRendering copies its
        // guides to the model's size. With a window in force the copy is the window of the guide,
        // not the whole of it squeezed; without one it is the blit it replaced.
        public static void GuideBlit(Texture source, RenderTexture dest)
        {
            View view = _current;

            try
            {
                if (view != null && view.Windowed && view.CutGuide(source, dest))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Fault("cutting a guide to the focus window", ex);
            }

            Graphics.Blit(source, dest);
        }

        // Stands in for RunNeuralRendering's call to VamDlssNrPlugin.SetParams. mx and my are what
        // one unit of a motion vector -- a whole eye's width or height -- is in model pixels, and
        // VamDlssNr takes that to be the model raster's size. With a window the raster covers only
        // part of the eye, so the same vector is worth more of its pixels.
        public static void SetParamsWindowed(int view, float i, float lt, float ls, float ss, float mx, float my, int preset, int style, int inv, int uiCorrection, int controlMask, int reset)
        {
            View v = _current;

            if (v != null && v.Windowed)
            {
                mx *= v.GainX;
                my *= v.GainY;
            }

            _setParams(view, i, lt, ls, ss, mx, my, preset, style, inv, uiCorrection, controlMask, reset);
        }

        public static IEnumerable<CodeInstruction> RunTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            List<int> blits = new List<int>();
            List<int> parameters = new List<int>();

            for (int i = 0; i < list.Count; i++)
            {
                MethodInfo called = list[i].operand as MethodInfo;

                if (called == null || (list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt))
                {
                    continue;
                }

                if (called.DeclaringType == typeof(Graphics) && called.Name == "Blit")
                {
                    ParameterInfo[] p = called.GetParameters();

                    if (p.Length == 2 && p[0].ParameterType == typeof(Texture) && p[1].ParameterType == typeof(RenderTexture))
                    {
                        blits.Add(i);
                    }
                }
                else if (called.DeclaringType == typeof(VamDlssNrPlugin) && called.Name == "SetParams")
                {
                    parameters.Add(i);
                }
            }

            GuideSites = blits.Count;
            ParamSites = parameters.Count;
            WindowHooked = blits.Count == 3 && parameters.Count == 1 && _setParams != null && M_guideBlit != null && M_setParamsWindowed != null;

            // All of it or none of it: a window with its guides uncut, or with its motion vectors
            // at the wrong scale, would be worse than no window.
            if (WindowHooked)
            {
                foreach (int i in blits)
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = M_guideBlit;
                }

                list[parameters[0]].opcode = OpCodes.Call;
                list[parameters[0]].operand = M_setParamsWindowed;
            }

            return list;
        }

        public static void RunPostfix(NrCapture __instance, bool __result, View __state)
        {
            View view = __state;
            _current = null;

            if (view == null)
            {
                return;
            }

            try
            {
                view.Live = false;

                // Whatever happened in between, the guides come back out of its hands.
                view.TakeGuides();

                // VamDlssNr refused to run this frame; its caller does not read the output then.
                if (!__result)
                {
                    return;
                }

                RenderTexture output = F_output.GetValue(__instance) as RenderTexture;

                if (output == null)
                {
                    return;
                }

                int debug = CfgDebugView != null ? CfgDebugView.Value : 0;
                int mode = debug == 2 || debug == 3 ? debug : (CfgEnlargement != null && CfgEnlargement.Value == 1 ? 1 : 0);
                view.Finish(output, mode);
            }
            catch (Exception ex)
            {
                view.Live = false;
                Fault("the model-resolution postfix", ex);
            }
        }

        // What NrCapture.Compose gets where it used to read its _output field: the resolved,
        // frame-size picture on a frame where the model ran small, the field itself otherwise.
        public static RenderTexture OutputFor(NrCapture capture)
        {
            if (_views.Count != 0 && capture != null)
            {
                View view;

                if (_views.TryGetValue(capture.GetInstanceID(), out view) && view.Live && view.LiveFrame == Time.frameCount && view.Result != null)
                {
                    return view.Result;
                }
            }

            return (RenderTexture)F_output.GetValue(capture);
        }

        public static IEnumerable<CodeInstruction> ComposeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int sites = 0;

            foreach (CodeInstruction ins in instructions)
            {
                FieldInfo field = ins.operand as FieldInfo;

                if (ins.opcode == OpCodes.Ldfld && field != null && field.Name == "_output" && field.DeclaringType == typeof(NrCapture))
                {
                    // Same stack shape: the capture on, a RenderTexture off.
                    ins.opcode = OpCodes.Call;
                    ins.operand = M_outputFor;
                    sites++;
                }

                yield return ins;
            }

            _composeSites = sites;
        }

        // ---- the panel -----------------------------------------------------------------------

        private static string _status = "";
        private static string _statusBrief = "";
        private static float _statusAt = -1f;

        private static string StatusLine()
        {
            if (!Hooked || !Native.Loaded || CfgScale == null)
            {
                return "";
            }

            float want = Quantise(CfgScale.Value, MaxScale());
            float wantWindow = CfgWindow != null ? QuantiseWindow(CfgWindow.Value) : 1f;
            float wantW = CfgMonitorWidth != null ? QuantiseWindow(CfgMonitorWidth.Value) : 1f;
            float wantH = CfgMonitorHeight != null ? QuantiseWindow(CfgMonitorHeight.Value) : 1f;

            if (want != _applied || wantWindow != _appliedWindow || wantW != _appliedMonitorW || wantH != _appliedMonitorH)
            {
                string what = want != _applied ? Percent(want)
                    : (wantWindow != _appliedWindow ? (wantWindow >= 1f ? "the whole view" : "a " + Percent(wantWindow) + " window") : "a " + Percent(wantW) + " x " + Percent(wantH) + " monitor window");
                return SliderHeld() ? "release the slider to apply " + what : "applying " + what + "...";
            }

            if (_applied == 1f && !AnyWindow())
            {
                return "";
            }

            if (Time.unscaledTime - _statusAt < 0.25f)
            {
                return _status;
            }

            _statusAt = Time.unscaledTime;
            _status = "";
            _statusBrief = "";
            View best = null;

            foreach (KeyValuePair<int, View> kv in _views)
            {
                View v = kv.Value;

                if (!v.Live || Time.frameCount - v.LiveFrame > 3 || v.Capture == null)
                {
                    continue;
                }

                bool primary = F_primary != null && (bool)F_primary.GetValue(v.Capture);

                if (best == null || primary)
                {
                    best = v;
                }
            }

            if (best != null && best.Windowed)
            {
                float ratio = best.WindowH > 0 ? (float)best.WorkH / best.WindowH : _applied;
                string raster = best.Eyes == 2 ? "2 x " + (best.WorkW / 2) + "x" + best.WorkH : best.WorkW + "x" + best.WorkH;
                string whole = best.Eyes == 2 ? "2 x " + (best.FrameW / 2) + "x" + best.FrameH + " eyes" : "a " + best.FrameW + "x" + best.FrameH + " frame";
                string size = best.WindowFractionX == best.WindowFractionY ? Percent(best.WindowFractionX) : Percent(best.WindowFractionX) + " x " + Percent(best.WindowFractionY);
                bool aimed = best.AimMode != View.AimAtRest && (best.GazeState == GazeFilter.Following || best.GazeState == GazeFilter.Holding);
                bool people = best.AimMode == View.AimAtPerson || best.AimMode == View.AimFitPeople;
                string shape = best.AimMode == View.AimFitPeople ? (best.FitShape == 0 ? "upright " : (best.FitShape == 2 ? "wide " : "square ")) : "";
                string where = !aimed ? "the middle " : (best.AimMode == View.AimFitPeople ? "fitted to the people, " : (people ? "around the person, " : (best.GazeState == GazeFilter.Following ? "where you look, " : "where you last looked, ")));
                _status = "network: " + raster + " -- " + where + shape + size + " of " + whole + ", at " + Percent(ratio);
                _statusBrief = "model: " + raster + ", " + (!aimed ? "middle " : (best.AimMode == View.AimFitPeople ? "fit " : (people ? "person " : "gaze "))) + size + " at " + Percent(ratio);

                // Asked to follow the eye, or the people, and not doing so: say why.
                if (best.AimMode == View.AimAtGaze && best.GazeState == GazeFilter.AtRest)
                {
                    _status += " -- no eye tracking: " + Gaze.Status;
                    _statusBrief += "\nno eye tracking: " + Gaze.Status;
                }
                else if (people && best.GazeState == GazeFilter.AtRest)
                {
                    _status += " -- " + Subject.Status;
                    _statusBrief += "\n" + Subject.Status;
                }
            }
            else if (best != null)
            {
                float ratio = best.FrameH > 0 ? (float)best.WorkH / best.FrameH : _applied;
                _status = best.Eyes == 2
                    ? "network: 2 x " + (best.WorkW / 2) + "x" + best.WorkH + " for 2 x " + (best.FrameW / 2) + "x" + best.FrameH + " eyes (" + Percent(ratio) + ")"
                    : "network: " + best.WorkW + "x" + best.WorkH + " for a " + best.FrameW + "x" + best.FrameH + " frame (" + Percent(ratio) + ")";

                // The same, worded for a box half as wide (the in-headset panel's).
                _statusBrief = best.Eyes == 2
                    ? "model: 2 x " + (best.WorkW / 2) + "x" + best.WorkH + " of 2 x " + (best.FrameW / 2) + "x" + best.FrameH + " (" + Percent(ratio) + ")"
                    : "model: " + best.WorkW + "x" + best.WorkH + " of " + best.FrameW + "x" + best.FrameH + " (" + Percent(ratio) + ")";
            }

            // The headset's window, set and not in use because this is not a headset's view: say
            // so, or the slider looks as if it does nothing.
            if ((best == null || !best.Windowed) && _appliedWindow < 1f && WindowHooked)
            {
                string note = "focus window " + Percent(_appliedWindow) + ": that one is the headset's";
                _status = _status.Length != 0 ? _status + " -- " + note : note;
                _statusBrief = _statusBrief.Length != 0 ? _statusBrief + "\n" + note : note;
            }

            return _status;
        }

        private static string ProblemLine()
        {
            if (!Native.Loaded)
            {
                return "model resolution is off: " + Native.Problem;
            }

            if (Problem.Length == 0 && WindowProblem.Length != 0 && _appliedWindow < 1f)
            {
                return WindowProblem;
            }

            return Problem;
        }

        // The same two lines, for the in-headset panel's status box.
        internal static string ModelLine()
        {
            if (!Hooked || !Native.Loaded || CfgScale == null)
            {
                return "";
            }

            string line = StatusLine();

            if (line.Length != 0)
            {
                // What the network is running at has a shorter wording; "applying..." is short as it is.
                return ReferenceEquals(line, _status) && _statusBrief.Length != 0 ? _statusBrief : line;
            }

            return _applied == 1f ? "model: full frame size (100%)" : "model: " + Percent(_applied) + " per axis, once NR is running";
        }

        // Whether the live view is being run through a window right now.
        internal static bool WindowInForce()
        {
            foreach (KeyValuePair<int, View> kv in _views)
            {
                View v = kv.Value;

                if (v.Windowed && v.Live && Time.frameCount - v.LiveFrame <= 3)
                {
                    return true;
                }
            }

            return false;
        }

        internal static string ProblemText()
        {
            return ProblemLine();
        }

        // Saves both settings files: VamDlssNr's own save, which our hook on it extends to ours.
        internal static void SaveAll()
        {
            try
            {
                if (M_saveNow != null)
                {
                    M_saveNow.Invoke(null, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                Warn("VamDlssNr's save failed: " + ex.Message);
            }

            SaveNowPostfix();
        }

        internal static void RedrawModPanel(VamDlssNrPanel panel)
        {
            if (M_renderTab != null && panel != null)
            {
                M_renderTab.Invoke(panel, null);
            }
        }

        public static void RenderTabPostfix(VamDlssNrPanel __instance)
        {
            if (CfgScale == null || PanelProblem.Length != 0)
            {
                return;
            }

            try
            {
                if ((string)F_tab.GetValue(__instance) != "nr")
                {
                    return;
                }

                IList rows = (IList)F_rows.GetValue(__instance);
                int before = rows.Count;

                M_slider.Invoke(__instance, new object[] { 0f, "Model resolution", CfgScale });

                if (CfgWindow != null && WindowHooked)
                {
                    M_slider.Invoke(__instance, new object[] { 0f, "Focus window (headset)", CfgWindow });

                    // The monitor's own window: this panel is where a monitor is set up.
                    if (M_toggle != null && CfgWindowMonitor != null && CfgMonitorWidth != null && CfgMonitorHeight != null && CfgMonitorFollow != null)
                    {
                        // Short: a toggle row has room for about twenty characters of label.
                        M_toggle.Invoke(__instance, new object[] { 0f, "Monitor window", CfgWindowMonitor, false, null, false });
                        M_slider.Invoke(__instance, new object[] { 0f, "Monitor window width", CfgMonitorWidth });
                        M_slider.Invoke(__instance, new object[] { 0f, "Monitor window height", CfgMonitorHeight });
                        M_toggle.Invoke(__instance, new object[] { 0f, "Follow the person", CfgMonitorFollow, false, null, false });

                        if (CfgMonitorFit != null)
                        {
                            M_toggle.Invoke(__instance, new object[] { 0f, "Fit the people", CfgMonitorFit, false, null, false });
                        }
                    }
                }

                if (HeadsetUi.Hooked && HeadsetUi.CfgSharpen != null)
                {
                    M_slider.Invoke(__instance, new object[] { 0f, "Sharpening", HeadsetUi.CfgSharpen });
                }

                if (M_toggle != null && HeadsetUi.Hooked && HeadsetUi.CfgOn != null)
                {
                    M_toggle.Invoke(__instance, new object[] { 0f, "Sharp headset menu", HeadsetUi.CfgOn, false, null, false });
                }

                M_live.Invoke(__instance, new object[] { 0f, new Func<string>(StatusLine) });

                if (M_liveWarn != null)
                {
                    M_liveWarn.Invoke(__instance, new object[] { 0f, new Func<string>(ProblemLine) });
                }

                // The rows were appended below the Status group. Move them up to sit under
                // "Passes", with the other things that decide what the network costs.
                int anchor = -1;

                if (F_rowRt != null)
                {
                    for (int i = 0; i < before; i++)
                    {
                        RectTransform rt = F_rowRt.GetValue(rows[i]) as RectTransform;

                        if (rt != null && rt.name == "Seg_Passes")
                        {
                            anchor = i;
                            break;
                        }
                    }
                }

                if (anchor >= 0 && rows.Count > before)
                {
                    List<object> added = new List<object>();

                    for (int i = before; i < rows.Count; i++)
                    {
                        added.Add(rows[i]);
                    }

                    for (int i = rows.Count - 1; i >= before; i--)
                    {
                        rows.RemoveAt(i);
                    }

                    for (int i = 0; i < added.Count; i++)
                    {
                        rows.Insert(anchor + 1 + i, added[i]);
                    }
                }

                M_reflow.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                PanelProblem = "the slider could not be added to VamDlssNr's panel (" + ex.GetType().Name + ": " + ex.Message + ")";
                Error(PanelProblem + "\n" + ex.StackTrace);
            }
        }

        public static void SaveNowPostfix()
        {
            try
            {
                if (WorkScalePlugin.Instance != null)
                {
                    WorkScalePlugin.Instance.Config.Save();
                }
            }
            catch (Exception ex)
            {
                Warn("config save failed: " + ex.Message);
            }
        }
    }

    [BepInPlugin(Guid, "VaM DLSS - Model Resolution", Version)]
    [BepInDependency("uncleburrito.vamdlssnr")]
    public class WorkScalePlugin : BaseUnityPlugin
    {
        public const string Guid = "jeahbwoi720.vamdlssnr.workscale";
        public const string Version = "1.9.0";

        // What every build's settings file is called after its owner prefix.
        private const string SettingsSuffix = ".vamdlssnr.workscale.cfg";

        internal static WorkScalePlugin Instance;

        private float _nextSweep;
        private float _nextDrain;

        // 1.0.0 kept its settings under a different owner prefix. If this build's file does not
        // exist yet and that one does, it is renamed into place -- once, before anything is bound,
        // so the values in it are the ones the settings below come up with.
        private void AdoptEarlierSettings()
        {
            try
            {
                string mine = Config.ConfigFilePath;

                if (File.Exists(mine))
                {
                    return;
                }

                foreach (string other in Directory.GetFiles(Path.GetDirectoryName(mine), "*" + SettingsSuffix))
                {
                    if (string.Equals(Path.GetFullPath(other), mine, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    File.Move(other, mine);
                    Config.Reload();
                    Logger.LogInfo("[vws] settings taken over from an earlier build's file (now " + Path.GetFileName(mine) + ")");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[vws] an earlier build's settings could not be taken over (" + ex.GetType().Name + ": " + ex.Message + ") -- starting from defaults");
            }
        }

        private void Awake()
        {
            Instance = this;
            Hooks.Info = delegate(string s) { Logger.LogInfo("[vws] " + s); };
            Hooks.Warn = delegate(string s) { Logger.LogWarning("[vws] " + s); };
            Hooks.Error = delegate(string s) { Logger.LogError("[vws] " + s); };

            AdoptEarlierSettings();
            Config.SaveOnConfigSet = false;

            ConfigEntry<bool> supersample = Config.Bind("Advanced", "AllowSupersampling", false,
                "Let the Model resolution slider go above 1.0, up to 2.0: the network then runs on an ENLARGED copy of the frame and its edit is averaged back down. Experimental and expensive -- time and video memory grow with the area, so 2.0 costs four times what 1.0 does. Takes effect the next time VaM starts.");

            Hooks.CfgScale = Config.Bind("Neural Rendering", "ModelResolution", 1f, new ConfigDescription(
                "What fraction of the frame, per axis, Neural Rendering works at. Cost falls with the square of this, so 0.5 is roughly a quarter of the network's time.\n\n" +
                "The frame is never reduced. The network is shown a filtered shrink of it, and only what the network CHANGED is enlarged and laid back over the full-size frame, so the picture underneath keeps all of its own detail whatever this says. What it trades is the network's own fine structure, which is synthesised at the smaller size and softens when enlarged; its broader work -- tone, shading, skin -- survives.\n\n" +
                "1.0 is VamDlssNr exactly as it ships. Works the same on the monitor and in a headset (per eye), and multiplies with everything else that sets the network's input size: with Run before DLSS on, this is a fraction of the DLSS render extent.\n\n" +
                "Applied when the slider is let go, because every change rebuilds the network.",
                new AcceptableValueRange<float>(Hooks.MinScale, supersample.Value ? 2f : 1f)));

            Hooks.CfgEditFollow = Config.Bind("Neural Rendering", "EditFollowsEdges", true,
                "Below 100% Model resolution: enlarge the network's edit along the full-size frame's own edges instead of plainly. " +
                "Enlarged plainly, what the network did at its smaller size is a blur: across an edge it runs out on both sides (the lift it gave a face spills onto the wall behind, in steps a network pixel wide), and inside a surface it is softer than the network made it. " +
                "With this on, each full-size pixel takes the edit from the network pixels that were looking at the same thing it shows, so the edit stays on its own side of every edge, to the pixel. It is the dearer of the two improvements (it reads the network's pixels one by one, in every pass): where the frame rate matters more than edges, leave it off and keep EditSharpening.");
            Hooks.CfgEditEdge = Config.Bind("Neural Rendering", "EditEdgeTolerance", 0.08f, new ConfigDescription(
                "With EditFollowsEdges: how unlike a full-size pixel a network pixel's input may be and still be listened to (0..1 of the picture's range). Lower follows fainter edges and can make flat, noisy areas blocky; higher goes back towards the plain enlargement.",
                new AcceptableValueRange<float>(0.01f, 0.5f)));
            Hooks.CfgEditSharpen = Config.Bind("Neural Rendering", "EditSharpening", 1f, new ConfigDescription(
                "Below 100% Model resolution: how much the fine part of the network's edit -- pores, the grain of skin, which it drew small and which comes out of the enlargement soft -- is strengthened, at 50% Model resolution (it follows the scale: none at 100%, more the lower it goes). Works with EditFollowsEdges on or off; off is the cheaper. 0 = none, and costs nothing.",
                new AcceptableValueRange<float>(0f, 4f)));
            Hooks.CfgEditHalo = Config.Bind("Neural Rendering", "EditSharpeningReach", 0.5f, new ConfigDescription(
                "With EditSharpening: how far the strengthened edit may stand out. With EditFollowsEdges, past what the network pixels around it hold, as a share of their range; without, as a cap on what the sharpening adds. 0 is the least effect and no ringing along edges; higher lets fine detail stand out more and edges begin to ring.",
                new AcceptableValueRange<float>(0f, 2f)));

            Hooks.CfgEditSteady = Config.Bind("Neural Rendering", "EditKeptOverFrames", true,
                "Below 100% Model resolution (and with a focus window): keep the network's edit from frame to frame and blend each new frame's into it, instead of laying each frame's on alone. " +
                "The network is run afresh on every frame and does not answer quite the same twice; at a lower model resolution each of its pixels is several of the screen's, and the difference shows as a crawl over skin. " +
                "What is kept is carried to where things have moved to by the network's own motion vectors, and is not believed where the new frame's edit says something else, so moving things should not trail ghosts. " +
                "Costs one small pass at the network's size a pass, and a little video memory.");
            Hooks.CfgEditSteadyTake = Config.Bind("Neural Rendering", "EditNewShare", 0.2f, new ConfigDescription(
                "With EditKeptOverFrames: how much of each new frame's edit is taken. 0.2 makes a still picture's edit the mean of about the last nine frames; lower is steadier and slower to follow a change of light, 1 keeps nothing.",
                new AcceptableValueRange<float>(0.05f, 1f)));

            Hooks.CfgEditDetail = Config.Bind("Neural Rendering", "EditDetailOverFrames", false,
                "Experimental. Below 100% Model resolution, where the network runs at a half, a third or a quarter of the frame each way (and not with a focus window): build up detail finer than the network's own pixels over a few frames. " +
                "The network's raster is shifted by a part of one of its pixels, differently every frame, so that in turn every pixel of the frame is looked at from the middle of a network pixel; the edit is kept at the frame's full size, and each pixel keeps what it got in the frame in which it was the one looked at. " +
                "It works where things hold still for a few frames: a person moving by their own animation gets that frame's edit, as without this. It costs a full-size pass a frame and video memory for two full-size pictures for each pass of the network (about 120 MB at 2560x1440 with two passes, more in a headset), " +
                "and the network sees a picture that is never in quite the same place twice, which it may or may not take well: look for shimmer.");
            Hooks.CfgEditDetailOwn = Config.Bind("Neural Rendering", "EditDetailStrength", 0.6f, new ConfigDescription(
                "With EditDetailOverFrames: how much each pixel of the frame keeps to the frame in which it was the one the network looked at. At 1 it takes all of the edit then and nothing of it in the frames between (the most detail, and the most to shimmer if the network does not answer alike from frame to frame); at 0 it is little more than the edit kept over frames.",
                new AcceptableValueRange<float>(0f, 1f)));
            Hooks.CfgWindowOutline = Config.Bind("Focus window", "ShowOutline", false,
                "Draw the focus window into the picture, for seeing where it is and what it does as the view changes: its edge in magenta, and in cyan the line inside which the network's work lands whole (between the two it fades out). For the headset's window and the monitor's.");
            Hooks.CfgEditDenoise = Config.Bind("Neural Rendering", "EditDenoise", 0f, new ConfigDescription(
                "Below 100% Model resolution: take grain out of the network's edit. Run small, the network's grain is coarser -- each speck of it covers several of the frame's pixels -- and reads as noise. " +
                "This blends the edit towards a blur of itself over a few of the network's pixels; its tone and shading stay, its speckle goes. It is the opposite of EditSharpening and is taken off it (set that to 0 to judge this alone); 1 is the blur outright. " +
                "For noise that changes from frame to frame, EditKeptOverFrames is the better tool: it averages over time and keeps what is really there.",
                new AcceptableValueRange<float>(0f, 1f)));
            Hooks.CfgInputSharpen = Config.Bind("Neural Rendering", "InputSharpening", 0f, new ConfigDescription(
                "Below 100% Model resolution: how much sharper than its exact average the network's input is made. The exact average is the honest shrink and the softest: fine contrast between the frame's pixels is gone before the network has seen it. " +
                "This gives some back, so the network has more to work from. The frame itself is not sharpened by it -- the network's edit is still measured against the same input. 0 = the plain average. Experimental: whether the network answers better or only differently is for the eye to say.",
                new AcceptableValueRange<float>(0f, 1.5f)));

            Hooks.CfgStills = Config.Bind("Neural Rendering", "ApplyToScreenshots", true,
                "Use the same model resolution for VaM's screenshot cameras (the screenshot key, save thumbnails, SuperShot). A SuperShot frame is several times the screen's size, so this is where the saving is largest; turn it off to have stills always run the network at full size.");

            Hooks.CfgEnlargement = Config.Bind("Advanced", "Enlargement", 0, new ConfigDescription(
                "How the network's work is brought back up when it ran below the frame's size. 0 = matched residual: only the network's difference is enlarged, onto the untouched full-size frame. 1 = classic: the network's own small picture is enlarged and shown, which is softer and is here for comparison.",
                new AcceptableValueRange<int>(0, 1)));

            Hooks.CfgDebugView = Config.Bind("Advanced", "DebugView", 0, new ConfigDescription(
                "Diagnostic, only while Model resolution is below 1.0. 0 = normal. 2 = show the network's edit on its own, amplified around mid-grey (grey = untouched). 3 = show the frame with the edit left out, while everything still runs. Reset to 0 at startup.",
                new AcceptableValueRange<int>(0, 3)));
            Hooks.CfgDebugView.Value = 0;

            Hooks.CfgWindow = Config.Bind("Focus window", "Size", 1f, new ConfigDescription(
                "How much of each eye, per axis, Neural Rendering works on: a window around the lens centre, where a headset is sharpest and where one mostly looks. Outside it nothing is done -- the frame is shown as VaM rendered it -- and the network's work fades out towards the window's edge.\n\n" +
                "1.0 is the whole view (no window). 0.5 is the middle half of the width and of the height: a quarter of the pixels, so about a quarter of the network's time, with ModelResolution still applying inside it. The two multiply -- a 0.5 window at 0.5 is a sixteenth.\n\n" +
                "What it costs is the look of the edge: Neural Rendering changes tone and shading as well as detail, so the window is a change of look, not only of sharpness. The fade is there to hide it.\n\n" +
                "This is the headset's window. The monitor has one of its own, with its own shape (OnMonitor, MonitorWidth, MonitorHeight). Screenshot cameras are never windowed. Applied when the slider is let go, because every change rebuilds the network.",
                new AcceptableValueRange<float>(Hooks.MinWindow, 1f)));

            Hooks.CfgWindowFeather = Config.Bind("Focus window", "EdgeSoftness", 0.35f, new ConfigDescription(
                "How far in from the window's edge the network's work fades out, as a fraction of the window's half-size. Small is a crisp edge with more of the window at full strength; large is a gentler transition. Takes effect at once.",
                new AcceptableValueRange<float>(0.05f, 1f)));

            Hooks.CfgWindowOffsetX = Config.Bind("Focus window", "OffsetX", 0f, new ConfigDescription(
                "Moves the window sideways from the lens centre, as a fraction of the eye's width: positive is towards the nose in a headset (the two eyes move opposite ways), to the right on the monitor. For a headset whose sharp spot is not where its projection says.",
                new AcceptableValueRange<float>(-0.25f, 0.25f)));

            Hooks.CfgWindowOffsetY = Config.Bind("Focus window", "OffsetY", 0f, new ConfigDescription(
                "Moves the window up (positive) or down from the lens centre, as a fraction of the eye's height.",
                new AcceptableValueRange<float>(-0.25f, 0.25f)));

            Hooks.CfgWindowMonitor = Config.Bind("Focus window", "OnMonitor", false,
                "Use a focus window on the monitor as well: MonitorWidth by MonitorHeight of the screen, aimed at the person in view (MonitorFollowPerson). Off by default: a monitor is looked at all over, a headset mostly through the middle of its lenses.");

            Hooks.CfgWindowGaze = Config.Bind("Focus window", "FollowGaze", true,
                "Aim the window where each eye is looking instead of at the lens centre, on a headset whose eye tracking reaches SteamVR (PlayStation VR2 with PSVR2Toolkit, for one). The gaze comes from SteamVR itself -- the same call other foveated-rendering tools use -- so nothing else needs installing. Without eye tracking, or outside SteamVR, the window stays at the lens centre and the status line says why.");

            Hooks.CfgGazeDeadZone = Config.Bind("Focus window", "GazeDeadZone", 0.03f, new ConfigDescription(
                "How far the gaze may wander from the window's centre before the window moves, as a fraction of the eye's picture (0.03 is about three degrees). The window does not chase every sample: an eye is never still, and each move costs the network the edge that comes into view.",
                new AcceptableValueRange<float>(0f, 0.15f)));

            Hooks.CfgGazeHold = Config.Bind("Focus window", "GazeHoldSeconds", 0.4f, new ConfigDescription(
                "How long the window stays where it is when the eye tracker loses the eye -- a blink -- before it starts back towards the lens centre.",
                new AcceptableValueRange<float>(0f, 5f)));

            Hooks.CfgGazeReturn = Config.Bind("Focus window", "GazeReturnSeconds", 0.3f, new ConfigDescription(
                "How long the window takes to travel back to the lens centre once the hold has run out.",
                new AcceptableValueRange<float>(0f, 5f)));

            Hooks.CfgMonitorWidth = Config.Bind("Focus window", "MonitorWidth", 0.45f, new ConfigDescription(
                "The monitor's focus window, when OnMonitor is set: how much of the screen's width Neural Rendering works on. A screen is wide and a figure is tall, so the monitor's window has a width and a height of its own rather than the headset's one size; the defaults make an upright window that holds a standing figure. Applied when the slider is let go.",
                new AcceptableValueRange<float>(Hooks.MinWindow, 1f)));

            Hooks.CfgMonitorHeight = Config.Bind("Focus window", "MonitorHeight", 0.9f, new ConfigDescription(
                "How much of the screen's height the monitor's focus window covers. Applied when the slider is let go.",
                new AcceptableValueRange<float>(Hooks.MinWindow, 1f)));

            Hooks.CfgMonitorFollow = Config.Bind("Focus window", "MonitorFollowPerson", true,
                "Aim the monitor's window at the person in view instead of at the middle of the screen: centred on the figure when it fits the window, on head and chest when it does not (a close-up). With several people it takes the one nearest the middle and stays with them. The window glides after the figure rather than jumping. With nobody in view it goes back to the middle.");

            Hooks.CfgMonitorDeadZone = Config.Bind("Focus window", "MonitorDeadZone", 0.04f, new ConfigDescription(
                "How far the figure may move on screen before the monitor's window starts after it, as a fraction of the screen. Keeps the window still through idle motion.",
                new AcceptableValueRange<float>(0f, 0.2f)));

            Hooks.CfgMonitorFit = Config.Bind("Focus window", "MonitorFitPeople", true,
                "Fit the monitor's window to the people in view (needs MonitorFollowPerson): it grows, shrinks and moves so that everyone is inside it, and takes whichever of three shapes -- upright, square, wide -- suits them.\n\n" +
                "The network's cost does not change with any of that. MonitorWidth x MonitorHeight then stands for the AREA the network is given, not for a window size: a small figure gets the network's pixels one for one, a figure that fills the screen gets them spread thinner (the status line says how thin). Growing, shrinking and moving are free; a change of SHAPE rebuilds the network, a hitch of a fifth of a second, so a shape is kept for a couple of seconds at least.\n\n" +
                "Off, the window is MonitorWidth x MonitorHeight of the screen and only follows.");

            Passthrough.CfgOn = Config.Bind("Passthrough", "Enabled", false,
                "In a headset whose cameras SteamVR can read (a PlayStation VR2 with PSVR2Toolkit 1.0.0 or later), show the room wherever the finished frame is the key colour: put a backdrop of that colour behind the person and the room appears in its place, with the person and the interface left as they are.\n\n" +
                "The cameras are monochrome and give 60 pictures a second; the room is drawn into the game's own frame, so what moves in it moves at the game's frame rate.");
            Passthrough.CfgMode = Config.Bind("Passthrough", "Mode", 0, new ConfigDescription(
                "How the room is shown. 0: as a SteamVR overlay of its own, drawn by a thread that keeps the camera's pace (60 pictures a second) however slowly the game runs -- SteamVR places it for the head at the headset's own rate, and the game only says where it shows. 1: drawn into the game's own frame, where it moves at the game's frame rate.",
                new AcceptableValueRange<int>(0, 1)));
            Passthrough.CfgOverlayShape = Config.Bind("Passthrough", "OverlayOtherShape", false,
                "Mode 0 only. The overlay holds the two eyes' pictures side by side, and SteamVR's rule for how tall such an overlay stands is not written down. If the room looks squashed or stretched top to bottom, turn this on.");

            Passthrough.CfgOverlayDistance = Config.Bind("Passthrough", "OverlayDistance", 10f, new ConfigDescription(
                "Mode 0 only. How far away the overlay's quad stands, in metres. SteamVR carries the game's picture to a moving head by turning it, as if it were infinitely far; a quad that stands close shifts against it when the head moves sideways, and the cut-out around the person opens on one side. Far away, the two move as one.",
                new AcceptableValueRange<float>(1f, 60f)));
            Passthrough.CfgRoomBehind = Config.Bind("Passthrough", "OverlayRoomBehind", true,
                "Mode 0 only. Draw the room into the game's own frame as well, underneath the overlay. Wherever the overlay's cut-out and the person do not quite meet, the room shows instead of the key colour.");

            Passthrough.CfgPoseSource = Config.Bind("Passthrough", "CutOutPose", 0, new ConfigDescription(
                "Which head pose the game's frame is taken to have been drawn from, when the overlay's cut-out is fitted to it: 0 the one Unity gave the camera for this frame; 1 the one SteamVR last handed out. If they differ, the wrong one makes the cut-out run ahead of the person, or trail, while the head turns.",
                new AcceptableValueRange<int>(0, 1)));
            Passthrough.CfgPoseTiming = Config.Bind("Passthrough", "CutOutTimingMs", 0f, new ConfigDescription(
                "Moves that pose along the head's turning by this many milliseconds. If the person is cut on the side away from the turn while the head moves, lower it; if on the side of the turn, raise it.",
                new AcceptableValueRange<float>(-80f, 80f)));

            Passthrough.CfgPreset = Config.Bind("Passthrough", "KeyPreset", 1, new ConfigDescription(
                "The key colour, by name: 0 as the three numbers below stand, 1 green, 2 blue, 3 magenta, 4 black, 5 white. Choosing one writes the three numbers.",
                new AcceptableValueRange<int>(0, 5)));
            Passthrough.CfgRed = Config.Bind("Passthrough", "KeyRed", 0f, new ConfigDescription("The key colour's red, as displayed (0 to 1).", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgGreen = Config.Bind("Passthrough", "KeyGreen", 1f, new ConfigDescription("The key colour's green, as displayed (0 to 1).", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgBlue = Config.Bind("Passthrough", "KeyBlue", 0f, new ConfigDescription("The key colour's blue, as displayed (0 to 1).", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgTolerance = Config.Bind("Passthrough", "Tolerance", 0.3f, new ConfigDescription(
                "How far a pixel's colour may be from the key colour and still be replaced. Raise it if patches of the backdrop are left; lower it if parts of the person turn into the room.",
                new AcceptableValueRange<float>(0.02f, 1f)));
            Passthrough.CfgSoftness = Config.Bind("Passthrough", "EdgeSoftness", 0.15f, new ConfigDescription(
                "How gradually the room fades in beyond the tolerance: the width of the soft edge around the person.",
                new AcceptableValueRange<float>(0f, 0.6f)));
            Passthrough.CfgDistance = Config.Bind("Passthrough", "Distance", 1.5f, new ConfigDescription(
                "How far away the room is taken to be, in metres. The cameras are not where the eyes are, so things at this distance line up with where they really are; nearer and farther things are slightly off.",
                new AcceptableValueRange<float>(0.3f, 10f)));
            Passthrough.CfgBrightness = Config.Bind("Passthrough", "Brightness", 1f, new ConfigDescription("Camera brightness.", new AcceptableValueRange<float>(0.2f, 4f)));
            Passthrough.CfgHandOver = Config.Bind("Passthrough", "OverlayHandOver", 0, new ConfigDescription(
                "Mode 0: how the room's pictures reach SteamVR. 0: by their shared handles, so that SteamVR reads them where they are. 1: as textures that SteamVR's own library copies, as it was up to 1.8 -- " +
                "that copying shares its machinery with the game's own pictures, and the overlay's graphics device was lost by it whenever VaM DLSS ran in a mode below DLAA (the room stuttered or froze) and now and then otherwise. " +
                "Set 1 only if the room does not show at all with 0.",
                new AcceptableValueRange<int>(0, 1)));
            Passthrough.CfgLook = Config.Bind("Passthrough", "Look", 0, new ConfigDescription(
                "What the room is shown in. The cameras are monochrome: 0 their grey as it comes; 1 night vision (green); 2 amber; 3 cold blue; 4 sepia; 5 heat (black through violet and orange to white, by brightness); " +
                "6 your colour (LookRed, LookGreen, LookBlue); 7 colours guessed by a network -- experimental: a network trained to colour black-and-white photographs looks at a small copy of the left camera's picture about three times a second, " +
                "on two of the processor's threads, and its colours are laid under the camera's own brightness. They are a guess (often a wrong one: the cameras see infrared too), they lag the head by a moment, " +
                "and things nearer than the passthrough's distance get their colour a little to the side in the right eye. Needs colour.onnx beside the plugin (270 MB, not part of the download: see the README).",
                new AcceptableValueRange<int>(0, 7)));
            Passthrough.CfgLookRed = Config.Bind("Passthrough", "LookRed", 1f, new ConfigDescription("Look 6, your colour: its red, as displayed (0 to 1). Mid-grey becomes this colour, black stays black, white nearly white.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgLookGreen = Config.Bind("Passthrough", "LookGreen", 0.3f, new ConfigDescription("Look 6, your colour: its green.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgLookBlue = Config.Bind("Passthrough", "LookBlue", 0.6f, new ConfigDescription("Look 6, your colour: its blue.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgGrain = Config.Bind("Passthrough", "LookGrain", 0f, new ConfigDescription(
                "Grain over the room's picture, new with every picture, as an image intensifier has. For every look but the camera's grey.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgRim = Config.Bind("Passthrough", "LookDarkRim", 0f, new ConfigDescription(
                "How much darker the room gets towards the rim of the view, as through a tube. For every look but the camera's grey.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgColour = Config.Bind("Passthrough", "LookColourStrength", 1f, new ConfigDescription(
                "Look 7: how strongly the network's colours are shown. 1 as it gives them; lower for a paler, calmer picture (its mistakes show less); higher for more colour.", new AcceptableValueRange<float>(0f, 2f)));
            Passthrough.CfgFocal = Config.Bind("Passthrough", "LensFocal", 382.6f, new ConfigDescription(
                "The cameras' fisheye scale, in pixels per radian for a 1016-pixel-wide lens picture. Measured on a PlayStation VR2; it sets the room's apparent size -- raise it if the room looks too small, lower it if too large.",
                new AcceptableValueRange<float>(200f, 600f)));
            Passthrough.CfgFollowHead = Config.Bind("Passthrough", "FollowHead", false,
                "Carry the camera's picture from where the head was when it was taken to where the head is now, so a turning head does not drag the room with it. Off, the room is drawn as if no time had passed.");
            Passthrough.CfgView = Config.Bind("Passthrough", "View", 0, new ConfigDescription(
                "0 the picture; 1 the matte (white where the room will show) for setting the tolerance; 2 the camera everywhere, for looking at the room alone; 3 how near the room is taken to be (bright = near); 4 how near the scene is.",
                new AcceptableValueRange<int>(0, 4)));
            Passthrough.CfgDepth = Config.Bind("Passthrough", "Depth", false,
                "Experimental, Mode 0 only. Work out how far away the room is from the two cameras, and let whatever in it is nearer than the figure show in front of the figure: a hand held out before it covers it. Costs some frame rate: the game has to keep a depth picture of the scene, and stays doing so until it is restarted.");
            Passthrough.CfgDepthMargin = Config.Bind("Passthrough", "DepthMargin", 0.25f, new ConfigDescription(
                "How much nearer than the figure a thing in the room must be to show in front of it, as a difference of one-over-the-distance: 0.25 is a quarter of a metre at one metre, six centimetres at half a metre. Raise it if the room breaks through the figure where it should not.",
                new AcceptableValueRange<float>(0f, 2f)));
            Passthrough.CfgDepthSoftness = Config.Bind("Passthrough", "DepthSoftness", 0.1f, new ConfigDescription(
                "Over how much more than the margin the room fades in.", new AcceptableValueRange<float>(0f, 1f)));
            Passthrough.CfgDepthFlip = Config.Bind("Passthrough", "DepthUpsideDown", false,
                "For troubleshooting: take the scene's depth picture the other way up. Set it if View 4 shows the scene's depth upside down.");
            Passthrough.CfgPreset.SettingChanged += delegate { Passthrough.ApplyPreset(); };

            Hands.CfgOn = Config.Bind("Hands", "Tracking", false,
                "Experimental. Find the wearer's hands in the headset's cameras (a PlayStation VR2 with PSVR2Toolkit 1.0.0 or later; the same cameras passthrough uses). For now they are only shown, as a skeleton in the scene, to judge the tracking by. " +
                "The finding is done on the processor (ONNX Runtime, two background threads), not the graphics card. It needs onnxruntime.dll, hand-palm.onnx and hand-points.onnx beside the plugin.");
            Hands.CfgFull = Config.Bind("Hands", "FullModel", false,
                "Read when hand tracking is first switched on after VaM starts: find the fingers with MediaPipe's full-size landmark model instead of the small one. " +
                "It tells fingers apart better and costs about twice the processor time a look. Needs hand-points-full.onnx beside the plugin; without that file the small model is used.");
            Hands.CfgMercury = Config.Bind("Hands", "Mercury", false,
                "Read when hand tracking is first switched on after VaM starts: follow a hand, once it is found, with Mercury's keypoint network (from Monado, the open-source VR runtime) instead of MediaPipe's. " +
                "It was made for a headset's own cameras -- grey, wide, the hand often a dark shape against a window -- and on such pictures it does not turn the hand over as the other does, nor take a cloth or a screen for a hand. " +
                "Needs hand-mercury.onnx beside the plugin (Monado's grayscale_keypoint_jan18.onnx, renamed); without that file nothing changes.");
            HandDrive.CfgCurls = Config.Bind("Hands", "FingersByCurl", true,
                "With Mercury following: bend VaM's fingers by what Mercury says of each finger as a whole (how far it is bent), not by where it puts the finger's joints in the picture. " +
                "For a finger folded into a fist those places are guesses; the one number is what tells a fist, a thumb held up, or two fingers held out from an open hand.");
            HandDrive.CfgPinch = Config.Bind("Hands", "PinchAssist", true,
                "Bring the thumb's tip and the index finger's together on VaM's hand when the tracker has them nearly touching (PinchClosed), and part of the way up to PinchOpen. The tracker's fingertips stop short of touching, and VaM's hand has fingers of its own lengths: while the tracker has a pinch, VaM's own two fingertips are measured and curled until they meet.");
            HandDrive.CfgPinchClosed = Config.Bind("Hands", "PinchClosed", 0.045f, new ConfigDescription(
                "Metres between the tracker's thumb tip and index tip under which the pinch is closed. On recordings a real pinch read 2 to 4.5 cm (under 3.5 cm in only three frames of sixty of the last one).",
                new AcceptableValueRange<float>(0.005f, 0.08f)));
            HandDrive.CfgPinchOpen = Config.Bind("Hands", "PinchOpen", 0.08f, new ConfigDescription(
                "Metres from which the two fingers are left as the tracker has them.",
                new AcceptableValueRange<float>(0.02f, 0.15f)));
            Hands.CfgShow = Config.Bind("Hands", "ShowSkeleton", true,
                "Draw the tracked hands in the scene: lines along the fingers, a dot at each joint. Blue is the left hand, orange the right.");
            Hands.CfgBoth = Config.Bind("Hands", "BothHands", true,
                "Look for two hands. Off: one hand only, at about half the cost.");
            Hands.CfgEvery = Config.Bind("Hands", "EveryNthFrame", 2, new ConfigDescription(
                "The cameras give 60 frames a second; the hands are looked for in every so many of them. 1 is the smoothest and takes the most of the processor, 2 is 30 looks a second.",
                new AcceptableValueRange<int>(1, 4)));
            Hands.CfgSmoothing = Config.Bind("Hands", "Steadiness", 1.5f, new ConfigDescription(
                "How slow a movement is taken for jitter and smoothed away, in Hz: lower holds a still hand steadier and makes it lag more.",
                new AcceptableValueRange<float>(0.2f, 10f)));
            Hands.CfgQuick = Config.Bind("Hands", "Quickness", 20f, new ConfigDescription(
                "How much less a moving hand is smoothed, the faster it moves: higher follows quick movements more closely and lets more jitter through while moving.",
                new AcceptableValueRange<float>(0f, 100f)));
            Foveation.CfgOn = Config.Bind("Foveation", "Enabled", false,
                "Shade the scene at full rate only where the eyes look, and more coarsely around that (NVIDIA variable rate shading; GTX 16 / RTX 20 series or later). Edges and depth stay at full resolution; only the shading inside surfaces gets coarser. " +
                "It saves the card's time on the scene itself -- skin, hair, lights -- not on DLSS or Neural Rendering. It applies to the scene camera's own geometry only: shadows, image effects and the menu drawn at full size are untouched. " +
                "With DLSS upscaling on as well the two coarsenings add up.");
            Foveation.CfgGaze = Config.Bind("Foveation", "FollowGaze", true,
                "Aim it where the eyes look, from SteamVR's eye tracking (a PlayStation VR2 with PSVR2Toolkit, for one). Without eye tracking, or with this off, it is aimed at the middle of each lens.");
            Foveation.CfgInner = Config.Bind("Foveation", "Inner", 0.22f, new ConfigDescription(
                "How far from where the eye looks the shading stays at full rate, as a share of the picture's height.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            Foveation.CfgOuter = Config.Bind("Foveation", "Outer", 0.42f, new ConfigDescription(
                "Beyond this the coarsest rate is used; between Inner and this, the middle one.",
                new AcceptableValueRange<float>(0.05f, 1.5f)));
            Foveation.CfgStrong = Config.Bind("Foveation", "Strong", true,
                "Beyond Outer, shade as coarsely as the picture's anti-aliasing allows (one shading for 4x4 pixels without anti-aliasing, 2x2 at 4x). Off: no coarser than the ring between Inner and Outer.");
            Foveation.CfgShow = Config.Bind("Foveation", "ShowZones", false,
                "For setting it up: beyond Outer the scene is not shaded at all, so the zone shows as a hole around where the eyes look.");
            Foveation.CfgTopDown = Config.Bind("Foveation", "UpsideDown", false,
                "Turn on if the zone moves down when the eyes look up.");
            Foveation.CfgMonitor = Config.Bind("Foveation", "OnMonitor", false,
                "Also in desktop mode, around the middle of the window.");

            SceneUi.CfgOn = Config.Bind("SceneUi", "AfterDlss", false,
                "Keep the scene's interface atoms (the UIButton, UISlider, UIToggle, UIText and UIImage a scene is built with) out of what DLSS and Neural Rendering work on: " +
                "they are drawn afterwards onto the finished picture, at its full size, as the menu is. Without it their text is rendered small by a DLSS quality mode, upscaled, and reworked by Neural Rendering with the rest of the scene. " +
                "In a headset and on the monitor, while VaM DLSS is at work.");
            SceneUi.CfgOcclude = Config.Bind("SceneUi", "HiddenBehindPeople", true,
                "Lay the scene's depth under them before they are drawn, so that a button on a wall stays behind whoever stands in front of it, as in plain VaM. " +
                "With a DLSS quality mode the edge where something covers them follows the lower size the scene is rendered at. Off: they are always on top.");
            SceneUi.CfgAtoms = Config.Bind("SceneUi", "Atoms", "UIButton,UIButtonImage,UISlider,UIToggle,UIText,UIImage",
                "The kinds of atom this is done for, by VaM's name for them, separated by commas.");
            SceneUi.CfgLayer = Config.Bind("SceneUi", "Layer", 19, new ConfigDescription(
                "The layer their canvases are moved to while this is on. It has to be one VaM leaves unused: 3, 6, 7, 18 or 19. Change it if another plugin uses 19.",
                new AcceptableValueRange<int>(1, 31)));
            SceneUi.CfgSlack = Config.Bind("SceneUi", "DepthSlack", 0.01f, new ConfigDescription(
                "How far back the scene's depth is pushed before they are drawn, as a fraction of its distance. It keeps a panel that lies close on a surface from being cut into stripes; raise it if you see that, lower it if they show through things just in front of them.",
                new AcceptableValueRange<float>(0f, 0.2f)));
            SceneUi.CfgFlip = Config.Bind("SceneUi", "DepthUpsideDown", false,
                "Turn on if they are hidden by things above or below them instead of things in front of them.");

            Presentation.CfgFlip = Config.Bind("Presentation", "FlipModel", false,
                "Monitor mode only, and read when VaM starts: present the game's window with the flip model instead of Unity's old bit-block copy. " +
                "With the old way the desktop compositor drops most frames above the base rate -- frame generation renders more frames and the picture gets no smoother -- and RTX HDR cannot take the window. " +
                "Needs VaM's own anti-aliasing off (DLSS does that job) and the file in BepInEx\\patchers that comes with this plugin.");

            Presentation.CfgPace = Config.Bind("Presentation", "FramePacing", true,
                "Monitor mode, with the flip-model window and VaM DLSS's frame generation on: spread the generated frames evenly on the screen. " +
                "Frame generation presents its frames as it makes them -- a few in quick succession, then nothing while the next real frame is rendered -- which looks like the base rate however many frames there are. " +
                "With this on they wait in the window's queue and come out in rhythm, one every so many screen refreshes, while the game is already rendering the next frame. " +
                "It costs the time a frame waits (up to about one rendered frame), and it replaces VaM's own VSync setting and VaM DLSS's \"space frames evenly\", which is best switched off with it.");

            ControlPanel.CfgFoldBelow = Config.Bind("Interface", "PanelBelow100Open", false,
                "Whether the in-headset panel's section of options for a model resolution below 100% is unfolded; it opens as it was left.");
            ControlPanel.CfgPage = Config.Bind("Interface", "PanelPage", 0, new ConfigDescription(
                "The page the in-headset panel was last on; it opens there again.",
                new AcceptableValueRange<int>(0, 16)));

            HandDrive.CfgMode = Config.Bind("Hands", "Drive", 1, new ConfigDescription(
                "Who moves VaM's own hands while hand tracking is on. 0 Controllers: the tracked hands are only shown. 1 Auto: a controller that is being moved or squeezed has its hand; a hand the cameras see, whose controller lies still, follows the cameras. 2 Hands: the cameras have every hand they see. " +
                "It is done through VaM's Leap Motion hands, without a Leap sensor and without touching VaM's Leap switch. To touch and push a person with them, VaM's own hand collision has to be on (User Preferences).",
                new AcceptableValueRange<int>(0, 2)));
            HandUi.CfgOn = Config.Bind("Hands", "Menus", false,
                "Not working yet. While a tracked hand drives VaM's hand: point at VaM's menus with it (a line from the hand shows where), pinch thumb and index to click, hold the pinch to drag a slider or scroll. " +
                "Turn the left palm to your face and pinch to open or close the menu. The controller's own pointer comes back the moment the controller is picked up.");
            Hands.CfgSwap = Config.Bind("Hands", "SwapLeftRight", false,
                "Turn on if the left hand is shown as the right one (orange) and the right as the left (blue).");

            HeadsetUi.CfgSharpen = Config.Bind("Picture", "Sharpening", 0f, new ConfigDescription(
                "Sharpen the finished frame, after DLSS and Neural Rendering and before the interface is drawn: 0 is off, 1 the most. DLSS has no sharpening of its own any more, and its picture -- DLAA's most of all -- is on the soft side.\n\n" +
                "The filter is contrast-adaptive: soft detail gains the most, flat areas and edges that are already hard are left nearly alone, and nothing is pushed beyond the darkest and brightest of its neighbours, so it does not ring.",
                new AcceptableValueRange<float>(0f, 1f)));

            SceneUi.CfgPicture = Config.Bind("Advanced", "SaveWindowPictureAfterSeconds", 0, new ConfigDescription(
                "For support: save the game window's own picture once, this many seconds after VaM has started, as window-picture.png beside the plugin (0: never). Monitor mode.",
                new AcceptableValueRange<int>(0, 3600)));

            DlssWindow.CfgOn = Config.Bind("DLSS window", "Enabled", false,
                "Experimental, headset at DLAA only. Run DLSS on a window around the middle of each eye instead of on the whole eye: DLSS's cost goes by the pixels it reconstructs, and a window half the eye's width has a quarter of them. " +
                "Outside the window the scene is shown as it was rendered -- not anti-aliased, and trembling by the fraction of a pixel DLSS has the camera shaken by; the window's edge is a hard one. A first version, to see what it buys in frame rate.");
            DlssWindow.CfgSize = Config.Bind("DLSS window", "Size", 0.5f, new ConfigDescription(
                "The window's width and height as a share of the eye's. 0.5 is a quarter of the pixels. Changing it has DLSS set up anew.",
                new AcceptableValueRange<float>(0.25f, 1f)));
            DlssWindow.CfgGaze = Config.Bind("DLSS window", "FollowGaze", false,
                "Put the window where the eye looks, when SteamVR has eye tracking for the headset, instead of at the lens centre. It moves in steps, when the eye has left its middle, and DLSS starts its history anew at every step: a moment of rougher picture each time.");

            Profile.CfgSeconds = Config.Bind("Profile", "Seconds", 30, new ConfigDescription(
                "How long a run of the main-thread profile lasts (the panel's button 'Profile the main thread', DLSS page). For that long every script's FixedUpdate, Update, LateUpdate and render callbacks are timed and the frame's phases marked off; " +
                "what it finds is written to BepInEx\\LogOutput.log every ten seconds (lines beginning 'profile:'). Nothing is measured, and nothing costs anything, outside a run.",
                new AcceptableValueRange<int>(5, 600)));
            Profile.CfgScripts = Config.Bind("Profile", "Scripts", true,
                "Time every script by name during a run. Off: only the frame's phases are marked off, which costs next to nothing but does not say which script it is.");
            Profile.CfgPlugins = Config.Bind("Profile", "ScenePlugins", true,
                "During a run, also time the scripts of scene and session plugins (the ones VaM compiles as it loads them). Off: they are left alone and their time is counted as the engine's; the log names them.");
            Profile.CfgTry = Config.Bind("Profile", "Trials", true,
                "After a run's own time, try the scene with one thing changed for five seconds each, an unchanged five seconds before and after each, and say what each comes to: at most one physics step a frame (the scene runs a little slow where a frame is longer than a step), and the physics solver's iterations at half of what every body has. " +
                "Everything is put back at the end. While the second runs the physics is looser than the scene was made for: soft parts may wobble or sag for those seconds.");
            const string inside = "SuperController, SuperController.AssignUICamera, LookInputModule, LookInputModule.GetLookPointerEventData, UnityEngine.EventSystems.EventSystem.RaycastAll, UnityEngine.UI.GraphicRaycaster.Raycast";
            Profile.CfgInside = Config.Bind("Profile", "LookInside", inside,
                "During a run, also time the steps inside these, with commas between. A class of VaM's: its methods named Process..., Check..., Sync..., Prep..., Verify..., Handle... and Apply.... 'Class.Method': that one method, of VaM's or (with its full name) of Unity's. " +
                "For finding what a script that takes long is spending it on. Empty: nothing. As it comes: SuperController, and what its ProcessUI calls for the menu pointers.");

            if (Profile.CfgInside.Value.Trim() == "SuperController")
            {
                Profile.CfgInside.Value = inside; // what it came as before the menu pointers were looked into
            }

            MenuPointers.CfgOn = Config.Bind("Menu pointers", "RestOffMenus", false,
                "Experimental, headset only. Every frame VaM asks for each controller and for the mouse what it points at, and goes through every menu of the scene for each: 2.4-4 ms of a frame in a heavy scene. On: a pointer that is not on a menu is asked on one frame in four only, " +
                "and every frame again from the moment it is on one (so the pointer's dot may appear up to three frames late as it comes onto a menu). Never while it holds something, while a text field has the keyboard, or while the mouse moves.");
            Profile.CfgStartAfter = Config.Bind("Profile", "StartAfterSeconds", 0, new ConfigDescription(
                "Start one run by itself this many seconds after VaM has started (0: only the panel's button starts one). For a run without the panel: set it, start VaM, load the scene within that time, read the log, set it back to 0.",
                new AcceptableValueRange<int>(0, 3600)));

            EyeSize.CfgCorrect = Config.Bind("Headset", "CorrectEyeSize", true,
                "VaM DLSS measures the size of the headset's picture once and reconstructs to it for the rest of the session. Started with a DLSS upscaling mode already on, VaM can still have eye textures of half the size at that moment, and the headset then gets a coarse, pixelated picture whatever is chosen afterwards (until VaM's own Render Scale is moved, or VaM is restarted). " +
                "On: the measurement is checked against the size SteamVR gives for an eye, and written anew where SteamVR and the eye texture Unity has both say otherwise. " +
                "Likewise the eye scale VaM DLSS multiplies its DLSS ratio onto: where it has come to hold another than VaM's Render Scale (seen after changing the quality: Ultra Performance at a ninth of the size instead of a third), it is given VaM's. Said in the log when either happens, and as often as it happens (four times within twenty seconds at most, so as not to fight over it with something else). SteamVR headsets only.");
            Logs.OwnName = Logger.SourceName;
            Logs.CfgOwn = Config.Bind("Logs", "ThisPlugin", true,
                "Write this plugin's lines to BepInEx\\LogOutput.log and to Unity's output_log.txt. Off: only its errors are written, and what the profiler was asked for by its button. " +
                "(The BepInEx console, where it is open, shows everything either way.) Leave it on when something is to be looked into: the log is what says how a session went wrong.");
            Logs.CfgMod = Config.Bind("Logs", "VamDlss", true,
                "Write VaM DLSS's lines to BepInEx\\LogOutput.log and to Unity's output_log.txt. Off: only its errors are written. VaM DLSS itself has no setting for this; its lines are let fall where BepInEx hands them to its file writers.");
            Logs.CfgMotionFile = Config.Bind("Logs", "VamDlssMotionFile", true,
                "Let VaM DLSS write its motion statistics to BepInEx\\plugins\\VamDlssNr\\vr_motion.log (up to 8 MB, and as much again in vr_motion.log.1). Off: the file is not written; the lines still go to the BepInEx log, where VamDlss above decides.");
            Logs.CfgNativeFiles = Config.Bind("Logs", "VamDlssNativeFiles", true,
                "Let VaM DLSS's native half write ngx\\vdn.log and ngx\\vdn_fg.log in its folder. Off: its writes to those two files are answered as done and not made (in memory, in this process; nothing of VaM DLSS's on disk is changed). The files stay, empty.");
            Logs.CfgNgxFiles = Config.Bind("Logs", "NvidiaFiles", true,
                "Let NVIDIA's DLSS libraries write ngx\\nvngx.log and ngx\\nvngx_dlss_*.log in VaM DLSS's folder. Off: their writes to those files are answered as done and not made, the same way. " +
                "Switched off while VaM runs it holds from the next line on; set before VaM starts, from the first.");
            EyeSize.CfgKeep = Config.Bind("Headset", "KeepSizeWithoutFocus", true,
                "VaM's SteamVR plugin halves the headset's eye scale whenever SteamVR takes the input focus away -- for its dashboard, and during every scene load and long freeze -- and gives back what it kept when the focus returns. " +
                "It has been seen to give back its own half instead: the headset stays at half size (a coarse, pixelated picture, with or without a DLSS mode) until VaM's Render Scale slider is moved; and under a DLSS upscaling mode VaM DLSS takes the half for your scale. " +
                "Each halving also has every picture of VaM DLSS made anew, twice. On: the eye scale is left alone while SteamVR has the focus (the scene goes on at full size behind the dashboard). " +
                "Off: it is halved as before, and given back as it was before the first halving. Lines beginning 'eye size: SteamVR' in the log say each time.");
            EyeSize.CfgLog = Config.Bind("Headset", "EyeSizeLog", true,
                "Write a line to the log whenever the headset's eye texture, its eye scale, SteamVR's size for an eye, VaM's Render Scale or VaM DLSS's own measurement changes (lines beginning 'eye size:'; 160 a session at most). For finding out how a session came to a wrong picture size.");

            HeadsetUi.CfgMethod = Config.Bind("Interface", "HeadsetDrawWhen", 0, new ConfigDescription(
                "When FullSizeInHeadset draws the interface: 0 at the end of the frame, with the single-pass stereo shader keyword off (the way that works); 1 at the end of the frame with the keyword left alone; 2 at once, inside VaM DLSS's own frame. For finding what a given headset runtime needs -- leave at 0.",
                new AcceptableValueRange<int>(0, 2)));

            HeadsetUi.CfgOn = Config.Bind("Interface", "FullSizeInHeadset", false,
                "Draw VaM's interface apart from the scene in a headset, the way VaM DLSS does on the monitor: its layers ([General] UILayerMask in VaM DLSS's file) come off the scene and are drawn afterwards, at the headset's full size, onto the frame DLSS and Neural Rendering have finished with. The menu is then neither rendered small by a DLSS quality mode nor reworked by either.\n\n" +
                "The interface is drawn with nothing of the scene's depth behind it, so it is always on top: a hand or a person in front of the menu does not cover it.");

            // The in-headset panel needs none of what follows: it is offered VamDlssNr's settings
            // even when the frame hook below cannot go in.
            ControlPanel.Watch();

            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Native.Load(dir))
            {
                Logger.LogError("[vws] " + Native.Problem + " -- Neural Rendering runs at full size");
            }

            string missing = Hooks.Resolve();

            if (missing != null)
            {
                Hooks.Problem = "model resolution is off: this build of VamDlssNr has no " + missing;
                Logger.LogError("[vws] " + Hooks.Problem + " -- nothing was patched and VamDlssNr runs as it ships");
                return;
            }

            Harmony harmony = new Harmony(Guid);

            try
            {
                Hooks.Apply(harmony);
            }
            catch (Exception ex)
            {
                Hooks.Hooked = false;
                Hooks.Problem = "model resolution is off: VamDlssNr could not be hooked (" + ex.GetType().Name + ")";
                Logger.LogError("[vws] " + Hooks.Problem + ": " + ex.Message + "\n" + ex.StackTrace);

                // Half a hook is worse than none: take back whatever did go in.
                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception)
                {
                }

                return;
            }

            // The headset's interface pass stands apart, with a Harmony of its own: if it cannot
            // go in, it alone is taken back out.
            HeadsetUi.Resolve();
            HeadsetUi.Apply(new Harmony(Guid + ".headsetui"));

            // DLSS on a window of each eye: likewise apart, and likewise taken back out if it cannot go in.
            DlssWindow.Apply(new Harmony(Guid + ".dlsswindow"));
            MenuPointers.Apply(new Harmony(Guid + ".menupointers"));
            EyeSize.Apply(new Harmony(Guid + ".eyesize"));
            Logs.Apply(new Harmony(Guid + ".logs"));

            if (Logs.Problem.Length != 0)
            {
                Logger.LogWarning("[vws] " + Logs.Problem);
            }

            Logs.Tick(0f);

            Foveation.Begin();

            try
            {
                HandDrive.Apply(new Harmony(Guid + ".hands"));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[vws] hands: VaM's Leap hand rule could not be hooked (" + ex.GetType().Name + ": " + ex.Message + ") -- the tracked hands will only be shown");
            }

            HandUi.Apply(new Harmony(Guid + ".handui"));

            if (HeadsetUi.Problem.Length != 0)
            {
                Logger.LogWarning("[vws] " + HeadsetUi.Problem);
            }

            if (HeadsetUi.Hooked)
            {
                StartCoroutine(EndOfFrames());
            }

            if (Hooks.PanelProblem.Length != 0)
            {
                Logger.LogWarning("[vws] " + Hooks.PanelProblem);
            }

            if (Hooks.WindowProblem.Length != 0)
            {
                Logger.LogWarning("[vws] " + Hooks.WindowProblem);
            }

            float window = Hooks.QuantiseWindow(Hooks.CfgWindow.Value);
            Logger.LogInfo("[vws] VaM DLSS - Model Resolution " + Version + ": hooked VamDlssNr (" + Hooks.ComposeSites + " output reads redirected, focus window " + (Hooks.WindowHooked ? "available" : "NOT available") + "), native " + (Native.Loaded ? "loaded" : "MISSING") +
                ", model resolution " + Hooks.Percent(Hooks.Quantise(Hooks.CfgScale.Value, 2f)) + ", focus window " + (window >= 1f ? "off" : Hooks.Percent(window)));

#if VWS_SELFTEST
            SelfTest.Begin(this);
#endif
        }

        private void OnApplicationQuit()
        {
            Hands.Stop();
            Passthrough.Stop();
            SceneUi.Stop();
        }

        // The headset's interface is drawn once every camera has rendered (see HeadsetUi).
        private IEnumerator EndOfFrames()
        {
            WaitForEndOfFrame wait = new WaitForEndOfFrame();

            while (true)
            {
                yield return wait;
                HeadsetUi.EndOfFrame();
            }
        }

        // VaM's own fingertips brought together while the tracker has a pinch (see HandDrive.Late):
        // after every Update, VaM's included.
        private void LateUpdate()
        {
            HandDrive.Late(Time.unscaledTime);
        }

        private void Update()
        {
            // Nothing, unless the panel's button started a probe.
            CameraProbe.Tick(Time.unscaledTime);

            Foveation.Tick(Time.unscaledTime);
            Presentation.Tick(Time.unscaledTime);
            SceneUi.Tick(Time.unscaledTime);
            EyeSize.Tick(Time.unscaledTime);
            Logs.Tick(Time.unscaledTime);
            DlssWindow.Tick(Time.unscaledTime);
            Profile.Tick(Time.unscaledTime);

            // The wearer's hands, when they are being tracked: it holds the camera, so before the tick
            // that would give it back.
            try
            {
                Hands.Tick(Time.unscaledTime);
            }
            catch (Exception ex)
            {
                Hands.Fail(ex);
            }

            HandUi.Tick(Time.unscaledTime);

            // Gives the headset's camera back once nothing has asked for it for a while.
            Passthrough.Tick(Time.unscaledTime);

            // The in-headset panel stands apart from the rest: a fault in it takes only it down.
            if (!ControlPanel.Off)
            {
                try
                {
                    ControlPanel.Tick();
                }
                catch (Exception ex)
                {
                    ControlPanel.Off = true;
                    Logger.LogError("[vws] the in-headset control panel failed and is off for this session: " + ex.GetType().Name + " -- " + ex.Message + "\n" + ex.StackTrace);
                }
            }

            try
            {
                Hooks.Tick();

                if (Time.unscaledTime >= _nextSweep)
                {
                    _nextSweep = Time.unscaledTime + 0.5f;
                    Hooks.Sweep();
                }

                if (Native.Loaded && Time.unscaledTime >= _nextDrain)
                {
                    _nextDrain = Time.unscaledTime + 0.25f;
                    string text = Native.DrainLog();

                    if (text != null)
                    {
                        string[] lines = text.Split('\n');

                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Length != 0)
                            {
                                Logger.LogInfo("[vws native] " + lines[i]);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[vws] update failed: " + ex.GetType().Name + " -- " + ex.Message);
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            try
            {
                Config.Save();
                Hooks.ReleaseAll();
            }
            catch (Exception)
            {
            }
        }
    }
}
