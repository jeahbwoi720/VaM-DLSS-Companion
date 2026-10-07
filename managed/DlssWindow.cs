// VaM DLSS - Model Resolution: DLSS only around where the eye looks.
//
// Foveated shading makes the scene's own shading cheaper; with DLSS on (DLAA above all) the larger
// cost is DLSS itself, and that goes by the number of pixels it reconstructs. In a headset most of
// those pixels are out at the edge of the lens, where neither the optics nor the eye resolve them.
//
// VamDlssNr runs DLSS on each eye by itself: it cuts the two eyes out of the frame into textures of
// their own (SliceEyeInputs), has DLSS work on those, and puts the two results side by side again
// (GatherEyeOutputs). Here the textures are made the size of a window instead of the eye, the
// window is what is cut out for them, and the results are laid back over the frame at the window's
// place -- so DLSS reconstructs a quarter of the pixels for a window of half the eye's width.
// Outside the window the frame is the scene as it was rendered.
//
// A first version, to see what it buys:
//   - a headset at DLAA only (an upscaling mode hands DLSS's per-eye results to the headset
//     directly, and those would be the window alone);
//   - outside the window nothing is done to the picture yet: it is not anti-aliased, and it
//     trembles by the fraction of a pixel DLSS has the camera shaken by;
//   - the window's edge is a hard one;
//   - a window that follows the gaze moves in steps, and DLSS starts its history anew at each
//     (the motion it is given does not yet include the window's own).

using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    internal static class DlssWindow
    {
        internal static ConfigEntry<bool> CfgOn, CfgGaze;
        internal static ConfigEntry<float> CfgSize;

        internal static string Status = "", Problem = "";
        internal static bool Hooked;

        // ---- the window's place: plain numbers (the host test runs this) -------------------------

        // The window's size for an eye of `full` pixels one way: even, no smaller than 256 nor
        // larger than the eye. Equal to `full` means "no window".
        internal static int Extent(int full, float share)
        {
            if (full < 512 || !(share > 0f) || share >= 0.98f)
            {
                return full;
            }

            int size = (int)Math.Round(full * (double)share / 2.0) * 2;
            return Math.Max(256, Math.Min(full, size));
        }

        // Where the window begins along one axis: centred on `centre` (a fraction of the eye from
        // the texture's first row or column) and kept inside the eye.
        internal static int Origin(int full, int size, float centre)
        {
            int origin = (int)Math.Round(full * (double)centre - size * 0.5);
            return Math.Max(0, Math.Min(full - size, origin));
        }

        // Whether a window that sits at `at` should go to `wanted`: only when the eye has left
        // the middle fifth of it, so that it does not creep (every move costs DLSS its history).
        internal static bool Moves(int at, int wanted, int size)
        {
            return Math.Abs(wanted - at) > size / 5;
        }

        // ---- VamDlssNr's side -------------------------------------------------------------------

        private static FieldInfo F_primary, F_stereo, F_scaleApplied, F_netColor, F_eyeColor, F_eyeMVec, F_eyeDepth, F_eyeOut, F_eyeW, F_eyeH, F_srOutput, F_netMVec, F_netDepth, F_confOutW, F_reset;
        private static MethodInfo M_ensure, M_eyeTargets, M_slice, M_gather, M_configure;

        internal static void Apply(Harmony harmony)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type capture = typeof(NrCapture);

            try
            {
                F_primary = capture.GetField("Primary", any);
                F_stereo = capture.GetField("_stereoNow", any);
                F_scaleApplied = capture.GetField("_vrScaleApplied", any);
                F_netColor = capture.GetField("_netColor", any);
                F_netMVec = capture.GetField("_netMVec", any);
                F_netDepth = capture.GetField("_netDepth", any);
                F_eyeColor = capture.GetField("_eyeColor", any);
                F_eyeMVec = capture.GetField("_eyeMVec", any);
                F_eyeDepth = capture.GetField("_eyeDepth", any);
                F_eyeOut = capture.GetField("_eyeOut", any);
                F_eyeW = capture.GetField("_eyeW", any);
                F_eyeH = capture.GetField("_eyeH", any);
                F_srOutput = capture.GetField("_srOutput", any);
                F_confOutW = capture.GetField("_srConfiguredOutW", any);
                F_reset = capture.GetField("_srResetCountdown", any);
                M_ensure = capture.GetMethod("EnsureSrTargets", any, null, new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(bool).MakeByRefType() }, null);
                M_eyeTargets = capture.GetMethod("EnsureEyeTargets", any, null, new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, null);
                M_slice = capture.GetMethod("SliceEyeInputs", any, null, new[] { typeof(int) }, null);
                M_gather = capture.GetMethod("GatherEyeOutputs", any, null, new[] { typeof(int) }, null);
                M_configure = typeof(VamDlssNrPlugin).GetMethod("SrConfigure", any, null, new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(bool), typeof(uint) }, null);

                foreach (object needed in new object[] { F_primary, F_stereo, F_scaleApplied, F_netColor, F_netMVec, F_netDepth, F_eyeColor, F_eyeMVec, F_eyeDepth, F_eyeOut, F_eyeW, F_eyeH, F_srOutput, F_confOutW, F_reset,
                    M_ensure, M_eyeTargets, M_slice, M_gather, M_configure })
                {
                    if (needed == null)
                    {
                        Problem = "DLSS window: this build of VaM DLSS runs DLSS another way, and the window cannot be put in";
                        return;
                    }
                }

                Type me = typeof(DlssWindow);
                const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;
                harmony.Patch(M_ensure, new HarmonyMethod(me.GetMethod("EnsurePrefix", pub)), new HarmonyMethod(me.GetMethod("EnsurePostfix", pub)));
                harmony.Patch(M_configure, new HarmonyMethod(me.GetMethod("ConfigurePrefix", pub)));
                harmony.Patch(M_eyeTargets, new HarmonyMethod(me.GetMethod("EyeTargetsPrefix", pub)));
                harmony.Patch(M_slice, new HarmonyMethod(me.GetMethod("SlicePrefix", pub)));
                harmony.Patch(M_gather, new HarmonyMethod(me.GetMethod("GatherPrefix", pub)));
                Hooked = true;
            }
            catch (Exception ex)
            {
                Hooked = false;
                Problem = "DLSS window: could not be put in (" + ex.GetType().Name + ": " + ex.Message + ")";

                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- a frame -----------------------------------------------------------------------------

        private static NrCapture _owner;          // whose DLSS has a window in it
        private static bool _on, _ensuring;       // this frame; and: inside its EnsureSrTargets
        private static int _fullW, _fullH, _winW, _winH;
        private static readonly int[] _ox = new int[2], _oy = new int[2];
        private static readonly float[] _centre = new float[4], _gaze = new float[4];
        private static bool _placed, _gazed;
        private static int _onFrame = -100;
        private static string _why = "";
        private static float _saidAt = -100f;

        // Makes VamDlssNr set DLSS up afresh at its next look: its per-eye textures are of another size now.
        private static void Afresh(NrCapture capture)
        {
            try
            {
                F_confOutW.SetValue(capture, -1);
            }
            catch (Exception)
            {
            }
        }

        private static void Off(NrCapture capture, string why)
        {
            if ((object)_owner != null && ReferenceEquals(_owner, capture))
            {
                Afresh(capture);
                _owner = null;
                _placed = false;
            }

            _on = false;
            _why = why;
        }

        public static void EnsurePrefix(NrCapture __instance, int __0, int __1, int __2, int __3, int __6)
        {
            _ensuring = false;

            try
            {
                if (!(bool)F_primary.GetValue(__instance))
                {
                    return;
                }

                _on = false;

                if (CfgOn == null || !CfgOn.Value)
                {
                    Off(__instance, "");
                    return;
                }

                if (__6 != 2 || !(bool)F_stereo.GetValue(__instance))
                {
                    Off(__instance, "it is for a headset");
                    return;
                }

                if (__0 != __2 || __1 != __3 || (bool)F_scaleApplied.GetValue(__instance))
                {
                    Off(__instance, "it works at DLAA only for now, not in an upscaling mode");
                    return;
                }

                float share = CfgSize != null ? CfgSize.Value : 0.5f;
                int w = Extent(__0, share), h = Extent(__1, share);

                if (w >= __0 && h >= __1)
                {
                    Off(__instance, "its size is the whole eye");
                    return;
                }

                if (!ReferenceEquals(_owner, __instance) || w != _winW || h != _winH || __0 != _fullW || __1 != _fullH)
                {
                    Afresh(__instance);
                    _owner = __instance;
                    _placed = false;
                }

                _fullW = __0;
                _fullH = __1;
                _winW = w;
                _winH = h;
                Place(__instance);
                _on = true;
                _onFrame = Time.frameCount;
                _ensuring = true;
                _why = "";
            }
            catch (Exception ex)
            {
                _on = false;
                _why = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void EnsurePostfix()
        {
            _ensuring = false;
        }

        // Where each eye's window goes: the lens centre, or where the eye looks.
        private static void Place(NrCapture capture)
        {
            Hooks.LensCentres(capture, 2, _centre);
            _gazed = CfgGaze != null && CfgGaze.Value && Gaze.Read(_gaze, Time.frameCount, Time.unscaledTime);

            if (_gazed)
            {
                Array.Copy(_gaze, _centre, 4);
            }

            bool topDown = Hooks.NetIsTopDown();
            bool moved = false;

            for (int eye = 0; eye < 2; eye++)
            {
                int x = Origin(_fullW, _winW, _centre[eye * 2]);
                int y = Origin(_fullH, _winH, topDown ? 1f - _centre[eye * 2 + 1] : _centre[eye * 2 + 1]);

                if (!_placed || Moves(_ox[eye], x, _winW) || Moves(_oy[eye], y, _winH))
                {
                    _ox[eye] = x;
                    _oy[eye] = y;
                    moved = true;
                }
            }

            // (DLSS's history is of the window as it was: after a move it starts anew)
            if (moved && _placed)
            {
                F_reset.SetValue(capture, 2);
            }

            _placed = true;
        }

        public static void ConfigurePrefix(ref uint __0, ref uint __1, ref uint __2, ref uint __3, uint __6)
        {
            if (_ensuring && _on && __6 == 2)
            {
                __0 = __2 = (uint)_winW;
                __1 = __3 = (uint)_winH;
            }
        }

        public static void EyeTargetsPrefix(NrCapture __instance, ref int __0, ref int __1, ref int __2, ref int __3, int __4)
        {
            if (_ensuring && _on && __4 == 2 && ReferenceEquals(__instance, _owner))
            {
                __0 = __2 = _winW;
                __1 = __3 = _winH;
            }
        }

        // The per-eye textures are the window's size only if our sizes went in; if they are the
        // eye's after all, VamDlssNr's own cutting is the right one.
        private static bool Ours(NrCapture capture, int eyes)
        {
            return _on && eyes == 2 && ReferenceEquals(capture, _owner) && (int)F_eyeW.GetValue(capture) == _winW && (int)F_eyeH.GetValue(capture) == _winH;
        }

        public static bool SlicePrefix(NrCapture __instance, int __0)
        {
            try
            {
                if (!Ours(__instance, __0))
                {
                    return true;
                }

                RenderTexture color = (RenderTexture)F_netColor.GetValue(__instance), motion = (RenderTexture)F_netMVec.GetValue(__instance), depth = (RenderTexture)F_netDepth.GetValue(__instance);
                RenderTexture[] eyeColor = (RenderTexture[])F_eyeColor.GetValue(__instance), eyeMotion = (RenderTexture[])F_eyeMVec.GetValue(__instance), eyeDepth = (RenderTexture[])F_eyeDepth.GetValue(__instance);

                if (color == null || motion == null || depth == null || color.width != _fullW * 2 || color.height != _fullH)
                {
                    Off(__instance, "the frame is not two eyes of the size DLSS was set up for");
                    return true;
                }

                for (int eye = 0; eye < 2; eye++)
                {
                    int x = eye * _fullW + _ox[eye];
                    Graphics.CopyTexture(color, 0, 0, x, _oy[eye], _winW, _winH, eyeColor[eye], 0, 0, 0, 0);
                    Graphics.CopyTexture(motion, 0, 0, x, _oy[eye], _winW, _winH, eyeMotion[eye], 0, 0, 0, 0);
                    Graphics.CopyTexture(depth, 0, 0, x, _oy[eye], _winW, _winH, eyeDepth[eye], 0, 0, 0, 0);
                }

                return false;
            }
            catch (Exception ex)
            {
                Off(__instance, ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        public static bool GatherPrefix(NrCapture __instance, int __0)
        {
            try
            {
                if (!Ours(__instance, __0))
                {
                    return true;
                }

                RenderTexture color = (RenderTexture)F_netColor.GetValue(__instance), output = (RenderTexture)F_srOutput.GetValue(__instance);
                RenderTexture[] eyeOut = (RenderTexture[])F_eyeOut.GetValue(__instance);

                if (color == null || output == null || color.width != output.width || color.height != output.height || output.width != _fullW * 2)
                {
                    Off(__instance, "DLSS's output is not the frame's size");
                    return true;
                }

                // Outside the window: the frame as it was rendered. Then the window, as DLSS made it.
                Graphics.CopyTexture(color, output);

                for (int eye = 0; eye < 2; eye++)
                {
                    Graphics.CopyTexture(eyeOut[eye], 0, 0, 0, 0, _winW, _winH, output, 0, 0, eye * _fullW + _ox[eye], _oy[eye]);
                }

                return false;
            }
            catch (Exception ex)
            {
                Off(__instance, ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        // ---- what the panel says, and the log when it changes ---------------------------------------

        private static string _said = "";

        internal static void Tick(float now)
        {
            if (Problem.Length != 0)
            {
                Status = CfgOn != null && CfgOn.Value ? Problem : "";
            }
            else if (CfgOn == null || !CfgOn.Value)
            {
                Status = "";
            }
            else if (_on && Time.frameCount - _onFrame <= 3)
            {
                Status = "DLSS window: DLSS on " + _winW + "x" + _winH + " of each " + _fullW + "x" + _fullH + " eye (" + (100f * _winW * _winH / ((float)_fullW * _fullH)).ToString("F0") + "% of the pixels), " +
                    (_gazed ? "where you look" : "at the lens centre") + "; outside it the scene is as rendered";
            }
            else
            {
                Status = "DLSS window: off" + (_why.Length != 0 ? " -- " + _why : " (DLSS is not running)");
            }

            if (Status != _said && now - _saidAt > 2f)
            {
                _said = Status;
                _saidAt = now;

                if (Status.Length != 0 && Hooks.Info != null)
                {
                    Hooks.Info(Status + (_on ? " [left eye at " + _ox[0] + "," + _oy[0] + ", right eye at " + _ox[1] + "," + _oy[1] + "]" : ""));
                }
            }
        }
    }
}
