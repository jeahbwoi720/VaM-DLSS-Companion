// VaM DLSS - Model Resolution: what the headset's cameras give SteamVR.
//
// Passthrough needs the headset's camera picture, and whether SteamVR has one to give depends on
// the headset's driver (for a PlayStation VR2: PSVR2Toolkit's builds with Room View). Before
// anything is built on it, this asks -- once, when told to from the in-headset panel -- and writes
// what it is told to the log: whether there is a camera, how large its frames are in each of the
// three forms SteamVR offers, each lens's intrinsics and projection, and how fast frames arrive.
//
// It reads and nothing else: the camera stream is taken for two seconds and given back.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VamDlssNrWorkScale
{
    internal static class CameraProbe
    {
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Mat44
        {
            public float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23, M30, M31, M32, M33;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HasCameraFn(uint device, out byte has);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FrameSizeFn(uint device, int frameType, out uint width, out uint height, out uint bufferSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IntrinsicsFn(uint device, uint camera, int frameType, out Vec2 focal, out Vec2 centre);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ProjectionFn(uint device, uint camera, int frameType, float near, float far, out Mat44 projection);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireFn(uint device, out ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFn(ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FrameBufferFn(ulong handle, int frameType, IntPtr buffer, uint bufferSize, IntPtr header, uint headerSize);

        // IVRTrackedCamera_006's function table, in the order openvr.h declares it.
        private const string Interface = "FnTable:IVRTrackedCamera_006";
        private const int SlotHasCamera = 1, SlotFrameSize = 2, SlotIntrinsics = 3, SlotProjection = 4, SlotAcquire = 5, SlotRelease = 6, SlotFrameBuffer = 7;
        private const int HeaderBytes = 128; // CameraVideoStreamFrameHeader_t is 112; its first five fields are 32-bit

        private static readonly string[] Forms = { "distorted", "undistorted", "undistorted, widest" };

        internal static string Summary = "";

        private static IntPtr _table = IntPtr.Zero;
        private static ulong _handle;
        private static bool _streaming;
        private static float _until;
        private static float _since;
        private static IntPtr _header = IntPtr.Zero;
        private static uint _firstSequence, _lastSequence;
        private static int _headers, _asked;
        private static int _lastError;
        private static uint _width, _height, _bytesPerPixel;

        private static T Fn<T>(int slot) where T : class
        {
            IntPtr fn = Marshal.ReadIntPtr(_table, slot * IntPtr.Size);
            return fn == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        private static void Say(string line)
        {
            if (Hooks.Info != null)
            {
                Hooks.Info("camera probe: " + line);
            }
        }

        // From the panel's button. `now` is Unity's unscaled time.
        internal static void Begin(float now)
        {
            if (_streaming)
            {
                return;
            }

            try
            {
                Summary = Ask(now);
            }
            catch (Exception ex)
            {
                Summary = "camera probe failed (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            Say(Summary);
        }

        private static string Ask(float now)
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
            IntPtr name = Marshal.StringToHGlobalAnsi(Interface);
            int error;

            try
            {
                _table = getInterface(name, out error);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }

            if (_table == IntPtr.Zero)
            {
                return "SteamVR gave no camera interface (error " + error + ")";
            }

            HasCameraFn hasCamera = Fn<HasCameraFn>(SlotHasCamera);
            FrameSizeFn frameSize = Fn<FrameSizeFn>(SlotFrameSize);
            IntrinsicsFn intrinsics = Fn<IntrinsicsFn>(SlotIntrinsics);
            ProjectionFn projection = Fn<ProjectionFn>(SlotProjection);
            AcquireFn acquire = Fn<AcquireFn>(SlotAcquire);

            if (hasCamera == null || frameSize == null || intrinsics == null || projection == null || acquire == null)
            {
                return "SteamVR's camera interface is missing calls";
            }

            byte has;
            int e = hasCamera(0, out has);

            if (e != 0 || has == 0)
            {
                return "SteamVR says the headset has no camera (error " + e + ") -- the headset's driver does not offer one";
            }

            for (int form = 0; form < 3; form++)
            {
                uint w, h, bytes;
                e = frameSize(0, form, out w, out h, out bytes);
                Say(Forms[form] + ": " + (e == 0 ? w + "x" + h + ", " + bytes + " bytes a frame" : "error " + e));

                if (e != 0)
                {
                    continue;
                }

                for (uint camera = 0; camera < 4; camera++)
                {
                    Vec2 focal, centre;
                    e = intrinsics(0, camera, form, out focal, out centre);

                    if (e != 0)
                    {
                        Say("  lens " + camera + ": error " + e);
                        break;
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.Append("  lens ").Append(camera).Append(": focal ").Append(focal.X.ToString("0.0")).Append(", ").Append(focal.Y.ToString("0.0"))
                        .Append("  centre ").Append(centre.X.ToString("0.0")).Append(", ").Append(centre.Y.ToString("0.0"));

                    Mat44 m;

                    if (projection(0, camera, form, 0.1f, 100f, out m) == 0)
                    {
                        sb.Append("  projection x ").Append(m.M00.ToString("0.000")).Append(" ").Append(m.M02.ToString("0.000"))
                            .Append("  y ").Append(m.M11.ToString("0.000")).Append(" ").Append(m.M12.ToString("0.000"));
                    }

                    Say(sb.ToString());
                }
            }

            e = acquire(0, out _handle);

            if (e != 0 || _handle == 0)
            {
                return "there is a camera, but its stream could not be taken (error " + e + ")";
            }

            if (_header == IntPtr.Zero)
            {
                _header = Marshal.AllocHGlobal(HeaderBytes);
            }

            _streaming = true;
            _since = now;
            _until = now + 2f;
            _headers = _asked = 0;
            _lastError = 0;
            _firstSequence = _lastSequence = 0;
            return "there is a camera; watching its stream for two seconds";
        }

        // Every frame, from the plugin: nothing unless a probe is under way.
        internal static void Tick(float now)
        {
            if (!_streaming)
            {
                return;
            }

            try
            {
                FrameBufferFn frame = Fn<FrameBufferFn>(SlotFrameBuffer);

                if (frame != null)
                {
                    for (int i = 0; i < HeaderBytes; i += 4)
                    {
                        Marshal.WriteInt32(_header, i, 0);
                    }

                    // No buffer: the header alone says which frame is the latest.
                    int e = frame(_handle, 0, IntPtr.Zero, 0, _header, 112);
                    _asked++;

                    if (e == 0)
                    {
                        uint sequence = (uint)Marshal.ReadInt32(_header, 16);

                        if (_headers == 0)
                        {
                            _firstSequence = sequence;
                            _since = now;
                        }

                        _lastSequence = sequence;
                        _width = (uint)Marshal.ReadInt32(_header, 4);
                        _height = (uint)Marshal.ReadInt32(_header, 8);
                        _bytesPerPixel = (uint)Marshal.ReadInt32(_header, 12);
                        _headers++;
                    }
                    else
                    {
                        _lastError = e;
                    }
                }

                if (now < _until)
                {
                    return;
                }

                float seconds = Math.Max(0.001f, now - _since);
                Summary = _headers == 0
                    ? "there is a camera, but no frame came in two seconds (last error " + _lastError + ")"
                    : "camera stream: " + _width + "x" + _height + ", " + _bytesPerPixel + " bytes a pixel, " +
                      ((_lastSequence - _firstSequence) / seconds).ToString("0.0") + " frames a second (" + _headers + " of " + _asked + " asks answered)";
            }
            catch (Exception ex)
            {
                Summary = "camera probe failed while watching the stream (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            _streaming = false;

            try
            {
                ReleaseFn release = Fn<ReleaseFn>(SlotRelease);

                if (release != null)
                {
                    release(_handle);
                }
            }
            catch (Exception)
            {
            }

            _handle = 0;
            Say(Summary);
        }
    }
}
