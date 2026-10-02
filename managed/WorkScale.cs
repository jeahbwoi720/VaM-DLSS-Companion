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
// The two image passes live in VamDlssNrWorkScaleNative.dll and run on Unity's render thread; see
// native/vws.cpp for why nothing here touches D3D11 directly.

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
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VwsHostTest")]

namespace VamDlssNrWorkScale
{
    internal static class Native
    {
        private const string Dll = "VamDlssNrWorkScaleNative";
        private const uint ExpectedAbi = 2;

        internal const uint Frame = 1, Proxy = 2, Model = 4, Result = 8;
        internal const int ReadyDown = 1, ReadyResolve = 2;

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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushPassFn(uint resolve, uint set, uint eyes, float strength, uint mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PushReleaseFn(uint set);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PollFn(uint set, out int ready, out int error, out uint token, out int lastHr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DrainFn([In, Out] byte[] buffer, int capacity);

        private static PushRegisterFn _pushRegister;
        private static PushPassFn _pushPass;
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
                _pushPass = (PushPassFn)Bind(module, "vws_push_pass", typeof(PushPassFn));
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

        internal static int PushPass(bool resolve, uint set, int eyes, float strength, int mode)
        {
            return _pushPass(resolve ? 1u : 0u, set, (uint)eyes, strength, (uint)mode);
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
        internal bool Live;
        internal int LiveFrame = -1;
        internal float LastUsed;
        internal bool Broken;

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
                }

                return true;
            }

            _behind = 0;

            // It has seen everything sent, so what it reports is the verdict on it. The error alone
            // is not enough: it describes the last registration only, and a first half that was
            // refused is hidden by a second half that went through. What is ready is the whole truth.
            int expected = (_sentIn ? Native.ReadyDown : 0) | (_sentIn && _sentOut ? Native.ReadyResolve : 0);

            if (error == 0 && (ready & expected) == expected)
            {
                return true;
            }

            Hooks.Fail(this, error != 0
                ? Native.ErrorName(error) + (hr != 0 ? " (hr=0x" + hr.ToString("X8") + ")" : "")
                : "the " + ((ready & Native.ReadyDown) == 0 ? "frame or model input" : "model output or result") + " texture was refused (the [vws native] lines in the log say why)");
            return false;
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
            return true;
        }

        internal void Downsample()
        {
            // TestFlat is the self-test's: it replaces the model's input with a flat grey so the
            // timing of the model's answer can be read off the screen. Never set in normal use.
            if (Hooks.TestFlat >= 0f)
            {
                Native.IssuePass(Native.PushPass(false, Set, Eyes, Hooks.TestFlat, 9));
            }
            else
            {
                Native.IssuePass(Native.PushPass(false, Set, Eyes, 1f, 0));
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

            Native.IssuePass(Native.PushPass(true, Set, Eyes, 1f, mode));
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

        internal static Action<string> Info = delegate { };
        internal static Action<string> Warn = delegate { };
        internal static Action<string> Error = delegate { };

        internal static ConfigEntry<float> CfgScale;
        internal static ConfigEntry<bool> CfgStills;
        internal static ConfigEntry<int> CfgEnlargement;
        internal static ConfigEntry<int> CfgDebugView;

        // Whether the frame hook is in place. Without it the slider is shown but explains itself.
        internal static bool Hooked;
        internal static string Problem = "";
        internal static string PanelProblem = "";

        private static float _applied = 1f;
        private static int _failures;
        private static int _faults;
        private static int _composeSites;

        private static readonly Dictionary<int, View> _views = new Dictionary<int, View>();
        private static readonly List<int> _scratch = new List<int>();

        private static FieldInfo F_output, F_primary, F_failed;
        private static MethodInfo M_run, M_compose, M_outputFor;

        // NrCapture._nrMVec, _nrDepth, _nrMask -- see View.HandGuidesBack.
        internal static readonly FieldInfo[] GuideFields = new FieldInfo[3];

        // Self-test only: >= 0 replaces the model's input with that flat grey level.
        internal static float TestFlat = -1f;

        private static FieldInfo F_tab, F_rows, F_heldCfg, F_rowRt;
        private static MethodInfo M_slider, M_live, M_liveWarn, M_reflow, M_renderTab, M_saveNow;

        internal static float Applied
        {
            get { return _applied; }
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
            M_liveWarn = panel.GetMethod("LiveWarn", any, null, new[] { typeof(float).MakeByRefType(), typeof(Func<string>) }, null);
            Type row = panel.GetNestedType("RowEntry", any);
            F_rowRt = row != null ? row.GetField("Rt", any) : null;
            M_saveNow = typeof(VamDlssNrPlugin).GetMethod("SaveNow", any, null, Type.EmptyTypes, null);

            if (F_tab == null || F_rows == null || M_renderTab == null || M_reflow == null || M_slider == null || M_live == null)
            {
                PanelProblem = "VamDlssNr's panel has changed shape -- the slider cannot be added to it; set ModelResolution in " + WorkScalePlugin.Guid + ".cfg instead";
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

        private static bool SliderHeld()
        {
            try
            {
                return F_heldCfg != null && ReferenceEquals(F_heldCfg.GetValue(null), CfgScale);
            }
            catch
            {
                return false;
            }
        }

        // The value the passes use follows the setting only once the slider is let go. Every
        // distinct value is a different model size, and a different size rebuilds the network --
        // committing on each pixel of a drag would be dozens of rebuilds a second.
        internal static void Tick()
        {
            if (CfgScale == null)
            {
                return;
            }

            float want = Quantise(CfgScale.Value, MaxScale());

            if (want == _applied || SliderHeld())
            {
                return;
            }

            float was = _applied;
            _applied = want;

            if (CfgScale.Value != want)
            {
                CfgScale.Value = want;
            }

            _failures = 0;
            Problem = Hooked && Native.Loaded ? "" : Problem;
            Info("model resolution " + Percent(was) + " -> " + Percent(want));

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
                    .Append(" eyes=").Append(v.Eyes).Append(v.Live && Time.frameCount - v.LiveFrame <= 2 ? " live" : " idle").Append(v.Broken ? " BROKEN" : "")
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

            if (!Hooked || !Native.Loaded || _faults >= 3 || _failures >= 3 || _applied == 1f)
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

                if (CfgStills != null && !CfgStills.Value && F_primary != null && !(bool)F_primary.GetValue(__instance))
                {
                    return;
                }

                RenderTexture frameRt = frame as RenderTexture;
                int eyes = frameRt != null && frameRt.vrUsage == VRTextureUsage.TwoEyes ? 2 : 1;
                int w = __0, h = __1, ww, wh;

                if (frame.width != w || frame.height != h || !WorkExtent(w, h, _applied, eyes, out ww, out wh))
                {
                    return;
                }

                View view = ViewFor(__instance);

                if (view == null || view.Broken || !view.Prepare(frame, w, h, ww, wh, eyes))
                {
                    return;
                }

                view.HandGuidesBack();
                view.Downsample();

                // Only now, with nothing left that can fail, does the method see the smaller frame.
                __2 = view.Down;
                __0 = ww;
                __1 = wh;
                __3 = true; // the guides are a different size from the model: have them resampled
                __state = view;
            }
            catch (Exception ex)
            {
                Fault("the model-resolution prefix", ex);
            }
        }

        public static void RunPostfix(NrCapture __instance, bool __result, View __state)
        {
            View view = __state;

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
        private static float _statusAt = -1f;

        private static string StatusLine()
        {
            if (!Hooked || !Native.Loaded || CfgScale == null)
            {
                return "";
            }

            float want = Quantise(CfgScale.Value, MaxScale());

            if (want != _applied)
            {
                return "release the slider to apply " + Percent(want);
            }

            if (_applied == 1f)
            {
                return "";
            }

            if (Time.unscaledTime - _statusAt < 0.25f)
            {
                return _status;
            }

            _statusAt = Time.unscaledTime;
            _status = "";
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

            if (best != null)
            {
                float ratio = best.FrameH > 0 ? (float)best.WorkH / best.FrameH : _applied;
                _status = best.Eyes == 2
                    ? "network: 2 x " + (best.WorkW / 2) + "x" + best.WorkH + " for 2 x " + (best.FrameW / 2) + "x" + best.FrameH + " eyes (" + Percent(ratio) + ")"
                    : "network: " + best.WorkW + "x" + best.WorkH + " for a " + best.FrameW + "x" + best.FrameH + " frame (" + Percent(ratio) + ")";
            }

            return _status;
        }

        private static string ProblemLine()
        {
            if (!Native.Loaded)
            {
                return "model resolution is off: " + Native.Problem;
            }

            return Problem;
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
        public const string Version = "1.0.0";

        internal static WorkScalePlugin Instance;

        private float _nextSweep;
        private float _nextDrain;

        private void Awake()
        {
            Instance = this;
            Hooks.Info = delegate(string s) { Logger.LogInfo("[vws] " + s); };
            Hooks.Warn = delegate(string s) { Logger.LogWarning("[vws] " + s); };
            Hooks.Error = delegate(string s) { Logger.LogError("[vws] " + s); };

            Config.SaveOnConfigSet = false;

            ConfigEntry<bool> supersample = Config.Bind("Advanced", "AllowSupersampling", false,
                "Let the Model resolution slider go above 1.0, up to 2.0: the network then runs on an ENLARGED copy of the frame and its edit is averaged back down. Experimental and expensive -- time and video memory grow with the area, so 2.0 costs four times what 1.0 does. Takes effect the next time VaM starts.");

            Hooks.CfgScale = Config.Bind("Neural Rendering", "ModelResolution", 1f, new ConfigDescription(
                "What fraction of the frame, per axis, Neural Rendering works at. Cost falls with the square of this, so 0.5 is roughly a quarter of the network's time.\n\n" +
                "The frame is never reduced. The network is shown a filtered shrink of it, and only what the network CHANGED is enlarged and laid back over the full-size frame, so the picture underneath keeps all of its own detail whatever this says. What it trades is the network's own fine structure, which is synthesised at the smaller size and softens when enlarged; its broader work -- tone, shading, skin -- survives.\n\n" +
                "1.0 is VamDlssNr exactly as it ships. Works the same on the monitor and in a headset (per eye), and multiplies with everything else that sets the network's input size: with Run before DLSS on, this is a fraction of the DLSS render extent.\n\n" +
                "Applied when the slider is let go, because every change rebuilds the network.",
                new AcceptableValueRange<float>(Hooks.MinScale, supersample.Value ? 2f : 1f)));

            Hooks.CfgStills = Config.Bind("Neural Rendering", "ApplyToScreenshots", true,
                "Use the same model resolution for VaM's screenshot cameras (the screenshot key, save thumbnails, SuperShot). A SuperShot frame is several times the screen's size, so this is where the saving is largest; turn it off to have stills always run the network at full size.");

            Hooks.CfgEnlargement = Config.Bind("Advanced", "Enlargement", 0, new ConfigDescription(
                "How the network's work is brought back up when it ran below the frame's size. 0 = matched residual: only the network's difference is enlarged, onto the untouched full-size frame. 1 = classic: the network's own small picture is enlarged and shown, which is softer and is here for comparison.",
                new AcceptableValueRange<int>(0, 1)));

            Hooks.CfgDebugView = Config.Bind("Advanced", "DebugView", 0, new ConfigDescription(
                "Diagnostic, only while Model resolution is below 1.0. 0 = normal. 2 = show the network's edit on its own, amplified around mid-grey (grey = untouched). 3 = show the frame with the edit left out, while everything still runs. Reset to 0 at startup.",
                new AcceptableValueRange<int>(0, 3)));
            Hooks.CfgDebugView.Value = 0;

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

            if (Hooks.PanelProblem.Length != 0)
            {
                Logger.LogWarning("[vws] " + Hooks.PanelProblem);
            }

            Logger.LogInfo("[vws] VaM DLSS - Model Resolution " + Version + ": hooked VamDlssNr (" + Hooks.ComposeSites + " output reads redirected), native " + (Native.Loaded ? "loaded" : "MISSING") + ", model resolution " + Hooks.Percent(Hooks.Quantise(Hooks.CfgScale.Value, 2f)));

#if VWS_SELFTEST
            SelfTest.Begin(this);
#endif
        }

        private void Update()
        {
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
