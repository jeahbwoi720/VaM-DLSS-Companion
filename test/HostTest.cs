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
