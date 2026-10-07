// VaM DLSS - Model Resolution: the room, where the scene is the key colour.
//
// In a headset whose cameras SteamVR can read (a PlayStation VR2 with PSVR2Toolkit 1.0.0, for
// one), every pixel of the finished frame that is the key colour -- a green backdrop behind the
// person, say -- is replaced with what the headset's camera sees in that direction. The person and
// the interface stay as they are.
//
// The picture is made in the native half (PSPassthrough): for each key-coloured pixel, the point
// the eye sees through it at a set distance is carried into the camera's space -- through the head
// as it is now and as it was when the camera took its frame, so that a turning head does not drag
// the room with it -- and through the camera's fisheye to a place in the raw frame. This file
// finds the camera, reads what SteamVR knows of it (where the two lenses sit in the head, their
// polynomial, the eyes' projection), starts the native worker that keeps the newest frame, and
// each frame hands over the head's pose.
//
// The raw frames are used, not SteamVR's undistorted ones: those spread a 154-degree view over the
// same pixels and leave the middle with a quarter of the detail. SteamVR does not give out the raw
// frames' intrinsics, so the fisheye's scale is a setting (measured on a PlayStation VR2) and its
// centre is taken from the undistorted frames', which it matches to a pixel or two.

using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.XR;

namespace VamDlssNrWorkScale
{
    internal static class Passthrough
    {
        internal static ConfigEntry<bool> CfgOn;
        internal static ConfigEntry<int> CfgPreset;
        internal static ConfigEntry<float> CfgRed, CfgGreen, CfgBlue;
        internal static ConfigEntry<float> CfgTolerance, CfgSoftness, CfgDistance, CfgBrightness, CfgFocal;
        internal static ConfigEntry<bool> CfgFollowHead;
        internal static ConfigEntry<int> CfgView;
        internal static ConfigEntry<int> CfgMode;
        internal static ConfigEntry<bool> CfgOverlayShape;
        internal static ConfigEntry<float> CfgOverlayDistance;
        internal static ConfigEntry<bool> CfgRoomBehind;
        internal static ConfigEntry<int> CfgPoseSource;
        internal static ConfigEntry<float> CfgPoseTiming;
        internal static ConfigEntry<bool> CfgDepth, CfgDepthFlip;
        internal static ConfigEntry<float> CfgDepthMargin, CfgDepthSoftness;
        internal static ConfigEntry<int> CfgLook, CfgHandOver;
        internal static ConfigEntry<float> CfgLookRed, CfgLookGreen, CfgLookBlue, CfgGrain, CfgRim, CfgColour;

        // The component of the camera the frame in hand was rendered by (HeadsetUi sets it before Run).
        internal static Component SceneCamera;

        internal static string Status = "";

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetGenericInterfaceFn(IntPtr version, out int error);

        [StructLayout(LayoutKind.Sequential)]
        private struct Vec2
        {
            public float X, Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInt32Fn(uint device, int property, out int error);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate float GetFloatFn(uint device, int property, out int error);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetArrayFn(uint device, int property, uint tag, IntPtr buffer, uint size, out int error);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ProjectionRawFn(int eye, out float left, out float right, out float top, out float bottom);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HasCameraFn(uint device, out byte has);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FrameSizeFn(uint device, int frameType, out uint width, out uint height, out uint bufferSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IntrinsicsFn(uint device, uint camera, int frameType, out Vec2 focal, out Vec2 centre);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireFn(uint device, out ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFn(ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetSpaceFn(int origin);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSpaceFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetLastPosesFn(IntPtr renderPoses, uint renderCount, IntPtr gamePoses, uint gameCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetDevicePoseFn(int origin, float secondsFromNow, IntPtr poses, uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FrameBufferFn(ulong handle, int frameType, IntPtr buffer, uint bufferSize, IntPtr header, uint headerSize);

        // Function-table slots, in the order openvr_capi.h declares each interface.
        private const int SysProjectionRaw = 2, SysDevicePose = 12, SysGetFloat = 23, SysGetInt32 = 24, SysGetArray = 27;
        private const int CamHasCamera = 1, CamFrameSize = 2, CamIntrinsics = 3, CamAcquire = 5, CamRelease = 6, CamFrameBuffer = 7, CamSetSpace = 12;
        private const int CompGetSpace = 1, CompGetLastPoses = 3;

        private const int PropNumCameras = 1039, PropFrameLayout = 1040, PropIpd = 2003, PropCameraToHead = 2055, PropDistortionFunction = 2072, PropDistortionCoefficients = 2073;
        private const uint TagInt32 = 2, TagDouble = 7, TagMatrix34 = 20;

        // The native half's block of numbers (CamField in vws.cpp).
        private const int FKey = 0, FTolerance = 3, FSoftness = 4, FGain = 5, FDistance = 6, FFocal = 7, FK = 8, FCentre = 12, FTan = 16, FCamToHead = 24, FEyeToHead = 48, FFollow = 72, FView = 73, FMode = 74, FQuadTan = 75, FQuadDistance = 76, FStereoRule = 77, FSpace = 78, FPoseIsCamera = 79, FDepth = 80, FDepthMargin = 81, FDepthSoft = 82;

        private const int FLook = 83, FLookLow = 84, FLookMid = 87, FLookHigh = 90, FGrain = 93, FRim = 94, FColour = 95, FHandOver = 96;

        private static readonly float[] _config = new float[112];
        private static readonly float[] _eyeToRoom = new float[12];
        private static readonly float[] _head = new float[12];
        private static IntPtr _compositor = IntPtr.Zero;
        private static IntPtr _poseBuffer = IntPtr.Zero;
        private static GetLastPosesFn _lastPoses;
        private static int _space = 1;

        // Whose pose a camera frame comes with: the first camera's (a PlayStation VR2) until the
        // frames themselves say it is the head's.
        private static bool _poseIsCamera = true;
        private static bool _poseKindKnown;
        private static int _stalled;

        internal static bool CaptureWanted;
        private static byte[] _capture;
        private static int _captureTries;
        private static IntPtr _system = IntPtr.Zero, _camera = IntPtr.Zero;
        private static ulong _handle;
        private static bool _started;
        private static bool _failed;
        private static float _retryAt, _lastRun, _statusAt, _ipdAt;

        // When the camera was last asked for by something other than the room's picture, and what
        // stood in the way.
        private static float _heldAt = -100f;
        private static string _holdProblem;
        private static uint _lastFrames;
        private static float _ipd = 0.064f;
        private static uint _width, _height;

        private static readonly float[][] Presets =
        {
            null,                          // 0 custom: the three sliders as they stand
            new[] { 0f, 1f, 0f },          // 1 green
            new[] { 0f, 0f, 1f },          // 2 blue
            new[] { 1f, 0f, 1f },          // 3 magenta
            new[] { 0f, 0f, 0f },          // 4 black
            new[] { 1f, 1f, 1f },          // 5 white
        };

        // ---- the room's look: the cameras are monochrome, and this is what their grey is shown in ----

        internal const int LookGrey = 0, LookOwn = 6, LookGuessed = 7;
        internal static readonly string[] LookNames =
        {
            "Camera's grey", "Night vision (green)", "Amber", "Cold blue", "Sepia", "Heat", "Your colour", "Colours guessed by a network (experimental)"
        };

        // The ramps: what black, mid-grey and white become, as displayed.
        private static readonly float[][] Ramps =
        {
            null,
            new[] { 0f, 0.03f, 0f,        0.12f, 0.78f, 0.16f,    0.80f, 1f, 0.78f },    // night vision
            new[] { 0.04f, 0.015f, 0f,    0.86f, 0.50f, 0.10f,    1f, 0.95f, 0.78f },    // amber
            new[] { 0f, 0.012f, 0.045f,   0.24f, 0.52f, 0.82f,    0.90f, 0.97f, 1f },    // cold blue
            new[] { 0.05f, 0.03f, 0.02f,  0.62f, 0.48f, 0.36f,    1f, 0.96f, 0.88f },    // sepia
        };

        // The look's numbers as the native half takes them (block[83..92]): which of its four
        // ways of showing the grey, and the ramp's three colours.
        internal static void LookNumbers(int look, float red, float green, float blue, float[] block)
        {
            float[] ramp = look > 0 && look < Ramps.Length ? Ramps[look] : null;

            if (look == LookOwn)
            {
                // black, the colour, and most of the way from it to white
                ramp = new[] { red * 0.04f, green * 0.04f, blue * 0.04f, red, green, blue, red + (1f - red) * 0.75f, green + (1f - green) * 0.75f, blue + (1f - blue) * 0.75f };
            }

            block[FLook] = ramp != null ? 1f : look == 5 ? 2f : look == LookGuessed ? 3f : 0f;

            for (int i = 0; i < 9; i++)
            {
                block[FLookLow + i] = ramp != null ? ramp[i] : 0f;
            }
        }

        private static void FillLook()
        {
            int look = CfgLook != null ? CfgLook.Value : 0;
            LookNumbers(look, CfgLookRed != null ? CfgLookRed.Value : 1f, CfgLookGreen != null ? CfgLookGreen.Value : 1f, CfgLookBlue != null ? CfgLookBlue.Value : 1f, _config);
            _config[FGrain] = look != LookGrey && CfgGrain != null ? CfgGrain.Value : 0f;
            _config[FRim] = look != LookGrey && CfgRim != null ? CfgRim.Value : 0f;
            _config[FColour] = CfgColour != null ? CfgColour.Value : 1f;
        }

        // How often the overlay's device has been lost since VaM started: nothing while it never was.
        private static string LostStatus()
        {
            uint lost = Native.OverlayLost();
            return lost == 0 ? "" : "; its device was lost " + lost + (lost == 1 ? " time" : " times");
        }

        // What the colour thread is doing, for the status line; empty unless that look is chosen.
        private static string _colourSaid = "";

        private static string ColourStatus()
        {
            if (CfgLook == null || CfgLook.Value != LookGuessed)
            {
                return "";
            }

            int state;
            uint pictures, micros;
            Native.ColourStatus(out state, out pictures, out micros);
            string said = state < 0 ? "; no colours: " + Native.ColourError() :
                state == 1 ? "; colours: loading the network" :
                pictures == 0 ? "; colours: waiting for the first" :
                "; colours: " + (micros / 1000f).ToString("0") + " ms a picture";

            if (state < 0 && said != _colourSaid && Hooks.Info != null)
            {
                Hooks.Info("passthrough" + said);
            }

            _colourSaid = said;
            return said;
        }

        internal static void ApplyPreset()
        {
            int preset = CfgPreset != null ? CfgPreset.Value : 0;

            if (preset > 0 && preset < Presets.Length && CfgRed != null && CfgGreen != null && CfgBlue != null)
            {
                CfgRed.Value = Presets[preset][0];
                CfgGreen.Value = Presets[preset][1];
                CfgBlue.Value = Presets[preset][2];
            }
        }

        private static IntPtr Table(GetGenericInterfaceFn getInterface, string name)
        {
            IntPtr text = Marshal.StringToHGlobalAnsi(name);

            try
            {
                int error;
                return getInterface(text, out error);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        private static T Fn<T>(IntPtr table, int slot) where T : class
        {
            IntPtr fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            return fn == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        private static string Start()
        {
            IntPtr module = GetModuleHandleW("openvr_api.dll");

            if (module == IntPtr.Zero)
            {
                return "this is not a SteamVR session";
            }

            IntPtr address = GetProcAddress(module, "VR_GetGenericInterface");

            if (address == IntPtr.Zero)
            {
                return "VaM's openvr_api.dll has no VR_GetGenericInterface";
            }

            GetGenericInterfaceFn getInterface = (GetGenericInterfaceFn)Marshal.GetDelegateForFunctionPointer(address, typeof(GetGenericInterfaceFn));

            _camera = Table(getInterface, "FnTable:IVRTrackedCamera_006");
            _system = Table(getInterface, "FnTable:IVRSystem_026");

            if (_camera == IntPtr.Zero || _system == IntPtr.Zero)
            {
                return "this SteamVR is too old for passthrough (it lacks IVRTrackedCamera_006 or IVRSystem_026)";
            }

            byte has;

            if (Fn<HasCameraFn>(_camera, CamHasCamera)(0, out has) != 0 || has == 0)
            {
                return "SteamVR has no camera for this headset (a PlayStation VR2 needs PSVR2Toolkit 1.0.0 or later)";
            }

            uint bytes;

            if (Fn<FrameSizeFn>(_camera, CamFrameSize)(0, 0, out _width, out _height, out bytes) != 0 || _width < 2 || _height < 1)
            {
                return "SteamVR would not say how large the camera's frames are";
            }

            GetInt32Fn getInt = Fn<GetInt32Fn>(_system, SysGetInt32);
            GetArrayFn getArray = Fn<GetArrayFn>(_system, SysGetArray);
            int error;
            int cameras = getInt(0, PropNumCameras, out error);
            int layout = getInt(0, PropFrameLayout, out error);

            // Two lenses, side by side in one frame: the only arrangement the pass knows.
            if (cameras != 2 || (layout & 32) == 0)
            {
                return "passthrough wants two cameras side by side in one frame; this headset has " + cameras + " (layout " + layout + ")";
            }

            IntPtr buffer = Marshal.AllocHGlobal(128);

            try
            {
                // Each camera in the head.
                if (getArray(0, PropCameraToHead, TagMatrix34, buffer, 96, out error) != 96)
                {
                    return "SteamVR does not say where the cameras sit in the headset";
                }

                Marshal.Copy(buffer, _config, FCamToHead, 24);

                // The lens: a fisheye, an angle from its axis laid down as a radius, times a polynomial.
                uint n = getArray(0, PropDistortionFunction, TagInt32, buffer, 8, out error);
                int function = n >= 4 ? Marshal.ReadInt32(buffer) : 0;

                if (function != 1 && function != 2)
                {
                    return "the cameras' lens model (" + function + ") is not a fisheye this knows";
                }

                for (int i = 0; i < 4; i++)
                {
                    _config[FK + i] = 0f;
                }

                if (getArray(0, PropDistortionCoefficients, TagDouble, buffer, 64, out error) >= 32)
                {
                    double[] k = new double[4];
                    Marshal.Copy(buffer, k, 0, 4);

                    for (int i = 0; i < 4; i++)
                    {
                        _config[FK + i] = (float)k[i];
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // The fisheye's centre in each lens's half: the undistorted frames' centre is the same
            // point to a pixel or two, and SteamVR does give that out.
            IntrinsicsFn intrinsics = Fn<IntrinsicsFn>(_camera, CamIntrinsics);

            for (uint lens = 0; lens < 2; lens++)
            {
                Vec2 focal, centre;
                bool known = intrinsics(0, lens, 1, out focal, out centre) == 0 && centre.X > 0f && centre.Y > 0f;
                _config[FCentre + lens * 2] = known ? centre.X : _width * 0.25f;
                _config[FCentre + lens * 2 + 1] = known ? centre.Y : _height * 0.5f;
            }

            ProjectionRawFn projection = Fn<ProjectionRawFn>(_system, SysProjectionRaw);

            for (int eye = 0; eye < 2; eye++)
            {
                float l, r, t, b;
                projection(eye, out l, out r, out t, out b);
                _config[FTan + eye * 4] = l;
                _config[FTan + eye * 4 + 1] = r;
                _config[FTan + eye * 4 + 2] = t;
                _config[FTan + eye * 4 + 3] = b;
            }

            // The camera's frames come with the head's pose at their moment: have it in the space
            // the game's own poses are in.
            int space = 1;
            IntPtr compositor = Table(getInterface, "FnTable:IVRCompositor_029");

            if (compositor == IntPtr.Zero)
            {
                compositor = Table(getInterface, "FnTable:IVRCompositor_020");
            }

            if (compositor != IntPtr.Zero)
            {
                space = Fn<GetSpaceFn>(compositor, CompGetSpace)();
            }

            Fn<SetSpaceFn>(_camera, CamSetSpace)(space);
            _compositor = compositor;
            _lastPoses = compositor != IntPtr.Zero ? Fn<GetLastPosesFn>(compositor, CompGetLastPoses) : null;
            _space = space;

            int e = Fn<AcquireFn>(_camera, CamAcquire)(0, out _handle);

            if (e != 0 || _handle == 0)
            {
                return "the camera's stream could not be taken (error " + e + ")";
            }

            if (Native.CamStart(Marshal.ReadIntPtr(_camera, CamFrameBuffer * IntPtr.Size), _handle, _width, _height) == 0)
            {
                Fn<ReleaseFn>(_camera, CamRelease)(_handle);
                _handle = 0;
                return "the camera worker could not be started";
            }

            _started = true;
            _ipdAt = 0f;
            _stalled = 0;

            if (Hooks.Info != null)
            {
                Hooks.Info("passthrough: camera " + _width + "x" + _height + ", tracking space " + space + ", lens k " + _config[FK].ToString("0.00000") + " " + _config[FK + 1].ToString("0.00000") +
                    " " + _config[FK + 2].ToString("0.00000") + " " + _config[FK + 3].ToString("0.00000") + ", centres " + _config[FCentre].ToString("0.0") + "," + _config[FCentre + 1].ToString("0.0") + " / " +
                    _config[FCentre + 2].ToString("0.0") + "," + _config[FCentre + 3].ToString("0.0"));
            }

            return null;
        }

        internal static void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;

            try
            {
                Native.CamStop();

                if (_camera != IntPtr.Zero && _handle != 0)
                {
                    Fn<ReleaseFn>(_camera, CamRelease)(_handle);
                }
            }
            catch (Exception)
            {
            }

            _handle = 0;
        }

        // The camera for something other than the room's picture (the hands, see Hands): taken if
        // passthrough has not taken it, given its numbers while passthrough is not doing that, and
        // kept for as long as this goes on being called. Null, or what stands in the way.
        internal static string Hold(float now)
        {
            if (!Native.Loaded)
            {
                return "the native half is not loaded";
            }

            if (_failed)
            {
                return "the camera failed (" + Status + ")";
            }

            _heldAt = now;

            // Passthrough is running it, and says what there is to say.
            if (CfgOn != null && CfgOn.Value && now - _lastRun < 1f)
            {
                return _started ? null : (Status.Length != 0 ? Status : "waiting for the camera");
            }

            if (!_started)
            {
                if (now < _retryAt)
                {
                    return _holdProblem ?? "waiting for the camera";
                }

                _holdProblem = Start();

                if (_holdProblem != null)
                {
                    _retryAt = now + 5f;
                    return _holdProblem;
                }
            }

            // Only what says where a point in the room falls in a frame: nothing is drawn.
            _config[FFocal] = CfgFocal.Value * (_width * 0.5f / 1016f);
            _config[FMode] = 0f;
            _config[FDepth] = 0f;
            _config[FLook] = 0f;
            _config[FSpace] = _space;
            _config[FPoseIsCamera] = _poseIsCamera ? 1f : 0f;
            Native.CamConfigure(_config);

            if (now >= _statusAt)
            {
                uint frames, uploads;
                int error;
                Native.CamStatus(out frames, out uploads, out error);

                // As in Run: a stream that has stopped giving frames is taken afresh.
                if (frames != 0 && frames == _lastFrames)
                {
                    if (++_stalled >= 2)
                    {
                        Stop();
                        _retryAt = now;
                        _statusAt = now + 1f;
                        return "the camera's stream stopped -- taking it again";
                    }
                }
                else
                {
                    _stalled = 0;
                }

                if (frames != 0 && !_poseKindKnown)
                {
                    DetectPoseKind();
                }

                _lastFrames = frames;
                _statusAt = now + 1f;
            }

            return null;
        }

        // From the plugin, every frame: gives the camera back once nothing has asked for it for a while.
        internal static void Tick(float now)
        {
            if (_started && now - _lastRun > 3f && now - _heldAt > 3f)
            {
                Stop();
                Status = "";
            }
        }

        internal static void Fail(Exception ex)
        {
            _failed = true;
            Stop();
            Status = "passthrough is off: " + ex.GetType().Name + ": " + ex.Message;

            if (Hooks.Warn != null)
            {
                Hooks.Warn(Status + "\n" + ex.StackTrace);
            }
        }

        // Is the pose a camera frame comes with the head's, or the first camera's? The frame's pose
        // is put beside the headset's own of the same moment: the difference is nothing, or it is
        // where that camera sits in the head.
        private static void DetectPoseKind()
        {
            IntPtr header = Marshal.AllocHGlobal(128);
            IntPtr device = Marshal.AllocHGlobal(96);

            try
            {
                for (int i = 0; i < 128; i += 4)
                {
                    Marshal.WriteInt32(header, i, 0);
                }

                if (Fn<FrameBufferFn>(_camera, CamFrameBuffer)(_handle, 0, IntPtr.Zero, 0, header, 112) != 0 || Marshal.ReadByte(header, 96) == 0)
                {
                    return;
                }

                Fn<GetDevicePoseFn>(_system, SysDevicePose)(_space, 0f, device, 1);

                if (Marshal.ReadByte(device, 76) == 0)
                {
                    return;
                }

                float[] frame = new float[12], head = new float[12];
                Marshal.Copy(new IntPtr(header.ToInt64() + 20), frame, 0, 12);
                Marshal.Copy(device, head, 0, 12);

                // The frame's pose in the head's own space: rotation head^T * frame, and where its
                // origin lies.
                float trace = 0f;

                for (int i = 0; i < 3; i++)
                {
                    trace += head[i] * frame[i] + head[4 + i] * frame[4 + i] + head[8 + i] * frame[8 + i];
                }

                float dx = frame[3] - head[3], dy = frame[7] - head[7], dz = frame[11] - head[11];
                float apart = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);

                _poseIsCamera = !(trace > 2.9f && apart < 0.03f);
                _poseKindKnown = true;

                if (Hooks.Info != null)
                {
                    Hooks.Info("passthrough: a camera frame comes with " + (_poseIsCamera ? "the first camera's pose" : "the head's pose") + " (" + apart.ToString("0.000") + " m from the head, turned " +
                        (Mathf.Acos(Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f)) * Mathf.Rad2Deg).ToString("0") + " degrees)");
                }
            }
            catch (Exception)
            {
                _poseKindKnown = true;
            }
            finally
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(device);
            }
        }

        // The camera's newest frame as it came (both lenses side by side, a byte a pixel), into a
        // buffer that is made to fit. False when there is none.
        internal static bool ReadCamera(ref byte[] grey, out uint width, out uint height)
        {
            width = _width;
            height = _height;

            if (_width == 0 || _height == 0)
            {
                return false;
            }

            if (grey == null || grey.Length != _width * _height)
            {
                grey = new byte[_width * _height];
            }

            uint cw, ch;
            return Native.CamRead(grey, out cw, out ch) && cw == _width && ch == _height;
        }

        // The camera's newest frame as it came (both lenses side by side), as a PNG. False when there
        // is none.
        internal static bool SaveCamera(string path)
        {
            if (_width == 0 || _height == 0)
            {
                return false;
            }

            byte[] grey = new byte[_width * _height];
            uint cw, ch;

            if (!Native.CamRead(grey, out cw, out ch) || cw != _width || ch != _height)
            {
                return false;
            }

            byte[] rgb = new byte[cw * ch * 3];

            for (uint y = 0; y < ch; y++)
            {
                uint from = y * cw, to = (ch - 1 - y) * cw * 3;

                for (uint x = 0; x < cw; x++)
                {
                    byte value = grey[from + x];
                    rgb[to + x * 3] = value;
                    rgb[to + x * 3 + 1] = value;
                    rgb[to + x * 3 + 2] = value;
                }
            }

            Texture2D camera = new Texture2D((int)cw, (int)ch, TextureFormat.RGB24, false);
            camera.LoadRawTextureData(rgb);
            camera.Apply(false);
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(camera));
            UnityEngine.Object.Destroy(camera);
            return true;
        }

        // The overlay's own picture, saved beside the plugin: the room as drawn on the quad, left
        // eye's half first, and in its alpha where the game lets it show.
        private static byte[] _captureGrid;

        private static void SaveCapture()
        {
            try
            {
                if (_capture == null)
                {
                    _capture = new byte[3072 * 1536 * 4];
                    _captureTries = 0;
                }

                uint w, h;

                if (_captureGrid == null)
                {
                    _captureGrid = new byte[256 * 256 * 4];
                }

                uint side, micros;
                bool haveGrid = Native.DepthRead(_captureGrid, out side, out micros) && side >= 16 && side <= 256;

                if (!Native.OverlayRead(_capture, out w, out h) || w == 0 || h == 0)
                {
                    // The first asking only arms it; the picture is there a camera frame later.
                    if (++_captureTries > 120)
                    {
                        CaptureWanted = false;
                        _capture = null;

                        if (Hooks.Info != null)
                        {
                            Hooks.Info("passthrough: no overlay picture to capture (is the mode the overlay?)");
                        }
                    }

                    return;
                }

                if (_captureTries < 2)
                {
                    // Not a picture kept from an earlier asking: wait for the one just asked for.
                    _captureTries++;
                    return;
                }

                CaptureWanted = false;

                Texture2D picture = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                byte[] flipped = new byte[w * h * 4];

                for (uint y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(_capture, (int)(y * w * 4), flipped, (int)((h - 1 - y) * w * 4), (int)(w * 4));
                }

                picture.LoadRawTextureData(flipped);
                picture.Apply(false);

                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "passthrough-overlay-capture.png");
                System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(picture));
                UnityEngine.Object.Destroy(picture);
                _capture = null;

                // Beside it, what it was made from: the camera's frame as it came (both lenses),
                // and the depth worked out from it (red: how near, green: believed, blue: held).
                string folder = System.IO.Path.GetDirectoryName(path);
                SaveCamera(System.IO.Path.Combine(folder, "passthrough-camera-capture.png"));

                if (haveGrid)
                {
                    int n = (int)side;
                    byte[] turned = new byte[n * n * 4];

                    for (int y = 0; y < n; y++)
                    {
                        Buffer.BlockCopy(_captureGrid, y * n * 4, turned, (n - 1 - y) * n * 4, n * 4);
                    }

                    Texture2D grid = new Texture2D(n, n, TextureFormat.RGBA32, false);
                    grid.LoadRawTextureData(turned);
                    grid.Apply(false);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "passthrough-depth-capture.png"), ImageConversion.EncodeToPNG(grid));
                    UnityEngine.Object.Destroy(grid);
                }

                if (Hooks.Info != null)
                {
                    Hooks.Info("passthrough: the overlay's picture is in " + path + (haveGrid ? ", with the camera's frame and the depth beside it" : ", with the camera's frame beside it"));
                }
            }
            catch (Exception ex)
            {
                CaptureWanted = false;
                _capture = null;

                if (Hooks.Info != null)
                {
                    Hooks.Info("passthrough: the capture failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
            }
        }

        private static bool _mattePending, _mattePosed, _matteFromAlpha;

        private static readonly int DepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly float[] _depthInfo = new float[4];
        private static RenderTexture _sceneDepth;
        private static IntPtr _depthPtr = IntPtr.Zero;
        private static string _depthNote = "";

        // The scene's depth for the frame in hand, in a texture the native half can read: the
        // card's own depth buffer copied as plain numbers. With it go the near and far planes, how
        // many of the scene's units make a metre (the game can scale the player), and how the
        // buffer lies. Zero when there is none this frame.
        private static IntPtr SceneDepth()
        {
            _depthPtr = IntPtr.Zero;

            if (CfgDepth == null || !CfgDepth.Value)
            {
                _depthNote = "";
                return _depthPtr;
            }

            Camera camera = (object)SceneCamera != null && SceneCamera != null ? SceneCamera.GetComponent<Camera>() : null;

            if (camera == null)
            {
                _depthNote = "no camera";
                return _depthPtr;
            }

            // Asked for here if nobody has: it is there from the next frame, and it stays -- taking
            // it away again could take it from whoever else has come to rely on it meanwhile.
            if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
                _depthNote = "asked the game for its depth";
                return _depthPtr;
            }

            Texture depth = Shader.GetGlobalTexture(DepthTextureId);

            if (depth == null || depth.width < 16 || depth.height < 16)
            {
                _depthNote = "the game has no depth texture";
                return _depthPtr;
            }

            if (_sceneDepth == null || _sceneDepth.width != depth.width || _sceneDepth.height != depth.height)
            {
                if (_sceneDepth != null)
                {
                    _sceneDepth.Release();
                    UnityEngine.Object.Destroy(_sceneDepth);
                }

                RenderTexture source = depth as RenderTexture;
                RenderTextureDescriptor d = source != null ? source.descriptor : new RenderTextureDescriptor(depth.width, depth.height);
                d.colorFormat = RenderTextureFormat.RFloat;
                d.depthBufferBits = 0;
                d.msaaSamples = 1;
                d.sRGB = false;
                d.useMipMap = false;
                d.autoGenerateMips = false;
                _sceneDepth = new RenderTexture(d);
                _sceneDepth.filterMode = FilterMode.Point;
                _sceneDepth.Create();
            }

            RenderTexture active = RenderTexture.active;
            Graphics.Blit(depth, _sceneDepth);
            RenderTexture.active = active;

            Vector4 left = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
            Vector4 right = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
            float apart = ((Vector3)left - (Vector3)right).magnitude;

            _depthInfo[0] = camera.nearClipPlane;
            _depthInfo[1] = camera.farClipPlane;
            _depthInfo[2] = _ipd > 0.01f && apart > 1e-5f ? apart / _ipd : 1f;
            _depthInfo[3] = ((CfgDepthFlip != null && CfgDepthFlip.Value) ? 1f : 0f) + (depth.width > depth.height * 3 / 2 ? 2f : 0f) + (SystemInfo.usesReversedZBuffer ? 4f : 0f);
            _depthNote = "";
            _depthPtr = _sceneDepth.GetNativeTexturePtr();
            return _depthPtr;
        }
        private static int _loggedState = -1;

        private static System.Reflection.FieldInfo _hudVisible;
        private static bool _hudLooked;

        // Is VaM's main menu showing? (Its own flag; without it, no.)
        private static bool MenuOpen()
        {
            if (!_hudLooked)
            {
                _hudLooked = true;
                _hudVisible = typeof(SuperController).GetField("_mainHUDVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            }

            SuperController sc = SuperController.singleton;

            try
            {
                return _hudVisible != null && sc != null && (bool)_hudVisible.GetValue(sc);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The matte, once the interface has been drawn: `eyes` the two reconstructed eyes, else
        // `wide` holds both side by side.
        internal static void MatteLate(RenderTexture wide, RenderTexture[] eyes, bool topDown)
        {
            if (!_mattePending)
            {
                return;
            }

            _mattePending = false;

            if (!_started || _failed)
            {
                return;
            }

            for (int eye = 0; eye < 2; eye++)
            {
                if (eyes != null)
                {
                    Native.IssuePass(Native.PushMatte(eyes[eye].GetNativeTexturePtr(), 1, eye, false, topDown, _mattePosed ? _head : null, _matteFromAlpha, _depthPtr, _depthInfo));
                }
                else
                {
                    Native.IssuePass(Native.PushMatte(wide.GetNativeTexturePtr(), 2, eye, wide.sRGB, topDown, _mattePosed ? _head : null, _matteFromAlpha, _depthPtr, _depthInfo));
                }
            }
        }

        // The head in the room as SteamVR last gave it to the game to render with: the pose this
        // frame was drawn from, in the same terms as the poses the camera's frames come with.
        private static bool LastPose()
        {
            if (_lastPoses == null)
            {
                return false;
            }

            if (_poseBuffer == IntPtr.Zero)
            {
                _poseBuffer = Marshal.AllocHGlobal(96);
            }

            // One TrackedDevicePose_t, the headset's: a 3x4, two vectors, a result, two flags.
            if (_lastPoses(_poseBuffer, 1, IntPtr.Zero, 0) != 0 || Marshal.ReadByte(_poseBuffer, 76) == 0)
            {
                return false;
            }

            Marshal.Copy(_poseBuffer, _head, 0, 12);
            Marshal.Copy(new IntPtr(_poseBuffer.ToInt64() + 60), _turning, 0, 3);

            // The turn the camera was really given this frame is Unity's to say. It counts z the
            // other way from SteamVR, so a rotation's z row and z column change sign.
            Quaternion q = InputTracking.GetLocalRotation(XRNode.Head);

            if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f)
            {
                Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, q, Vector3.one);
                _unity[0] = m.m00; _unity[1] = m.m01; _unity[2] = -m.m02;
                _unity[3] = m.m10; _unity[4] = m.m11; _unity[5] = -m.m12;
                _unity[6] = -m.m20; _unity[7] = -m.m21; _unity[8] = m.m22;

                // How far apart the two are, for the log: while the head is still they should be
                // the same turn, and while it moves the difference is a matter of timing.
                float trace = 0f;

                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        trace += _unity[i * 3 + j] * _head[i * 4 + j];
                    }
                }

                float apart = Mathf.Acos(Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f)) * Mathf.Rad2Deg;
                float speed = Mathf.Sqrt(_turning[0] * _turning[0] + _turning[1] * _turning[1] + _turning[2] * _turning[2]) * Mathf.Rad2Deg;
                _apartSum += apart;
                _apartMax = Mathf.Max(_apartMax, apart);
                _speedMax = Mathf.Max(_speedMax, speed);

                if (speed < 5f)
                {
                    _apartStill = Mathf.Max(_apartStill, apart);
                }

                _apartCount++;

                if (CfgPoseSource == null || CfgPoseSource.Value == 0)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            _head[i * 4 + j] = _unity[i * 3 + j];
                        }
                    }
                }
            }

            // Along the head's turning, by the set time: a rotation about the room's axis the
            // head is turning on, in front of the pose.
            float ms = CfgPoseTiming != null ? CfgPoseTiming.Value : 0f;
            float rate = Mathf.Sqrt(_turning[0] * _turning[0] + _turning[1] * _turning[1] + _turning[2] * _turning[2]);

            if (ms != 0f && rate > 1e-4f)
            {
                Matrix4x4 turn = Matrix4x4.TRS(Vector3.zero, Quaternion.AngleAxis(rate * ms * 0.001f * Mathf.Rad2Deg, new Vector3(_turning[0], _turning[1], _turning[2]) / rate), Vector3.one);

                // The matrix of a turn about an axis is the same arithmetic whichever hand the
                // coordinates are: applied to SteamVR's numbers it is SteamVR's turn.
                for (int j = 0; j < 3; j++)
                {
                    float a = _head[j], b = _head[4 + j], c = _head[8 + j];
                    _head[j] = turn.m00 * a + turn.m01 * b + turn.m02 * c;
                    _head[4 + j] = turn.m10 * a + turn.m11 * b + turn.m12 * c;
                    _head[8 + j] = turn.m20 * a + turn.m21 * b + turn.m22 * c;
                }
            }

            return true;
        }

        private static readonly float[] _turning = new float[3];
        private static readonly float[] _unity = new float[9];
        private static float _apartSum, _apartMax, _apartStill, _speedMax;
        private static int _apartCount;

        // The finished frame of a headset: `eyes` the two reconstructed eyes when DLSS is upscaling,
        // else `frame` holds both side by side. True when the frame itself was drawn on.
        // `matteLater`: the interface is still to be drawn over this frame (HeadsetUi does that when
        // the frame is over), so the matte the overlay is cut by has to wait for it -- cut now, it
        // would call the room through wherever the menu is about to be.
        internal static bool Run(RenderTexture frame, RenderTexture[] eyes, bool topDown, float now, bool matteLater)
        {
            if (CfgOn == null || !CfgOn.Value || !Native.Loaded || _failed)
            {
                // ...unless the hands are using the camera.
                if (_started && (_failed || now - _heldAt > 1f))
                {
                    Stop();
                }

                if (!_failed)
                {
                    Status = "";
                }

                return false;
            }

            _lastRun = now;

            if (!_started)
            {
                if (now < _retryAt)
                {
                    return false;
                }

                string problem = Start();

                if (problem != null)
                {
                    Status = "passthrough: " + problem;
                    _retryAt = now + 5f;
                    return false;
                }
            }

            // The eyes in the head: straight ahead, half the lens distance to either side.
            if (now >= _ipdAt)
            {
                _ipdAt = now + 2f;
                int error;
                float ipd = Fn<GetFloatFn>(_system, SysGetFloat)(0, PropIpd, out error);

                if (error == 0 && ipd > 0.04f && ipd < 0.09f)
                {
                    _ipd = ipd;
                }
            }

            bool overlay = CfgMode == null || CfgMode.Value == 0;

            _config[FKey] = CfgRed.Value;
            _config[FKey + 1] = CfgGreen.Value;
            _config[FKey + 2] = CfgBlue.Value;
            _config[FTolerance] = CfgTolerance.Value;
            _config[FSoftness] = CfgSoftness.Value;
            _config[FGain] = CfgBrightness.Value;
            _config[FDistance] = CfgDistance.Value;
            _config[FFocal] = CfgFocal.Value * (_width * 0.5f / 1016f);
            _config[FFollow] = CfgFollowHead.Value ? 1f : 0f;
            _config[FView] = CfgView.Value;
            _config[FMode] = overlay ? 1f : 0f;
            _config[FQuadTan] = 2.2f;
            _config[FQuadDistance] = CfgOverlayDistance != null ? CfgOverlayDistance.Value : 10f;
            _config[FStereoRule] = CfgOverlayShape != null && CfgOverlayShape.Value ? 1f : 0f;
            _config[FSpace] = _space;
            _config[FPoseIsCamera] = _poseIsCamera ? 1f : 0f;
            _config[FDepth] = overlay && CfgDepth != null && CfgDepth.Value ? 1f : 0f;
            _config[FDepthMargin] = CfgDepthMargin != null ? CfgDepthMargin.Value : 0.25f;
            _config[FDepthSoft] = CfgDepthSoftness != null ? CfgDepthSoftness.Value : 0.1f;
            _config[FHandOver] = CfgHandOver != null ? CfgHandOver.Value : 0f;
            FillLook();

            for (int eye = 0; eye < 2; eye++)
            {
                int at = FEyeToHead + eye * 12;

                for (int i = 0; i < 12; i++)
                {
                    _config[at + i] = (i == 0 || i == 5 || i == 10) ? 1f : 0f;
                }

                _config[at + 3] = (eye == 0 ? -0.5f : 0.5f) * _ipd;
            }

            Native.CamConfigure(_config);

            if (CaptureWanted)
            {
                SaveCapture();
            }

            bool posed = LastPose();
            bool drew = false;

            // The room comes in front of the scene where it is nearer -- and the depth buffer knows
            // nothing of the interface. Drawn over the frame afterwards it is told apart by the
            // frame's alpha; while it is part of the scene itself (the full-size menu off, or
            // VamDlssNr's pipeline idle) it cannot be, and the room would be drawn over the menu
            // wherever no figure stands behind it. So with the menu open and in the scene, the
            // room stays behind.
            if (overlay && !(MenuOpen() && !matteLater))
            {
                SceneDepth();
            }
            else
            {
                _depthPtr = IntPtr.Zero;
            }

            // Under an overlay the room can be in the game's frame as well: whatever the overlay's
            // cut-out leaves uncovered beside the person is then the room, not the key colour. The
            // pass marks what it replaced in the frame's alpha, and the matte is read from there.
            bool behind = overlay && (CfgRoomBehind == null || CfgRoomBehind.Value);

            _mattePending = overlay && matteLater;
            _mattePosed = posed;
            _matteFromAlpha = behind;

            for (int eye = 0; eye < 2; eye++)
            {
                IntPtr target = eyes != null ? eyes[eye].GetNativeTexturePtr() : frame.GetNativeTexturePtr();
                int held = eyes != null ? 1 : 2;
                bool srgb = eyes == null && frame.sRGB;

                if (overlay && !behind)
                {
                    // The game only says where the room shows; the overlay draws it.
                    if (!_mattePending)
                    {
                        Native.IssuePass(Native.PushMatte(target, held, eye, srgb, topDown, posed ? _head : null, false, _depthPtr, _depthInfo));
                    }

                    continue;
                }

                // This eye in the room: the head, and half the lens distance along its x.
                float x = (eye == 0 ? -0.5f : 0.5f) * _ipd;
                Array.Copy(_head, _eyeToRoom, 12);
                _eyeToRoom[3] += _head[0] * x;
                _eyeToRoom[7] += _head[4] * x;
                _eyeToRoom[11] += _head[8] * x;

                Native.IssuePass(Native.PushPassthrough(target, held, eye, srgb, topDown, posed ? _eyeToRoom : null));
                drew = true;
            }

            if (behind && !_mattePending)
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    IntPtr target = eyes != null ? eyes[eye].GetNativeTexturePtr() : frame.GetNativeTexturePtr();
                    Native.IssuePass(Native.PushMatte(target, eyes != null ? 1 : 2, eye, eyes == null && frame.sRGB, topDown, posed ? _head : null, true, _depthPtr, _depthInfo));
                }
            }

            if (now >= _statusAt)
            {
                uint frames, uploads;
                int error;
                Native.CamStatus(out frames, out uploads, out error);

                // A stream that has stopped giving frames (another program letting go of the
                // camera can do that) is taken afresh.
                if (frames != 0 && frames == _lastFrames)
                {
                    if (++_stalled >= 2)
                    {
                        Stop();
                        _retryAt = now;
                        _statusAt = now + 1f;
                        Status = "passthrough: the camera's stream stopped -- taking it again";

                        if (Hooks.Info != null)
                        {
                            Hooks.Info(Status);
                        }

                        return drew;
                    }
                }
                else
                {
                    _stalled = 0;
                }

                if (frames != 0 && !_poseKindKnown)
                {
                    DetectPoseKind();
                }

                // Every few seconds, how Unity's head pose and SteamVR's compared.
                if (_apartCount >= 150)
                {
                    if (Hooks.Info != null)
                    {
                        Hooks.Info("passthrough cut-out: Unity's head turn and SteamVR's differ by " + (_apartSum / _apartCount).ToString("0.00") + " deg on average, " + _apartMax.ToString("0.00") +
                            " at most (" + _apartStill.ToString("0.00") + " while the head was still); the head turned up to " + _speedMax.ToString("0") + " deg/s; using " +
                            ((CfgPoseSource == null || CfgPoseSource.Value == 0) ? "Unity's" : "SteamVR's") + ", timing " + (CfgPoseTiming != null ? CfgPoseTiming.Value : 0f).ToString("0") + " ms");
                    }

                    _apartSum = _apartMax = _apartStill = _speedMax = 0f;
                    _apartCount = 0;
                }

                float rate = _statusAt > 0f ? (frames - _lastFrames) / Mathf.Max(0.001f, now - (_statusAt - 1f)) : 0f;
                _lastFrames = frames;
                _statusAt = now + 1f;

                if (frames == 0)
                {
                    Status = "passthrough: waiting for the camera's first frame" + (error != 0 ? " (error " + error + ")" : "");
                }
                else if (overlay)
                {
                    int state, overlayError;
                    uint drawn;
                    Native.OverlayStatus(out state, out drawn, out overlayError);
                    if (state != _loggedState)
                    {
                        _loggedState = state;

                        if (Hooks.Info != null)
                        {
                            Hooks.Info("passthrough overlay: state " + state + ", " + drawn + " pictures drawn, error " + overlayError);
                        }
                    }

                    Status = "passthrough: " + (
                        state == 5 ? "its own overlay, " + rate.ToString("0") + " pictures a second" :
                        state == 1 ? "overlay waiting for the game's first frame" :
                        state == 2 ? "the overlay's graphics device was lost (0x" + overlayError.ToString("X") + "), another is being made" :
                        state == 3 ? "this SteamVR has no overlay interface the plugin knows -- try Mode 1" :
                        state == 4 ? "SteamVR refused the overlay (error " + overlayError + ") -- try Mode 1" :
                        "overlay starting") + (posed ? "" : ", no head pose") + LostStatus() + ColourStatus();

                    // How far the room and the scene are taken to be, straight ahead: the one way
                    // to see from inside the headset whether either is being read.
                    if (state == 5 && CfgDepth != null && CfgDepth.Value)
                    {
                        float room, scene;
                        Native.OverlayDepth(out room, out scene);
                        uint side, micros;
                        Native.DepthRead(null, out side, out micros);
                        Status += "\ndepth ahead: room " + (room < 0f ? "not worked out" : room < 0.06f ? "not known" : (1f / room).ToString("0.00") + " m") +
                            ", scene " + (_depthNote.Length != 0 ? "(" + _depthNote + ")" : scene < 0f ? "not arriving" : scene < 0.02f ? "far" : (1f / scene).ToString("0.00") + " m") + " (" + (micros / 1000f).ToString("0") + " ms)";
                    }
                }
                else
                {
                    Status = "passthrough: in the game's frame, camera " + rate.ToString("0") + " pictures a second" + (posed ? "" : ", no head pose") + ColourStatus();
                }
            }

            return drew;
        }
    }
}
