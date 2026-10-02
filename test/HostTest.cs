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
        Check(Hooks.Hooked, "the frame hook is in place");
        Check(Hooks.ComposeSites == 4, "expected 4 reads of _output in 1.0.3's Compose, found " + Hooks.ComposeSites);
        Check(Hooks.PanelProblem.Length == 0, "panel after patching: " + Hooks.PanelProblem);

        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        string[][] expected =
        {
            new[] { "NrCapture", "Compose", "transpiler" },
            new[] { "NrCapture", "RunNeuralRendering", "prefix+postfix" },
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
                else ok = info.Prefixes.Count == 1 && info.Postfixes.Count == 1;
            }

            Check(ok, e[0] + "." + e[1] + " carries our " + e[2]);
        }

        // The accessor the transpiler put into Compose, on the path it takes when the model ran at
        // full size: no view for this capture, so the field itself comes back.
        NrCapture capture = (NrCapture)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(NrCapture));
        Check(Hooks.OutputFor(capture) == null, "OutputFor on a bare capture returns its (null) field");

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
        int id = Native.PushPass(true, 3, 2, 0.5f, 1);
        Check((id & 0x7FFF0000) == 0x57530000, "push returned event id 0x" + id.ToString("X8"));

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

        Check(Hooks.Quantise(0.4873f, 1f) == 0.49f, "quantise 0.4873 -> " + Hooks.Quantise(0.4873f, 1f));
        Check(Hooks.Quantise(0.1f, 1f) == 0.25f && Hooks.Quantise(1.7f, 1f) == 1f && Hooks.Quantise(1.7f, 2f) == 1.7f, "quantise clamps");
        Check(Hooks.Percent(0.5f) == "50%", "percent");
    }
}
