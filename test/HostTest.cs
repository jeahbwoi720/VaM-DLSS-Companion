// Runs under test\monohost.exe, i.e. on VaM's own Mono runtime but without Unity.
//
// What can be checked there, and is:
//   1. HarmonyX on this runtime does what the plugin relies on -- a prefix rewriting arguments by
//      position, state handed to the postfix, a transpiler swapping a field read for a call.
//   2. The plugin finds everything it reaches into the real VamDlssNrPlugin.dll for.
//   3. Its patches go onto VamDlssNr's real methods, and the rewritten Compose compiles.
//   4. The hook on VaM's plugin loader, which the in-headset panel is found by, goes on.
//   5. The P/Invokes into the native half marshal correctly.
//   6. The model-extent arithmetic.
//
// What cannot: anything that needs a running engine (render textures, the render thread).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using VamDlssNr;
using VamDlssNrWorkScale;

public class Dummy
{
    public object Out = "field";
    public string Seen = "";

    public bool Run(int w, int h, object input, bool flag)
    {
        Seen = w + "x" + h + " " + input + " " + flag;
        return w > 0;
    }

    public object Read()
    {
        object first = Out;
        object second = Out != null ? Out : "null";
        return first + "/" + second;
    }
}

public class Box
{
    public int W, H;
}

public static class DummyPatches
{
    public static int Sites;
    public static string PostfixSaw = "";

    public static void Prefix(Dummy __instance, ref int __0, ref int __1, ref object __2, ref bool __3, out Box __state)
    {
        __state = null;

        if (__0 == 100)
        {
            __state = new Box { W = __0, H = __1 };
            __0 = 50;
            __1 = 25;
            __2 = "small";
            __3 = true;
        }
    }

    public static void Postfix(Dummy __instance, bool __result, Box __state)
    {
        PostfixSaw = __state == null ? "no state" : "state " + __state.W + "x" + __state.H + " result " + __result;
    }

    public static object Replacement(Dummy d)
    {
        return "redirected";
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int n = 0;

        foreach (CodeInstruction ins in instructions)
        {
            FieldInfo f = ins.operand as FieldInfo;

            if (ins.opcode == OpCodes.Ldfld && f != null && f.Name == "Out")
            {
                ins.opcode = OpCodes.Call;
                ins.operand = typeof(DummyPatches).GetMethod("Replacement");
                n++;
            }

            yield return ins;
        }

        Sites = n;
    }
}

public static class HostTest
{
    private static int _checks, _failures;

    private static void Check(bool ok, string what)
    {
        _checks++;

        if (!ok)
        {
            _failures++;
            Console.WriteLine("  FAIL " + what);
        }
    }

    public static int Main(string[] args)
    {
        // The last argument is the plugin folder (managed + native DLL side by side).
        string pluginDir = args.Length > 0 ? args[args.Length - 1] : ".";

        Console.WriteLine("runtime: " + Environment.Version + ", corlib " + typeof(object).Assembly.Location);
        Type monoRuntime = Type.GetType("Mono.Runtime");
        MethodInfo display = monoRuntime != null ? monoRuntime.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static) : null;
        Console.WriteLine("mono: " + (display != null ? display.Invoke(null, null) : "?"));

        try
        {
            HarmonyOnThisRuntime();
            RealPlugin();
            PluginLoaderWatch();
            NativeCalls(pluginDir);
            Extents();
            GazeFollowing();
            EyeSizes();
            Profiles();
            DlssWindows();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("EXCEPTION: " + ex);
        }

        Console.WriteLine();
        Console.WriteLine(_checks + " checks, " + _failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static void HarmonyOnThisRuntime()
    {
        Console.WriteLine("[HarmonyX on this runtime]");
        Console.WriteLine("  0Harmony " + typeof(Harmony).Assembly.GetName().Version);
        Harmony h = new Harmony("vws.hosttest.dummy");

        h.Patch(typeof(Dummy).GetMethod("Run"),
            new HarmonyMethod(typeof(DummyPatches).GetMethod("Prefix")),
            new HarmonyMethod(typeof(DummyPatches).GetMethod("Postfix")));
        h.Patch(typeof(Dummy).GetMethod("Read"), null, null, new HarmonyMethod(typeof(DummyPatches).GetMethod("Transpiler")));

        Dummy d = new Dummy();

        bool r = d.Run(100, 60, "big", false);
        Check(r && d.Seen == "50x25 small True", "prefix rewrote the arguments: original saw '" + d.Seen + "'");
        Check(DummyPatches.PostfixSaw == "state 100x60 result True", "postfix got the state: '" + DummyPatches.PostfixSaw + "'");

        r = d.Run(7, 9, "as is", false);
        Check(r && d.Seen == "7x9 as is False", "an untouched call passes through: '" + d.Seen + "'");
        Check(DummyPatches.PostfixSaw == "no state", "postfix without state: '" + DummyPatches.PostfixSaw + "'");

        Check(DummyPatches.Sites == 3, "transpiler saw " + DummyPatches.Sites + " field reads");
        Check((string)d.Read() == "redirected/redirected", "transpiled read returned '" + d.Read() + "'");
    }

    private static void RealPlugin()
    {
        Console.WriteLine("[the plugin against the real VamDlssNrPlugin.dll]");
        Assembly mod = typeof(NrCapture).Assembly;
        Console.WriteLine("  " + mod.GetName().Name + " " + mod.GetName().Version + " from " + mod.Location);

        List<string> log = new List<string>();
        Hooks.Info = delegate(string s) { log.Add("info: " + s); };
        Hooks.Warn = delegate(string s) { log.Add("warn: " + s); };
        Hooks.Error = delegate(string s) { log.Add("error: " + s); };

        string missing = Hooks.Resolve();
        Check(missing == null, "Resolve() reported missing: " + missing);
        Check(Hooks.PanelProblem.Length == 0, "panel: " + Hooks.PanelProblem);

        if (missing != null)
        {
            return;
        }

        Harmony h = new Harmony("vws.hosttest.real");
        Hooks.Apply(h);

        foreach (string line in log)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine("  output reads redirected in Compose: " + Hooks.ComposeSites);
        Console.WriteLine("  in RunNeuralRendering: " + Hooks.GuideSites + " guide copies and " + Hooks.ParamSites + " SetParams call(s) redirected");
        Check(Hooks.Hooked, "the frame hook is in place");
        Check(Hooks.ComposeSites == 4, "expected 4 reads of _output in 1.0.3's Compose, found " + Hooks.ComposeSites);
        Check(Hooks.PanelProblem.Length == 0, "panel after patching: " + Hooks.PanelProblem);
        Check(Hooks.WindowHooked && Hooks.WindowProblem.Length == 0 && Hooks.GuideSites == 3 && Hooks.ParamSites == 1,
            "the focus window's redirections are in (" + Hooks.GuideSites + " guide copies, " + Hooks.ParamSites + " SetParams calls): " + Hooks.WindowProblem);

        HeadsetUi.Resolve();
        HeadsetUi.Apply(new Harmony("vws.hosttest.headsetui"));
        Check(HeadsetUi.Hooked && HeadsetUi.Problem.Length == 0, "the headset's interface pass is hooked: " + HeadsetUi.Problem);
        DlssWindow.Apply(new Harmony("vws.hosttest.dlsswindow"));
        Check(DlssWindow.Hooked && DlssWindow.Problem.Length == 0, "the DLSS window's five hooks on VaM DLSS are in place: " + DlssWindow.Problem);
        MenuPointers.Apply(new Harmony("vws.hosttest.menupointers"));
        Check(MenuPointers.Hooked && MenuPointers.Problem.Length == 0, "the menu pointers' four hooks on VaM are in place: " + MenuPointers.Problem);
        {
            int rows, each;
            bool fits = true;

            for (int pages = 1; pages <= 12; pages++)
            {
                ControlPanel.TabGrid(pages, out rows, out each);
                fits &= rows * 2 * each >= pages && (rows - 1) * 2 * each < pages && (pages <= 4) == (rows == 1);
            }

            ControlPanel.TabGrid(7, out rows, out each);
            Check(fits && rows == 2 && each == 2, "the panel's tabs: up to four pages in one row, seven in two rows of four places (" + rows + " x " + each * 2 + ")");
        }

        Logs.Apply(new Harmony("vws.hosttest.logs"));
        Check(Logs.Hooked && Logs.Listeners == 2 && Logs.Problem.Length == 0, "BepInEx's two file writers are led through the log switches (" + Logs.Listeners + "): " + Logs.Problem);
        Check(Logs.MotionHooked, "VaM DLSS's write of vr_motion.log is led through its switch");
        Check(Logs.ModName == "VaM DLSS", "VaM DLSS's lines are known by its name in the log (" + Logs.ModName + ")");
        Check(Logs.SourceOf("VaM DLSS", "VaM DLSS - Model Resolution", "VaM DLSS") == Logs.Mod && Logs.SourceOf("VaM DLSS - Model Resolution", "VaM DLSS - Model Resolution", "VaM DLSS") == Logs.Own &&
            Logs.SourceOf("BepInEx", "VaM DLSS - Model Resolution", "VaM DLSS") == Logs.Other && Logs.SourceOf(null, "a", "b") == Logs.Other, "a line is told by whose it is");
        Check(Logs.Written(Logs.Own, false, "[vws] x", true, false) && Logs.Written(Logs.Mod, false, "[vdn] x", false, true) && Logs.Written(Logs.Other, false, "x", false, false), "with its switch on a line is written, and anybody else's always");
        Check(!Logs.Written(Logs.Own, false, "[vws] x", false, true) && !Logs.Written(Logs.Mod, false, "[vdn] x", true, false), "with its switch off it is not");
        Check(Logs.Written(Logs.Own, true, "[vws] x", false, false) && Logs.Written(Logs.Mod, true, "[vdn] x", false, false), "an error is written whatever the switches say");
        Check(Logs.Written(Logs.Own, false, "[vws] profile: 12 ms", false, false) && !Logs.Written(Logs.Mod, false, "[vws] profile: 12 ms", false, false), "and so is what the profiler was asked for");
        Check(Logs.NativeMask(true, true) == 0u && Logs.NativeMask(false, true) == 1u && Logs.NativeMask(true, false) == 2u && Logs.NativeMask(false, false) == 3u, "the native half is told which files are off");
        EyeSize.Apply(new Harmony("vws.hosttest.eyesize"));
        Check(EyeSize.FocusHooked && EyeSize.FocusSites == 2 && EyeSize.FocusProblem.Length == 0, "both writes of the eye scale in VaM's SteamVR plugin are led through here (" + EyeSize.FocusSites + "): " + EyeSize.FocusProblem);

        {
            // Kept: whatever the plugin is told, and however often, nothing is written.
            bool dimmed = false;
            float before = 1f;
            bool nothing = EyeSize.FocusStep(false, true, 1f, ref dimmed, ref before) == 0f && EyeSize.FocusStep(false, true, 1f, ref dimmed, ref before) == 0f &&
                EyeSize.FocusStep(true, true, 1f, ref dimmed, ref before) == 0f && EyeSize.FocusStep(true, true, 0.3333f, ref dimmed, ref before) == 0f;
            Check(nothing && !dimmed, "kept: the eye scale is not written while SteamVR has the focus, nor when it gives it back");

            // As before: halved, and told twice that the focus is gone (the second time the scale
            // in force is the half) what comes back is what it was -- not the half.
            dimmed = false;
            before = 1f;
            float first = EyeSize.FocusStep(false, false, 1.5f, ref dimmed, ref before);
            float second = EyeSize.FocusStep(false, false, 0.5f, ref dimmed, ref before);
            float back = EyeSize.FocusStep(true, false, 0.5f, ref dimmed, ref before);
            Check(first == 0.5f && second == 0.5f && back == 1.5f && !dimmed, "halved as before: told twice that the focus is gone, the scale given back is the one from before (" + back + ")");
            Check(EyeSize.FocusStep(true, false, 1.5f, ref dimmed, ref before) == 0f, "the focus given back without having been taken writes nothing");

            // Switched to kept while it stands halved: it is still given back.
            EyeSize.FocusStep(false, false, 1f, ref dimmed, ref before);
            Check(EyeSize.FocusStep(false, true, 0.5f, ref dimmed, ref before) == 0f && EyeSize.FocusStep(true, true, 0.5f, ref dimmed, ref before) == 1f && !dimmed,
                "switched to kept while halved: the scale from before still comes back");
        }
        Check(MenuPointers.Asked(7, 0, true) && MenuPointers.Asked(8, 0, false) && !MenuPointers.Asked(9, 0, false) && !MenuPointers.Asked(11, 0, false), "a pointer on a menu is asked every frame, one at rest every fourth");
        int askedTogether = 0;

        for (int frame = 0; frame < 8; frame++)
        {
            int asked = (MenuPointers.Asked(frame, 0, false) ? 1 : 0) + (MenuPointers.Asked(frame, 1, false) ? 1 : 0) + (MenuPointers.Asked(frame, 2, false) ? 1 : 0);
            askedTogether = Math.Max(askedTogether, asked);
        }

        Check(askedTogether == 1, "no two resting pointers are asked on the same frame: " + askedTogether);
        Looks();
        Check(HeadsetUi.Line().Length == 0, "the headset's interface pass says nothing until it is asked for");

        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        string[][] expected =
        {
            new[] { "NrCapture", "Compose", "transpiler" },
            new[] { "NrCapture", "RunNeuralRendering", "prefix+postfix+transpiler" },
            new[] { "VamDlssNrPanel", "RenderTab", "postfix" },
            new[] { "VamDlssNrPlugin", "SaveNow", "postfix" },
        };

        foreach (string[] e in expected)
        {
            Type t = mod.GetType("VamDlssNr." + e[0]);
            MethodInfo m = null;

            foreach (MethodInfo candidate in t.GetMethods(any))
            {
                if (candidate.Name == e[1] && (e[1] != "RunNeuralRendering" || candidate.GetParameters().Length == 4))
                {
                    m = candidate;
                }
            }

            Patches info = m != null ? Harmony.GetPatchInfo(m) : null;
            bool ok = info != null;

            if (ok)
            {
                if (e[2] == "transpiler") ok = info.Transpilers.Count == 1;
                else if (e[2] == "postfix") ok = info.Postfixes.Count == 1;
                else ok = info.Prefixes.Count == 1 && info.Postfixes.Count == 1 && info.Transpilers.Count == 1;
            }

            Check(ok, e[0] + "." + e[1] + " carries our " + e[2]);
        }

        // The accessor the transpiler put into Compose, on the path it takes when the model ran at
        // full size: no view for this capture, so the field itself comes back.
        NrCapture capture = (NrCapture)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(NrCapture));
        Check(Hooks.OutputFor(capture) == null, "OutputFor on a bare capture returns its (null) field");

        // Where a window goes when there is no headset to ask: the middle, on a mono view and on a
        // stereo one whose capture has no camera.
        float[] centres = new float[4];
        Hooks.LensCentres(capture, 1, centres);
        Check(centres[0] == 0.5f && centres[1] == 0.5f, "a mono view's window is centred: " + centres[0] + "," + centres[1]);
        Hooks.LensCentres(capture, 2, centres);
        Check(centres[0] == 0.5f && centres[2] == 0.5f && centres[1] == 0.5f && centres[3] == 0.5f, "a stereo view without a camera falls back to the middle");

        h.UnpatchSelf();
    }

    // The in-headset panel finds its session script by a postfix on the one method of VaM's that
    // creates every plugin script. Here: that the method is where it is looked for, and takes it.
    private static void PluginLoaderWatch()
    {
        Console.WriteLine("[watching VaM's plugin loader]");
        List<string> log = new List<string>();
        Hooks.Warn = delegate(string s) { log.Add("warn: " + s); };
        Hooks.Error = delegate(string s) { log.Add("error: " + s); };

        ControlPanel.Watch();

        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo create = typeof(MVRPluginManager).GetMethod("CreateScriptController", any);
        Patches info = create != null ? Harmony.GetPatchInfo(create) : null;
        Check(ControlPanel.Watching, "the loader is watched");
        Check(info != null && info.Postfixes.Count == 1, "MVRPluginManager.CreateScriptController carries our postfix");

        // What the loader returns for a script it turned away is null; that passes through quietly.
        ControlPanel.ScriptCreated(null);
        Check(ControlPanel.PanelCount == 0, "a script that was not created fills no panel");

        foreach (string line in log)
        {
            Console.WriteLine("  " + line);
        }

        Check(log.Count == 0, "nothing was warned about: " + string.Join(" | ", log.ToArray()));
    }

    private static void NativeCalls(string pluginDir)
    {
        Console.WriteLine("[P/Invoke into the native half]");
        bool loaded = Native.Load(pluginDir);
        Check(loaded, "native load: " + Native.Problem);

        if (!loaded)
        {
            return;
        }

        Check(Native.EventFunc != IntPtr.Zero, "render callback pointer");

        // Nothing is executed here (there is no render thread to hand the ids to); this is about
        // the arguments and the out-parameters crossing the boundary intact.
        int id = Native.PushPass(true, 3, 2, 0.5f, 1, null, 0f);
        Check((id & 0x7FFF0000) == 0x57530000, "push returned event id 0x" + id.ToString("X8"));

        // The window crosses as an array of eight numbers; the guides' calls as plain values.
        float[] window = { 0.25f, 0.25f, 0.5f, 0.5f, 0.25f, 0.25f, 0.5f, 0.5f };
        int idWindow = Native.PushPass(false, 3, 2, 1f, 0, window, 0.35f);
        float[] before = { 0.2f, 0.3f, 0.4f, 0.6f, 0.2f, 0.3f, 0.4f, 0.6f };
        int idMoved = Native.PushGuide(3, 0, 2, window, before, true);
        Check((idMoved & 0x7FFF0000) == 0x57530000, "a guide push with the window as it was returned 0x" + idMoved.ToString("X8"));
        int idGuide = Native.PushGuide(3, 1, 2, window, null, false);
        int idRegister = Native.PushRegisterGuide(3, 1, IntPtr.Zero, IntPtr.Zero, 77);
        Check((idWindow & 0x7FFF0000) == 0x57530000 && (idGuide & 0x7FFF0000) == 0x57530000 && (idRegister & 0x7FFF0000) == 0x57530000,
            "window and guide pushes returned 0x" + idWindow.ToString("X8") + ", 0x" + idGuide.ToString("X8") + ", 0x" + idRegister.ToString("X8"));

        int id2 = Native.PushRegister(5, new IntPtr(0), new IntPtr(0), new IntPtr(0), new IntPtr(0), 3, 14, 4242);
        Check((id2 & 0x7FFF0000) == 0x57530000 && id2 != id, "second push returned 0x" + id2.ToString("X8"));

        int ready = -1, error = -1, hr = -1;
        uint token = 99;
        Native.Poll(5, out ready, out error, out token, out hr);
        Check(ready == 0 && error == 0 && token == 0 && hr == 0, "poll of an untouched set: ready=" + ready + " error=" + error + " token=" + token + " hr=" + hr);

        string drained = Native.DrainLog();
        Check(drained == null, "nothing logged yet: '" + drained + "'");
        Check(Native.ErrorName(-3).Length > 0, "error names");
    }

    // The window following the eye: the arithmetic that decides where it is aimed, with the eye
    // tracker's part played by hand. (The tracker itself is SteamVR's and is not here.)
    // DLSS on a window of each eye: the window's size and place.
    // The room's look: what the cameras' grey is shown in, as the numbers the native half takes.
    private static void Looks()
    {
        float[] block = new float[96];

        for (int i = 0; i < block.Length; i++)
        {
            block[i] = 7f;
        }

        Passthrough.LookNumbers(Passthrough.LookGrey, 1f, 0f, 0f, block);
        Check(block[83] == 0f && block[84] == 0f && block[92] == 0f && block[82] == 7f && block[93] == 7f, "the camera's grey is look 0, and nothing beside the look's ten numbers is written");

        bool ramps = true, rising = true;

        for (int look = 1; look <= 4; look++)
        {
            Passthrough.LookNumbers(look, 0f, 0f, 0f, block);
            ramps &= block[83] == 1f;
            float before = -1f;

            for (int stop = 0; stop < 3; stop++)
            {
                float luma = 0.299f * block[84 + stop * 3] + 0.587f * block[85 + stop * 3] + 0.114f * block[86 + stop * 3];
                rising &= luma > before + 0.2f || stop == 0;
                before = luma;
            }
        }

        Check(ramps, "night vision, amber, cold blue and sepia are ramps");
        Check(rising, "every ramp gets brighter from its dark end to its bright one, so the picture stays readable");
        Passthrough.LookNumbers(1, 0f, 0f, 0f, block);
        Check(block[88] > block[87] * 3f && block[88] > block[89] * 3f, "night vision's middle is green: " + block[87] + " " + block[88] + " " + block[89]);
        Passthrough.LookNumbers(5, 0f, 0f, 0f, block);
        Check(block[83] == 2f, "heat is look 2");
        Passthrough.LookNumbers(Passthrough.LookOwn, 1f, 0.5f, 0f, block);
        Check(block[83] == 1f && block[87] == 1f && block[88] == 0.5f && block[89] == 0f && block[84] < 0.05f && block[92] == 0.75f, "your colour is a ramp from black through the colour towards white");
        Passthrough.LookNumbers(Passthrough.LookGuessed, 0f, 0f, 0f, block);
        Check(block[83] == 3f, "the network's colours are look 3");
        Check(Passthrough.LookNames.Length == 8, "every look has a name in the panel");
    }

    private static void DlssWindows()
    {
        Console.WriteLine("DLSS window:");
        Check(DlssWindow.Extent(2040, 0.5f) == 1020 && DlssWindow.Extent(2080, 0.5f) == 1040, "half an eye of 2040x2080 is 1020x1040");
        Check(DlssWindow.Extent(2040, 0.333f) % 2 == 0 && DlssWindow.Extent(2040, 0.333f) == 680, "the window's size is even: " + DlssWindow.Extent(2040, 0.333f));
        Check(DlssWindow.Extent(2040, 1f) == 2040 && DlssWindow.Extent(2040, 0.99f) == 2040, "a window of the whole eye is no window");
        Check(DlssWindow.Extent(600, 0.25f) == 256, "it is never smaller than 256: " + DlssWindow.Extent(600, 0.25f));
        Check(DlssWindow.Extent(400, 0.5f) == 400, "a picture too small to bother with is left whole");
        Check(DlssWindow.Origin(2040, 1020, 0.5f) == 510, "centred in the eye it begins a quarter in");
        Check(DlssWindow.Origin(2040, 1020, 0.1f) == 0 && DlssWindow.Origin(2040, 1020, 0.95f) == 1020, "it stays inside the eye however far out the eye looks");
        Check(DlssWindow.Origin(2040, 1020, 0.56f) == 632, "it follows its centre: " + DlssWindow.Origin(2040, 1020, 0.56f));
        Check(!DlssWindow.Moves(510, 600, 1020) && DlssWindow.Moves(510, 800, 1020) && DlssWindow.Moves(510, 200, 1020), "it moves only when the eye has left its middle fifth");
    }

    // The main-thread profile: what is said of ten seconds of frames.
    private static void Profiles()
    {
        Console.WriteLine("profile:");

        // 100 frames of 30 ms at 1000 ticks a millisecond: 3 physics steps a frame of 6 ms each
        // (2 of scripts), Update 3 (2.5 scripts), 1 between, LateUpdate 2, 0.5 before rendering,
        // rendering 4, 1 between frames, and half a millisecond nobody saw.
        Profile.Window w = new Profile.Window();
        w.Frames = 100;
        w.Steps = 300;
        w.All = 100 * 30000;
        w.Worst = 61000;
        w.FixedSpan = 300 * 6000;
        w.Scripts[Profile.Fixed] = 300 * 2000;
        w.UpdateSpan = 100 * 3000;
        w.Scripts[Profile.Update] = 100 * 2500;
        w.Between = 100 * 1000;
        w.LateSpan = 100 * 2000;
        w.BeforeRender = 100 * 500;
        w.RenderSpan = 100 * 4000;
        w.Wait = 100 * 1000;

        List<Profile.Slot> slots = new List<Profile.Slot>();

        foreach (string[] one in new[] { new[] { "Small", "0", "10000" }, new[] { "Big", "0", "500000" }, new[] { "Middle", "1", "250000" }, new[] { "Idle", "2", "0" } })
        {
            Profile.Slot slot = new Profile.Slot();
            slot.Name = one[0];
            slot.Phase = int.Parse(one[1]);
            slot.Ticks = long.Parse(one[2]);
            slot.Calls = 300;
            slots.Add(slot);
        }

        // (this thread's cycles, 2000 to the millisecond: at work through its physics steps, and
        // for one of the ten milliseconds the engine takes before rendering -- the rest is waiting)
        w.Busy[1] = 100 * 36000;
        w.Busy[5] = 100 * 2000;

        List<string> lines = w.Describe(1000.0, slots, 2, 2000.0);
        Check(lines[0].Contains("at work 19.00 ms") && lines[1].Contains("[at work 18.00]") && string.Join("|", lines.ToArray()).Contains("take the last frame)  [at work 1.00]"),
            "a phase's length and how much of it the thread worked are both said: " + lines[0]);
        Check(!string.Join("|", w.Describe(1000.0, slots, 2, 0.0).ToArray()).Contains("at work"), "without the thread's cycles nothing is said of them");
        string all = string.Join("\n", lines.ToArray());
        Check(lines[0].Contains("33.3 fps") && lines[0].Contains("30.00 ms a frame") && lines[0].Contains("61.0"), "the frame rate and the frame's length are the window's: " + lines[0]);
        Check(lines[1].Contains("18.00 ms") && lines[1].Contains("3.00 a frame") && lines[1].Contains("6.00 ms each") && lines[1].Contains("FixedUpdate 2.00") && lines[1].Contains("the rest 4.00"),
            "a physics step is split into its scripts and the engine: " + lines[1]);
        Check(all.Contains("   0.50 ms  not accounted for"), "what no mark covers is said, not hidden");
        Check(lines[lines.Count - 2].Contains("Big") && lines[lines.Count - 2].Contains("5.00 ms") && lines[lines.Count - 1].Contains("Middle") && !all.Contains("Small") && !all.Contains("Idle"),
            "the scripts are named by what they take, the most first, and no more of them than asked for");
        Check(new Profile.Window().Describe(1000.0, slots, 5, 0.0).Count == 1, "a window without frames says so and nothing else");

        string brief = w.Short(1000.0, 2000.0);
        Check(brief.Contains("33.3 fps") && brief.Contains("3.00 physics steps a frame") && brief.Contains("6.00 ms each (scripts 2.00, engine 4.00)") && brief.Contains("at work 19.00") && brief.Contains("the longest 61.0"),
            "a trial's one line has the frame rate, the steps and what a step is made of: " + brief);
    }

    // The headset's picture size as VaM DLSS measured it, against SteamVR's and the eye texture.
    private static void EyeSizes()
    {
        Console.WriteLine("eye size:");

        // a healthy session: measured at full size, then run at a third
        Check(EyeSize.Judge(2040, 2080, 1f, 2040, 2080, 2040, 2080, 1f) == EyeSize.Fine, "a right measurement at full size is left alone");
        Check(EyeSize.Judge(2040, 2080, 1f, 2040, 2080, 680, 693, 0.3333f) == EyeSize.Fine, "a right measurement is left alone under a DLSS mode");
        Check(EyeSize.Judge(3060, 3120, 1.5f, 2040, 2080, 3060, 3120, 1.5f) == EyeSize.Fine, "a measurement taken at VaM's render scale 1.5 is the same headset");
        Check(EyeSize.Judge(2040, 2080, 1f, 2040, 2080, 2040, 2080, 0.3333f) == EyeSize.Fine, "a right measurement stays right while the texture has yet to follow the scale");
        Check(EyeSize.Judge(2038, 2079, 1f, 2040, 2080, 2038, 2079, 1f) == EyeSize.Fine, "a pixel or two of rounding is no difference");

        // the session that went wrong: half the size measured, the textures right a moment later
        Check(EyeSize.Judge(1020, 1040, 1f, 2040, 2080, 680, 693, 0.3333f) == EyeSize.AnchorWrong, "half the headset's size measured, both witnesses against it");
        Check(EyeSize.Judge(1020, 1040, 1f, 2040, 2080, 2040, 2080, 1f) == EyeSize.AnchorWrong, "half the headset's size measured, seen at full scale");
        Check(EyeSize.Judge(1020, 1040, 1f, 2040, 2080, 1020, 1040, 1f) == EyeSize.Unsettled, "while the eye texture is itself not SteamVR's size nothing is decided");
        Check(EyeSize.Judge(1020, 1040, 1f, 2040, 2080, 340, 347, 0.3333f) == EyeSize.Unsettled, "an eye texture that agrees with the measurement and not with SteamVR decides nothing");

        // nothing to judge by
        Check(EyeSize.Judge(0, 0, 0f, 2040, 2080, 2040, 2080, 1f) == EyeSize.Fine, "nothing measured yet: nothing to put right");
        Check(EyeSize.Judge(1020, 1040, 1f, 0, 0, 680, 693, 0.3333f) == EyeSize.Fine, "without SteamVR's size nothing is judged");
        Check(EyeSize.Judge(1020, 1040, 1f, 2040, 2080, 0, 0, 0.3333f) == EyeSize.Fine, "without an eye texture nothing is judged");

        // only a lasting difference, against one and the same size
        EyeSize.Patience patience = new EyeSize.Patience();
        int fired = 0, at = -1;

        for (int i = 0; i < 44; i++)
        {
            fired += patience.Step(true, 2040, 2080) ? 1 : 0;
        }

        Check(fired == 0, "a wrong measurement is not written anew before it has lasted");
        fired += patience.Step(false, 2040, 2080) ? 1 : 0;

        for (int i = 0; i < 44; i++)
        {
            fired += patience.Step(true, 2040, 2080) ? 1 : 0;
        }

        Check(fired == 0, "a frame of doubt starts the wait again");
        fired += patience.Step(true, 2448, 2496) ? 1 : 0;

        for (int i = 0; i < 60 && at < 0; i++)
        {
            if (patience.Step(true, 2448, 2496))
            {
                at = i;
            }
        }

        Check(fired == 0 && at == 43, "another size from SteamVR starts the wait again, and 45 frames of it are enough: fired at " + at);

        fired = 0;

        for (int i = 0; i < 200; i++)
        {
            fired += patience.Step(false, 2448, 2496) ? 1 : 0;
        }

        Check(fired == 0, "a right measurement is never written anew");

        // the scale VaM DLSS takes for the user's own, against VaM's Render Scale
        Check(!EyeSize.ScaleWrong(1f, 0.3333f, 0.3333f, 1f), "the user's scale as VaM has it is left alone");
        Check(!EyeSize.ScaleWrong(1.5f, 0.75f, 0.75f, 1.5f), "and so at a render scale of 1.5");
        Check(EyeSize.ScaleWrong(0.3333f, 0.1111f, 0.1111f, 1f), "its own DLSS scale taken for the user's is wrong");
        Check(EyeSize.ScaleWrong(0.5f, 0.1667f, 0.1667f, 1f), "and so is half");
        Check(!EyeSize.ScaleWrong(0.5f, 0.5f, 0.5f, 1f), "without upscaling the scale is not ours to judge");
        Check(!EyeSize.ScaleWrong(0.3333f, 0.1111f, 1f, 1f), "a scale somebody has just written is VaM DLSS's to take first");
        Check(!EyeSize.ScaleWrong(0.3333f, 0.1111f, 0.1111f, 0f), "without VaM's preference nothing is judged");
        Check(!EyeSize.ScaleWrong(0f, 0f, 1f, 1f), "nothing held yet: nothing to put right");

        // put right as often as it goes wrong over a session, but not in a tug-of-war
        EyeSize.Allowance allowance = new EyeSize.Allowance();
        int taken = 0;

        for (int i = 0; i < 40; i++)
        {
            taken += allowance.Take(100f + i * 30f) ? 1 : 0;
        }

        Check(taken == 40, "something that goes wrong every half minute is put right every time: " + taken + " of 40");
        allowance = new EyeSize.Allowance();
        taken = 0;

        for (int i = 0; i < 20; i++)
        {
            taken += allowance.Take(100f + i * 0.75f) ? 1 : 0;
        }

        Check(taken == 4, "something that goes wrong again at once is put right four times and then left: " + taken);
        Check(!allowance.Take(119f) && allowance.Take(120.5f), "and again once the first of those is twenty seconds ago");
    }

    private static void GazeFollowing()
    {
        Console.WriteLine("[gaze following]");
        float[] rest = { 0.55f, 0.48f, 0.45f, 0.48f };
        float[] aim = new float[4];
        GazeFilter filter = new GazeFilter();

        // No tracker: the window rests at the lens centre.
        int state = filter.Update(0f, false, new float[4], rest, aim);
        Check(state == GazeFilter.AtRest && aim[0] == 0.55f && aim[3] == 0.48f, "without gaze the aim is the lens centre: state " + state + " aim " + aim[0] + "," + aim[1]);

        // The first gaze is gone to at once.
        float[] gaze = { 0.30f, 0.60f, 0.25f, 0.60f };
        state = filter.Update(1f, true, gaze, rest, aim);
        Check(state == GazeFilter.Following && aim[0] == 0.30f && aim[1] == 0.60f && aim[2] == 0.25f, "the first gaze sample is where the window goes");

        // Wander inside the dead zone moves nothing; leaving it moves the window to the gaze.
        state = filter.Update(1.01f, true, new[] { 0.32f, 0.61f, 0.27f, 0.61f }, rest, aim);
        Check(state == GazeFilter.Following && aim[0] == 0.30f && aim[1] == 0.60f, "gaze within the dead zone leaves the window where it is: " + aim[0] + "," + aim[1]);
        state = filter.Update(1.02f, true, new[] { 0.36f, 0.61f, 0.31f, 0.61f }, rest, aim);
        Check(aim[0] == 0.36f && aim[1] == 0.61f && aim[2] == 0.31f, "gaze beyond the dead zone takes the window with it: " + aim[0] + "," + aim[1]);

        // A blink: held, then back to rest over the return time, then at rest.
        state = filter.Update(2.0f, false, gaze, rest, aim);
        Check(state == GazeFilter.Holding && aim[0] == 0.36f, "a lost eye holds the window: state " + state);
        state = filter.Update(2.3f, false, gaze, rest, aim);
        Check(state == GazeFilter.Holding && aim[0] == 0.36f, "still held inside the hold time");
        state = filter.Update(2.55f, false, gaze, rest, aim);
        Check(state == GazeFilter.Returning && aim[0] > 0.36f && aim[0] < 0.55f, "then travels back towards the lens centre: state " + state + " at " + aim[0]);
        state = filter.Update(3.0f, false, gaze, rest, aim);
        Check(state == GazeFilter.AtRest && Math.Abs(aim[0] - 0.55f) < 1e-6f && Math.Abs(aim[2] - 0.45f) < 1e-6f, "and arrives: state " + state + " at " + aim[0]);

        // The eye comes back inside the hold: the window goes straight to it.
        filter.Update(4.0f, true, gaze, rest, aim);
        filter.Update(4.1f, false, gaze, rest, aim);
        state = filter.Update(4.2f, true, new[] { 0.31f, 0.60f, 0.26f, 0.60f }, rest, aim);
        Check(state == GazeFilter.Following && aim[0] == 0.31f, "an eye found again is followed at once: " + aim[0]);

        // On a monitor the target is a figure, and the window glides: moved just far enough to
        // keep the target within the dead zone, and not at all while it stays inside.
        GazeFilter glide = new GazeFilter();
        glide.Glide = true;
        glide.DeadZone = 0.04f;
        float[] middle = { 0.5f, 0.5f, 0.5f, 0.5f };
        glide.Update(0f, true, new[] { 0.50f, 0.50f, 0.50f, 0.50f }, middle, aim);
        glide.Update(0.1f, true, new[] { 0.53f, 0.50f, 0.53f, 0.50f }, middle, aim);
        Check(aim[0] == 0.50f, "a figure moving inside the dead zone leaves the window still: " + aim[0]);
        glide.Update(0.2f, true, new[] { 0.60f, 0.47f, 0.60f, 0.47f }, middle, aim);
        Check(Math.Abs(aim[0] - 0.56f) < 1e-6f && aim[1] == 0.50f, "one moving out of it draws the window after it, no further than it must: " + aim[0] + "," + aim[1]);
        glide.Update(0.3f, true, new[] { 0.58f, 0.47f, 0.58f, 0.47f }, middle, aim);
        Check(Math.Abs(aim[0] - 0.56f) < 1e-6f, "and coming back a little moves nothing: " + aim[0]);

        // A window fitted to the people in view, on a 2560x1440 screen with the area of a
        // 45% x 90% window (1152 x 1296 pixels) for the network. Boxes are pixels, y up.
        FitTracker fit = new FitTracker();
        fit.Lead = 0f; // (these are the window's ways at rest; what it does while the picture moves fast is checked below)
        const float area = 1152f * 1296f;
        int shapeW, shapeH;

        // A standing figure: taller than wide, well within the screen -> the upright shape,
        // drawn no larger than the figure needs, around the figure.
        float[] standing = { 1000f, 200f, 1400f, 1300f };
        int fitState = fit.Update(0f, true, standing, area, 2560, 1440, 1f, 8f);
        FitTracker.ShapeSize(fit.Shape, area, 2560, 1440, out shapeW, out shapeH);
        Check(fitState == FitTracker.Fitting && fit.Shape == 0 && shapeW == 946 && shapeH == 1440, "a standing figure gets the upright shape: shape " + fit.Shape + " " + shapeW + "x" + shapeH);
        Check(fit.Zoom == 1f && Math.Abs(fit.CentreX - 1200f) < 0.5f && Math.Abs(fit.CentreY - 750f) < 0.5f, "at its plain size, centred on the figure: zoom " + fit.Zoom + " at " + fit.CentreX + "," + fit.CentreY);

        // The figure walks: nothing moves while it stays inside, then the window is pushed along.
        fit.Update(0.1f, true, new[] { 1100f, 200f, 1500f, 1300f }, area, 2560, 1440, 1f, 8f);
        Check(Math.Abs(fit.CentreX - 1200f) < 0.5f, "a figure moving inside the window leaves it where it is: " + fit.CentreX);
        fit.Update(0.2f, true, new[] { 1400f, 200f, 1800f, 1300f }, area, 2560, 1440, 1f, 8f);
        Check(Math.Abs(fit.CentreX - (1800f - shapeW * 0.5f)) < 0.5f, "one reaching its edge pushes it along, no further than it must: " + fit.CentreX);

        // Two people side by side, wider than the upright shape: the shape is kept for its dwell
        // time (the window just cannot hold them), then gives way to one that can.
        float[] couple = { 500f, 200f, 2000f, 1300f };
        fit.Update(0.5f, true, couple, area, 2560, 1440, 1f, 8f);
        Check(fit.Shape == 0, "a shape is not dropped the moment it stops fitting: shape " + fit.Shape);
        fit.Update(2.5f, true, couple, area, 2560, 1440, 1f, 8f);
        FitTracker.ShapeSize(fit.Shape, area, 2560, 1440, out shapeW, out shapeH);
        Check(fit.Shape == 2 && shapeW * fit.Zoom >= 1500f - 1f && shapeH * fit.Zoom >= 1100f - 1f && shapeW * fit.Zoom <= 2560.5f && shapeH * fit.Zoom <= 1440.5f,
            "after its dwell it gives way to the one that holds them (the square cannot, inside the screen): shape " + fit.Shape + " " + shapeW + "x" + shapeH + " zoom " + fit.Zoom);

        // A lying figure, much wider than tall: the wide shape stays.
        float[] lying = { 300f, 500f, 2300f, 1000f };
        fit.Update(5f, true, lying, area, 2560, 1440, 1f, 8f);
        FitTracker.ShapeSize(fit.Shape, area, 2560, 1440, out shapeW, out shapeH);
        Check(fit.Shape == 2 && shapeW == 1577 && shapeH == 946, "a lying figure gets the wide shape: shape " + fit.Shape + " " + shapeW + "x" + shapeH);
        Check(shapeW * fit.Zoom >= 2000f - 1f && shapeH * fit.Zoom >= 500f - 1f, "drawn large enough to hold it: " + shapeW * fit.Zoom + "x" + shapeH * fit.Zoom);

        // It grows at once and shrinks only when there is clearly room to spare.
        float zoomWas = fit.Zoom;
        fit.Update(5.1f, true, new[] { 350f, 500f, 2250f, 1000f }, area, 2560, 1440, 1f, 8f);
        Check(fit.Zoom == zoomWas, "a little room to spare shrinks nothing: " + fit.Zoom);
        fit.Update(5.2f, true, new[] { 800f, 600f, 1800f, 900f }, area, 2560, 1440, 1f, 8f);
        Check(fit.Zoom < zoomWas && fit.Zoom >= 1f, "a lot of room does, down to the plain size at most: " + fit.Zoom);

        // Nobody in view: held, then back to the middle at the plain size, the shape unchanged.
        float[] nobody = new float[4];
        fitState = fit.Update(6f, false, nobody, area, 2560, 1440, 1f, 8f);
        Check(fitState == FitTracker.Holding, "nobody in view holds the window: state " + fitState);
        fitState = fit.Update(8f, false, nobody, area, 2560, 1440, 1f, 8f);
        Check(fitState == FitTracker.AtRest && fit.Shape == 2 && fit.Zoom == 1f && Math.Abs(fit.CentreX - 1280f) < 0.5f && Math.Abs(fit.CentreY - 720f) < 0.5f,
            "and then rests in the middle at its plain size, shape kept: state " + fitState + " shape " + fit.Shape + " zoom " + fit.Zoom);

        // The camera swung fast: the figures cross the screen and their box turns from upright to
        // wide and back. No new shape while that lasts (each is a rebuild), the window held around
        // the box's middle and drawn a little larger; a shape that fits better once it is over.
        FitTracker swing = new FitTracker();
        swing.Update(0f, true, standing, area, 2560, 1440, 1f, 8f);
        bool reshaped = false;
        float off = 0f;

        for (int i = 1; i <= 40; i++)
        {
            float t = i * 0.05f, x = 300f + 1500f * Math.Abs((float)Math.Sin(t * 3.0));
            float[] moving = (i / 5) % 2 == 0 ? new[] { x, 500f, x + 700f, 1000f } : new[] { x, 300f, x + 350f, 1300f };
            swing.Update(t, true, moving, area, 2560, 1440, 1f, 8f);
            reshaped = reshaped || swing.Shape != 0;

            if (i == 40)
            {
                off = Math.Abs(swing.CentreX - (moving[0] + moving[2]) * 0.5f);
            }
        }

        Check(!reshaped && swing.Moving(2f), "no new shape while the picture moves fast: shape " + swing.Shape + ", moving " + swing.Moving(2f));
        Check(off < 2560f * 0.03f, "the window is held around the box's middle while it moves: " + off + " px off");
        swing.Update(2.1f, true, lying, area, 2560, 1440, 1f, 8f);
        swing.Update(5f, true, lying, area, 2560, 1440, 1f, 8f);
        Check(swing.Shape == 2 && !swing.Moving(5f), "and the shape that fits is taken once it is over: shape " + swing.Shape);

        // Flown towards a figure: its box hardly moves across the screen, it grows and changes its
        // proportions (a whole body, then head and shoulders). No new shape while it does; one,
        // when the view has come to rest.
        FitTracker flown = new FitTracker();
        flown.Update(0f, true, standing, area, 2560, 1440, 1f, 8f);
        bool early = false;

        for (int i = 1; i <= 100; i++)
        {
            float t = i * 0.05f, g = i * 9f;
            flown.Update(t, true, new[] { 1000f - g, 700f - g * 0.3f, 1400f + g, 1300f }, area, 2560, 1440, 1f, 8f);
            early = early || flown.Shape != 0;
        }

        Check(!early, "no new shape while the view is being changed: shape " + flown.Shape);
        float[] close = { 100f, 430f, 2300f, 1300f };
        flown.Update(5.05f, true, close, area, 2560, 1440, 1f, 8f);
        flown.Update(5.5f, true, close, area, 2560, 1440, 1f, 8f);
        Check(flown.Shape == 0, "nor the moment it stops: shape " + flown.Shape);
        flown.Update(6.2f, true, close, area, 2560, 1440, 1f, 8f);
        Check(flown.Shape == 2, "but once it has been at rest for a second: shape " + flown.Shape);

        // A figure lost for a moment is not a new figure: no new shape at once when it is back.
        FitTracker blink = new FitTracker();
        blink.Update(0f, true, standing, area, 2560, 1440, 1f, 8f);
        blink.Update(1f, true, standing, area, 2560, 1440, 1f, 8f);
        blink.Update(1.1f, false, nobody, area, 2560, 1440, 1f, 8f);
        blink.Update(1.3f, true, new[] { 900f, 500f, 1500f, 1000f }, area, 2560, 1440, 1f, 8f);
        Check(blink.Shape == 0, "a figure back after a fifth of a second keeps the shape: shape " + blink.Shape);

        // With the model resolution at a half the window may shrink to where the raster is 1:1.
        FitTracker small = new FitTracker();
        small.Update(0f, true, new[] { 1200f, 600f, 1360f, 900f }, area, 2560, 1440, 0.5f, 4f);
        Check(Math.Abs(small.Zoom - 0.5f) < 1e-4f, "a far-off figure at model resolution 50% gets the window down to half: " + small.Zoom);

        // SteamVR is not in this process: asking is refused quietly, with a reason.
        float[] uv = new float[4];
        bool got = false;
        string threw = null;

        try
        {
            got = Gaze.Read(uv, 1, 10f);
        }
        catch (Exception ex)
        {
            threw = ex.GetType().Name + ": " + ex.Message;
        }

        Console.WriteLine("  gaze without SteamVR: " + (threw ?? Gaze.Status));
        Check(threw == null && !got && Gaze.Status.Length != 0, "asking SteamVR for gaze where there is no SteamVR fails quietly");
    }

    private static void Extents()
    {
        Console.WriteLine("[model extents]");
        int ww, wh;

        Check(Hooks.WorkExtent(2560, 1440, 0.5f, 1, out ww, out wh) && ww == 1280 && wh == 720, "1440p at 50% -> " + ww + "x" + wh);
        Check(Hooks.WorkExtent(2560, 1440, 0.25f, 1, out ww, out wh) && ww == 640 && wh == 360, "1440p at 25% -> " + ww + "x" + wh);
        Check(Hooks.WorkExtent(1920, 1080, 0.67f, 1, out ww, out wh) && ww == 1286 && wh == 724, "1080p at 67% -> " + ww + "x" + wh);
        Check(!Hooks.WorkExtent(2560, 1440, 1f, 1, out ww, out wh) && ww == 2560 && wh == 1440, "100% changes nothing -> " + ww + "x" + wh);

        // A double-wide stereo frame scales per eye, and the width stays a whole number of eyes.
        Check(Hooks.WorkExtent(3052, 1680, 0.5f, 2, out ww, out wh) && ww == 1526 && wh == 840, "VR 2 x 1526x1680 at 50% -> " + ww + "x" + wh);
        Check(Hooks.WorkExtent(3054, 1681, 0.33f, 2, out ww, out wh) && (ww % 2) == 0 && ww == 2 * 504 && wh == 555, "VR odd sizes at 33% -> " + ww + "x" + wh);
        Check(!Hooks.WorkExtent(3053, 1680, 0.5f, 2, out ww, out wh), "an odd double-wide width is left alone");

        // Small frames: never below 256 on the short side, and a frame already that small runs as is.
        Check(Hooks.WorkExtent(512, 512, 0.25f, 1, out ww, out wh) && ww == 256 && wh == 256, "512 thumbnail at 25% is floored -> " + ww + "x" + wh);
        Check(!Hooks.WorkExtent(256, 256, 0.5f, 1, out ww, out wh), "a 256 thumbnail is left alone");
        Check(Hooks.WorkExtent(1280, 300, 0.5f, 1, out ww, out wh) && wh == 256 && ww == 1092, "a short frame is floored on its short side -> " + ww + "x" + wh);

        // Above the frame's size.
        Check(Hooks.WorkExtent(1920, 1080, 1.5f, 1, out ww, out wh) && ww == 2880 && wh == 1620, "1080p at 150% -> " + ww + "x" + wh);
        Check(!Hooks.WorkExtent(7680, 4320, 2f, 1, out ww, out wh), "an 8K still is not supersampled");
        Check(Hooks.WorkExtent(7680, 4320, 0.5f, 1, out ww, out wh) && ww == 3840 && wh == 2160, "an 8K still at 50% -> " + ww + "x" + wh);
        Check(Hooks.WorkExtent(15360, 8640, 0.75f, 1, out ww, out wh) && ww == 11520 && wh == 6480, "a 16K still is still reduced, whatever its size -> " + ww + "x" + wh);

        // The focus window: a part of each eye, never the other eye's, never too small to be useful.
        int winW, winH;
        Check(Hooks.WindowExtent(2560, 1440, 0.5f, 0.5f, 1, out winW, out winH) && winW == 1280 && winH == 720, "a 50% window of 1440p -> " + winW + "x" + winH);
        Check(Hooks.WindowExtent(4096, 2240, 0.4f, 0.4f, 2, out winW, out winH) && winW == 819 && winH == 896, "a 40% window of 2 x 2048x2240 -> " + winW + "x" + winH + " per eye");
        Check(!Hooks.WindowExtent(2560, 1440, 1f, 1f, 1, out winW, out winH), "a window of 1 is no window");
        Check(!Hooks.WindowExtent(1024, 600, 0.3f, 0.3f, 1, out winW, out winH), "a window under 256 pixels on its short side is not used");
        Check(!Hooks.WindowExtent(3053, 1680, 0.5f, 0.5f, 2, out winW, out winH), "an odd double-wide width gets no window");

        // The monitor's window has a width and a height of its own: upright on a wide screen.
        Check(Hooks.WindowExtent(2560, 1440, 0.45f, 0.9f, 1, out winW, out winH) && winW == 1152 && winH == 1296, "a 45% x 90% window of 1440p -> " + winW + "x" + winH);
        Check(Hooks.WindowExtent(2560, 1440, 1f, 0.5f, 1, out winW, out winH) && winW == 2560 && winH == 720, "full width, half height is still a window -> " + winW + "x" + winH);

        // The model's raster for a window is the window's own extent, scaled like a frame's.
        Check(Hooks.WorkExtent(819 * 2, 896, 0.5f, 2, out ww, out wh) && ww == 2 * 410 && wh == 448, "that window at 50% -> " + ww + "x" + wh);
        Check(Hooks.QuantiseWindow(0.337f) == 0.34f && Hooks.QuantiseWindow(0.1f) == 0.25f && Hooks.QuantiseWindow(3f) == 1f, "window size quantises and clamps");

        Check(Hooks.Quantise(0.4873f, 1f) == 0.49f, "quantise 0.4873 -> " + Hooks.Quantise(0.4873f, 1f));
        Check(Hooks.Quantise(0.1f, 1f) == 0.25f && Hooks.Quantise(1.7f, 1f) == 1f && Hooks.Quantise(1.7f, 2f) == 1.7f, "quantise clamps");
        Check(Hooks.Percent(0.5f) == "50%", "percent");
    }
}
