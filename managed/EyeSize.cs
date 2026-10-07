// VaM DLSS - Model Resolution: the headset's picture size, checked against SteamVR's own.
//
// VamDlssNr measures the size of the headset's picture once, when the first upscaling mode comes
// on -- its "anchor": the eye texture Unity has at that moment, and the eye scale in force. Every
// upscaling mode from then on reconstructs to that size. Started with an upscaling mode already
// on, VaM has been seen to begin with eye textures of half the headset's size (1020x1040 where
// SteamVR's is 2040x2080) and to have the right ones a moment later; the anchor is taken in
// between, and the headset is given a 1020x1040 picture for the rest of the session, whatever is
// chosen afterwards. Only VaM's own Render Scale slider put it right, or a restart.
//
// Two witnesses know the size without having been there at the start: SteamVR, which says what it
// recommends for an eye (IVRSystem::GetRecommendedRenderTargetSize, the number Unity multiplies by
// the eye scale), and the eye texture Unity has now, at the scale in force now. Where both agree
// with each other and not with the anchor, the anchor is written anew from SteamVR's size. (Making
// VamDlssNr measure again was tried before and made it worse: it measured under its own reduced
// scale. Nothing is measured here: the size is SteamVR's.) Where the two do not agree with each
// other, nothing is touched.
//
// The second number that goes wrong is the scale VamDlssNr takes for the user's own -- the one its
// DLSS ratio is multiplied onto. It takes any eye scale it did not set itself for that, and in the
// session above took its own for it twice while the quality was changed: 1 became 0.5, then 0.333,
// and Ultra Performance rendered at a ninth of the size (eye scale 0.111). In VaM the user's scale
// is the Render Scale preference and nothing else, which is why moving that slider cured it: VaM
// writes its value, and VamDlssNr takes it. Here the same is done without the slider: while
// VamDlssNr is upscaling and holds another scale for the user's than VaM's preference, it is given
// the preference.
//
// The third thing is where a half-size picture after a scene load came from, with or without a DLSS
// mode: VaM's SteamVR plugin. Whenever SteamVR takes the input focus away -- its dashboard, and by
// what the logs show every scene load and every long freeze -- SteamVR_Render.OnInputFocus keeps
// the eye scale in a field of its own and sets 0.5, and sets what it kept when the focus is back.
// Told twice that the focus is gone before it is told once that it is back, it keeps its own 0.5
// and gives that back: the headset stays at half size (1020x1040) with VaM's Render Scale at 1,
// until the slider is moved. And under an upscaling mode VaM DLSS takes that 0.5 for the user's
// scale, which is how its own came to be multiplied onto itself. Every one of these halvings also
// has all of VaM DLSS's pictures and its DLSS feature made anew, twice: about half a second each.
// Here the plugin's two writes of the scale are led through FocusScale below, which by default
// leaves the scale alone; with [Headset] KeepSizeWithoutFocus off it halves as before, but gives
// back what the scale was before the first halving, whatever came in between.
//
// And every change of any of these numbers is written to the log from the first frame on, so that
// a session that goes wrong all the same says how.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    internal static class EyeSize
    {
        internal static ConfigEntry<bool> CfgCorrect, CfgLog, CfgKeep;

        // ---- the judgement: plain numbers, nothing of Unity's (the host test runs it) -----------

        internal const int Fine = 0, AnchorWrong = 1, Unsettled = 2;

        // (a change of scale reaches the texture a frame or two later, and sizes are rounded: only
        // a plain difference counts)
        private static bool Apart(double a, double b)
        {
            return Math.Abs(a - b) > Math.Max(3.0, b * 0.04);
        }

        // The anchor (a size, and the eye scale it was measured at) against SteamVR's size for an
        // eye and the eye texture there is at `scale`. AnchorWrong: both witnesses give one size
        // and the anchor another. Unsettled: the anchor is not SteamVR's size, and the eye texture
        // is not either -- which of them is right cannot be said, and nothing should be done.
        internal static int Judge(int anchorW, int anchorH, float anchorScale, int steamW, int steamH, int eyeW, int eyeH, float scale)
        {
            if (anchorW <= 0 || anchorH <= 0 || !(anchorScale > 0f) || steamW < 64 || steamH < 64 || eyeW <= 0 || eyeH <= 0 || !(scale > 0f))
            {
                return Fine;
            }

            if (!Apart(anchorW / (double)anchorScale, steamW) && !Apart(anchorH / (double)anchorScale, steamH))
            {
                return Fine;
            }

            return Apart(eyeW, steamW * (double)scale) || Apart(eyeH, steamH * (double)scale) ? Unsettled : AnchorWrong;
        }

        // Whether VamDlssNr, while it is upscaling, holds another scale for the user's own than
        // VaM's Render Scale preference. `held` is what it holds, `set` the eye scale it last set
        // and `scale` the one in force: judged only while those two are one (it is in charge, and
        // nobody else has just written a scale it has yet to take) and differ from `held` (it is
        // upscaling -- otherwise the scale is the user's business and none of ours).
        internal static bool ScaleWrong(float held, float set, float scale, float preference)
        {
            return held > 0f && preference >= 0.25f && preference <= 4f && Math.Abs(scale - set) <= 0.0005f && Math.Abs(set - held) > 0.0005f && Math.Abs(held - preference) > 0.002f;
        }

        // How long something has to stay wrong, and the same wrong (two numbers that say what it
        // is wrong against, or how), before it is written anew: half a second of frames.
        internal sealed class Patience
        {
            internal int Frames = 45;

            private int _count, _w, _h;

            // True on the frame it has been wrong for long enough.
            internal bool Step(bool wrong, int steamW, int steamH)
            {
                if (!wrong || steamW != _w || steamH != _h)
                {
                    _w = steamW;
                    _h = steamH;
                    _count = wrong ? 1 : 0;
                    return false;
                }

                if (++_count < Frames)
                {
                    return false;
                }

                _count = 0;
                return true;
            }
        }

        // How often a thing may be put right: so many times within so many seconds, and then not
        // until the oldest of those is that long ago. A session's log had the scale go wrong at
        // every change of DLSS quality, minutes apart, and stay right in between -- that is to be
        // put right every time, however long the session. What this keeps from happening is a
        // tug-of-war with something that wants it otherwise, frame after frame.
        internal sealed class Allowance
        {
            internal float Within = 20f;

            private readonly float[] _at = { -1e9f, -1e9f, -1e9f, -1e9f };
            private int _next;

            internal bool Take(float now)
            {
                if (now - _at[_next] < Within)
                {
                    return false;
                }

                _at[_next] = now;
                _next = (_next + 1) % _at.Length;
                return true;
            }
        }

        // ---- the eye scale while SteamVR has the input focus ------------------------------------

        // What is written to the eye scale when VaM's SteamVR plugin is told that the input focus
        // is back (`hasFocus`) or gone: nothing (0), half, or what it was before it was halved.
        // `keep`: the scale is left alone while the focus is gone. `dimmed` and `before` are kept
        // between calls: whether it stands halved by this, and what it was before.
        internal static float FocusStep(bool hasFocus, bool keep, float now, ref bool dimmed, ref float before)
        {
            if (hasFocus)
            {
                if (!dimmed)
                {
                    return 0f;
                }

                dimmed = false;
                return before;
            }

            if (keep)
            {
                return 0f;
            }

            if (!dimmed)
            {
                before = now;
                dimmed = true;
            }

            return 0.5f;
        }

        internal static bool FocusHooked;
        internal static string FocusProblem = "";
        internal static int FocusSites;

        private static bool _focus = true, _dimmed;
        private static float _before = 1f;
        private static int _focusLines;

        internal static void Apply(Harmony harmony)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;

            try
            {
                Type render = Type.GetType("Valve.VR.SteamVR_Render, SteamVR") ?? AccessTools.TypeByName("Valve.VR.SteamVR_Render");
                MethodInfo focus = render != null ? render.GetMethod("OnInputFocus", any, null, new[] { typeof(bool) }, null) : null;

                if (focus == null)
                {
                    FocusProblem = "this build of VaM's SteamVR plugin has no OnInputFocus: its halving of the eye scale while SteamVR has the focus stays as it is";
                    return;
                }

                Type me = typeof(EyeSize);
                FocusSites = 0;
                harmony.Patch(focus, new HarmonyMethod(me.GetMethod("FocusPrefix", pub)), null, new HarmonyMethod(me.GetMethod("FocusTranspiler", pub)));

                if (FocusSites != 2)
                {
                    FocusProblem = "VaM's SteamVR plugin writes the eye scale in " + FocusSites + " places where two were expected: its halving while SteamVR has the focus stays as it is";
                    harmony.UnpatchSelf();
                    return;
                }

                FocusHooked = true;
            }
            catch (Exception ex)
            {
                FocusHooked = false;
                FocusProblem = "the eye scale could not be kept while SteamVR has the focus (" + ex.GetType().Name + ": " + ex.Message + ")";

                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception)
                {
                }
            }
        }

        // OnInputFocus's two writes of the eye scale (through SteamVR_Camera.sceneResolutionScale)
        // come to FocusScale instead.
        public static IEnumerable<CodeInstruction> FocusTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mine = typeof(EyeSize).GetMethod("FocusScale", BindingFlags.Static | BindingFlags.Public);

            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo called = instruction.operand as MethodInfo;

                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && called != null && called.Name == "set_sceneResolutionScale")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = mine;
                    FocusSites++;
                }

                yield return instruction;
            }
        }

        public static void FocusPrefix(bool __0)
        {
            _focus = __0;
        }

        // `asked` is what the plugin would have written: half, or whatever it kept.
        public static void FocusScale(float asked)
        {
            float now = XRSettings.eyeTextureResolutionScale;
            bool keep = CfgKeep == null || CfgKeep.Value;
            float write = FocusStep(_focus, keep, now, ref _dimmed, ref _before);

            if (write > 0f && Math.Abs(write - now) > 0.00001f)
            {
                XRSettings.eyeTextureResolutionScale = write;
            }

            if (++_focusLines <= 40)
            {
                Say("SteamVR " + (_focus ? "gave the input focus back" : "took the input focus away") + ": the eye scale " +
                    (write > 0f ? "goes from " + now.ToString("0.000") + " to " + write.ToString("0.000") : "stays " + now.ToString("0.000")) +
                    " (VaM's SteamVR plugin asked for " + asked.ToString("0.000") + ")" + (_focusLines == 40 ? " -- no more of these lines this session" : ""));
            }
        }

        // ---- SteamVR's size for an eye ----------------------------------------------------------

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetGenericInterfaceFn(IntPtr version, out int error);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RecommendedFn(out uint width, out uint height);

        // GetRecommendedRenderTargetSize is the first entry of every IVRSystem there has been; the
        // newest is asked for first because that is the one known to be answered here (see Gaze).
        private static readonly string[] Interfaces = { "FnTable:IVRSystem_026", "FnTable:IVRSystem_022", "FnTable:IVRSystem_019" };

        private static GetGenericInterfaceFn _getInterface;
        private static readonly IntPtr[] _names = new IntPtr[3];
        private static IntPtr _fnPointer = IntPtr.Zero;
        private static RecommendedFn _fn;

        private static bool Recommended(out int width, out int height)
        {
            width = height = 0;

            if (_getInterface == null)
            {
                IntPtr module = GetModuleHandleW("openvr_api.dll");
                IntPtr address = module != IntPtr.Zero ? GetProcAddress(module, "VR_GetGenericInterface") : IntPtr.Zero;

                if (address == IntPtr.Zero)
                {
                    return false;
                }

                for (int i = 0; i < Interfaces.Length; i++)
                {
                    _names[i] = Marshal.StringToHGlobalAnsi(Interfaces[i]);
                }

                _getInterface = (GetGenericInterfaceFn)Marshal.GetDelegateForFunctionPointer(address, typeof(GetGenericInterfaceFn));
            }

            // Asked for afresh each time: a table kept from before the runtime went away would be
            // a table of calls into nothing.
            IntPtr table = IntPtr.Zero;

            for (int i = 0; i < _names.Length && table == IntPtr.Zero; i++)
            {
                int error;
                table = _getInterface(_names[i], out error);
            }

            IntPtr fn = table != IntPtr.Zero ? Marshal.ReadIntPtr(table, 0) : IntPtr.Zero;

            if (fn == IntPtr.Zero)
            {
                return false;
            }

            if (fn != _fnPointer || _fn == null)
            {
                _fn = (RecommendedFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(RecommendedFn));
                _fnPointer = fn;
            }

            uint w, h;
            _fn(out w, out h);

            if (w < 64 || h < 64 || w > 32768 || h > 32768)
            {
                return false;
            }

            width = (int)w;
            height = (int)h;
            return true;
        }

        // ---- VamDlssNr's own numbers ------------------------------------------------------------

        private static FieldInfo F_anchorW, F_anchorH, F_anchorScale, F_base, F_weSet;
        private static bool _bound;

        private static FieldInfo Static(Type type, string name, Type of)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && field.FieldType == of ? field : null;
        }

        private static bool Bind()
        {
            if (!_bound)
            {
                _bound = true;
                Type capture = typeof(NrCapture);
                F_anchorW = Static(capture, "_vrAnchorW", typeof(int));
                F_anchorH = Static(capture, "_vrAnchorH", typeof(int));
                F_anchorScale = Static(capture, "_vrAnchorScale", typeof(float));
                F_base = Static(capture, "_vrBaseScale", typeof(float));
                F_weSet = Static(capture, "_vrScaleWeSet", typeof(float));

                if (F_anchorW == null || F_anchorH == null || F_anchorScale == null)
                {
                    F_anchorW = null;
                    Say("this VaM DLSS keeps the headset's size somewhere else: it is neither watched nor put right here");
                }
            }

            return F_anchorW != null;
        }

        // ---- every frame ------------------------------------------------------------------------

        private const int MostLines = 160;

        private static readonly Patience _patience = new Patience(), _scalePatience = new Patience();
        private static readonly Allowance _allowance = new Allowance(), _scaleAllowance = new Allowance();
        private static float _noteUntil;
        private static readonly float[] _said = new float[6];
        private static readonly int[] _saidSizes = new int[6];
        private static string _saidDevice = "";
        private static int _lines, _saidVerdict = -1;
        private static float _askAt;
        private static int _steamW, _steamH;
        private static bool _off;

        internal static string Note = "";

        internal static void Tick(float now)
        {
            // (what was put right is said in the panel for a while, not for the rest of the session)
            if (Note.Length != 0 && now > _noteUntil)
            {
                Note = "";
            }

            if (_off || !XRSettings.enabled)
            {
                return;
            }

            if (FocusProblem.Length != 0)
            {
                Say(FocusProblem);
                FocusProblem = "";
            }

            try
            {
                Look(now);
            }
            catch (Exception ex)
            {
                _off = true;
                Say("stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Look(float now)
        {
            string device = XRSettings.loadedDeviceName ?? "";

            // Each frame while VaM starts, where the sizes move; four times a second after that.
            if (now < 30f || now >= _askAt)
            {
                _askAt = now + 0.25f;

                if (!Recommended(out _steamW, out _steamH))
                {
                    _steamW = _steamH = 0;
                }
            }

            bool mod = Bind();
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            float scale = XRSettings.eyeTextureResolutionScale;
            int anchorW = mod ? (int)F_anchorW.GetValue(null) : 0, anchorH = mod ? (int)F_anchorH.GetValue(null) : 0;
            float anchorScale = mod ? (float)F_anchorScale.GetValue(null) : 0f;
            float held = mod && F_base != null ? (float)F_base.GetValue(null) : 0f, set = mod && F_weSet != null ? (float)F_weSet.GetValue(null) : 0f;
            float preference = UserPreferences.singleton != null ? UserPreferences.singleton.renderScale : 0f;
            int verdict = device == "OpenVR" ? Judge(anchorW, anchorH, anchorScale, _steamW, _steamH, eyeW, eyeH, scale) : Fine;

            if (CfgLog == null || CfgLog.Value)
            {
                Trail(device, eyeW, eyeH, scale, anchorW, anchorH, anchorScale, held, set, preference, verdict);
            }

            bool scaleWrong = device == "OpenVR" && verdict == Fine && anchorW > 0 && F_base != null && F_weSet != null && ScaleWrong(held, set, scale, preference);

            if (_scalePatience.Step(scaleWrong, Mathf.RoundToInt(held * 10000f), Mathf.RoundToInt(preference * 10000f)))
            {
                if (CfgCorrect != null && !CfgCorrect.Value)
                {
                    Once("VaM DLSS takes " + held.ToString("0.000") + " for your eye scale where VaM's Render Scale is " + preference.ToString("0.00") + ": [Headset] CorrectEyeSize is off, so it stays (moving VaM's Render Scale slider puts it right)");
                }
                else if (!_scaleAllowance.Take(now))
                {
                    Once("VaM DLSS's idea of your eye scale has been put right four times within " + _scaleAllowance.Within.ToString("0") + " seconds and is wrong again (" + held.ToString("0.000") + " against VaM's Render Scale " + preference.ToString("0.00") +
                        "): something keeps setting it. Left alone for a while.");
                }
                else
                {
                    F_base.SetValue(null, preference);
                    _noteUntil = now + 20f;
                    Note = "eye scale put right: VaM DLSS had taken " + held.ToString("0.000") + " for yours, VaM's Render Scale is " + preference.ToString("0.00");
                    Say("VaM DLSS was multiplying its DLSS ratio onto an eye scale of " + held.ToString("0.000") + " (eye scale in force " + scale.ToString("0.000") + "), where VaM's Render Scale is " + preference.ToString("0.00") +
                        ". It is given VaM's: the eye scale becomes " + (preference * (held > 0f ? set / held : 1f)).ToString("0.000") + ".");
                }
            }

            if (!_patience.Step(verdict == AnchorWrong, _steamW, _steamH))
            {
                return;
            }

            string was = anchorW + "x" + anchorH + " at eye scale " + anchorScale.ToString("0.000");

            if (CfgCorrect != null && !CfgCorrect.Value)
            {
                Once("VaM DLSS measured the headset's picture as " + was + ", SteamVR's size for an eye is " + _steamW + "x" + _steamH + " and the eye texture agrees with SteamVR: [Headset] CorrectEyeSize is off, so it stays");
                return;
            }

            if (!_allowance.Take(now))
            {
                Once("VaM DLSS's size for the headset's picture has been put right four times within " + _allowance.Within.ToString("0") + " seconds and is wrong again (" + was + " against SteamVR's " + _steamW + "x" + _steamH + "): left alone for a while");
                return;
            }

            F_anchorW.SetValue(null, _steamW);
            F_anchorH.SetValue(null, _steamH);
            F_anchorScale.SetValue(null, 1f);
            _noteUntil = now + 20f;
            Note = "headset size put right: " + _steamW + "x" + _steamH + " (VaM DLSS had measured " + anchorW + "x" + anchorH + ")";
            Say("VaM DLSS had measured the headset's picture as " + was + "; SteamVR's size for an eye is " + _steamW + "x" + _steamH + ", and the eye texture (" + eyeW + "x" + eyeH + " at eye scale " + scale.ToString("0.000") +
                ") agrees with SteamVR. Its measurement is put right: it reconstructs to " + _steamW + "x" + _steamH + " from here on.");
        }

        private static string _once = "";

        private static void Once(string line)
        {
            if (line != _once)
            {
                _once = line;
                Say(line);
            }
        }

        // One line whenever any of the numbers is another than in the line before.
        private static void Trail(string device, int eyeW, int eyeH, float scale, int anchorW, int anchorH, float anchorScale, float held, float set, float preference, int verdict)
        {
            bool same = device == _saidDevice && verdict == _saidVerdict && eyeW == _saidSizes[0] && eyeH == _saidSizes[1] && _steamW == _saidSizes[2] && _steamH == _saidSizes[3] &&
                anchorW == _saidSizes[4] && anchorH == _saidSizes[5] && Math.Abs(scale - _said[0]) <= 0.0005f && Math.Abs(anchorScale - _said[1]) <= 0.0005f && Math.Abs(held - _said[2]) <= 0.0005f &&
                Math.Abs(set - _said[3]) <= 0.0005f && Math.Abs(preference - _said[4]) <= 0.0005f && Math.Abs(XRSettings.renderViewportScale - _said[5]) <= 0.0005f;

            if (same || _lines > MostLines)
            {
                return;
            }

            _saidDevice = device;
            _saidVerdict = verdict;
            _saidSizes[0] = eyeW;
            _saidSizes[1] = eyeH;
            _saidSizes[2] = _steamW;
            _saidSizes[3] = _steamH;
            _saidSizes[4] = anchorW;
            _saidSizes[5] = anchorH;
            _said[0] = scale;
            _said[1] = anchorScale;
            _said[2] = held;
            _said[3] = set;
            _said[4] = preference;
            _said[5] = XRSettings.renderViewportScale;

            if (++_lines > MostLines)
            {
                Say("(" + MostLines + " changes written: no more of them this session)");
                return;
            }

            Say("frame " + Time.frameCount + ": " + (device.Length != 0 ? device : "no device") + " eye texture " + eyeW + "x" + eyeH + " at eye scale " + scale.ToString("0.000") +
                (Math.Abs(_said[5] - 1f) > 0.0005f ? " (viewport " + _said[5].ToString("0.000") + ")" : "") +
                "; SteamVR's size for an eye " + (_steamW > 0 ? _steamW + "x" + _steamH : "not known") + "; VaM's render scale " + preference.ToString("0.00") +
                "; VaM DLSS: " + (anchorW > 0 ? "measured " + anchorW + "x" + anchorH + " at " + anchorScale.ToString("0.000") + ", user's scale " + held.ToString("0.000") + ", last set " + set.ToString("0.000") : "nothing measured yet") +
                (verdict == AnchorWrong ? " -- its measurement is not the headset's size" : (verdict == Unsettled ? " -- its measurement and the eye texture both differ from SteamVR's size: left alone" : "")));
        }

        private static void Say(string line)
        {
            if (Hooks.Info != null)
            {
                Hooks.Info("eye size: " + line);
            }
        }
    }
}
