// VaM DLSS - Model Resolution: which logs are written to disk.
//
// Between them VaM DLSS and this plugin write a few thousand lines a session, and VaM DLSS has no
// setting that stops its own. They go to five places:
//
//   BepInEx\LogOutput.log and Unity's output_log.txt   every line either plugin logs (BepInEx
//                                                      writes each line to both)
//   BepInEx\plugins\VamDlssNr\vr_motion.log            VaM DLSS's motion statistics
//   ...\VamDlssNr\ngx\vdn.log, vdn_fg.log              VaM DLSS's native half
//   ...\VamDlssNr\ngx\nvngx.log, nvngx_dlss_*.log      NVIDIA's DLSS libraries
//
// Each has a switch here, all on as they come. The first two are taken out where BepInEx hands a
// line to the listeners that write files (DiskLogListener, UnityLogListener): lines of the two
// plugins below an error are let fall there, everybody else's go through, and the console (if it
// is open) still shows everything. An error is always written, and so is what the profiler was
// asked for by its button. vr_motion.log is VaM DLSS's own File.AppendAllText, skipped by a prefix
// (the line still goes to the BepInEx log, where its own switch decides). The files of the native
// half and of NVIDIA are written from native code: see native\vws_logs.h.

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    internal static class Logs
    {
        internal static ConfigEntry<bool> CfgOwn, CfgMod, CfgMotionFile, CfgNativeFiles, CfgNgxFiles;

        internal static string OwnName = "", ModName = "VaM DLSS";
        internal static string Problem = "", Status = "";
        internal static bool Hooked, MotionHooked;
        internal static int Listeners;

        // ---- the judgement: plain values (the host test runs it) --------------------------------

        internal const int Other = 0, Own = 1, Mod = 2;

        // Whether a line of `source` goes to the files. `own` and `mod` are the two switches.
        internal static bool Written(int source, bool error, string text, bool own, bool mod)
        {
            if (source == Own)
            {
                return own || error || (text != null && text.StartsWith("[vws] profile:"));
            }

            return source != Mod || mod || error;
        }

        internal static int SourceOf(string name, string ownName, string modName)
        {
            return name == null || name.Length == 0 ? Other : (name == ownName ? Own : (name == modName ? Mod : Other));
        }

        // What the native half is told: bit 0 VaM DLSS's own files are off, bit 1 NVIDIA's.
        internal static uint NativeMask(bool nativeFiles, bool ngxFiles)
        {
            return (nativeFiles ? 0u : 1u) | (ngxFiles ? 0u : 2u);
        }

        private static bool On(ConfigEntry<bool> entry)
        {
            return entry == null || entry.Value;
        }

        // ---- the hooks --------------------------------------------------------------------------

        private static FieldInfo F_modLog;
        private static MethodInfo M_modInfo;

        internal static void Apply(Harmony harmony)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;
            Type me = typeof(Logs);

            try
            {
                object[] named = typeof(VamDlssNrPlugin).GetCustomAttributes(typeof(BepInPlugin), false);

                if (named.Length != 0 && !string.IsNullOrEmpty(((BepInPlugin)named[0]).Name))
                {
                    ModName = ((BepInPlugin)named[0]).Name;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                Listeners = 0;

                foreach (string name in new[] { "BepInEx.Logging.DiskLogListener", "BepInEx.Logging.UnityLogListener" })
                {
                    Type listener = typeof(LogEventArgs).Assembly.GetType(name);
                    MethodInfo write = listener != null ? listener.GetMethod("LogEvent", any, null, new[] { typeof(object), typeof(LogEventArgs) }, null) : null;

                    if (write != null)
                    {
                        harmony.Patch(write, new HarmonyMethod(me.GetMethod("ListenerPrefix", pub)));
                        Listeners++;
                    }
                }

                Hooked = Listeners != 0;

                if (!Hooked)
                {
                    Problem = "logs: this BepInEx writes its files another way, and the two plugins' lines cannot be kept out of them";
                }
            }
            catch (Exception ex)
            {
                Hooked = false;
                Problem = "logs: the plugins' lines cannot be kept out of BepInEx's files (" + ex.GetType().Name + ": " + ex.Message + ")";
            }

            try
            {
                MethodInfo motion = typeof(NrCapture).GetMethod("VrLog", any, null, new[] { typeof(string) }, null);
                F_modLog = typeof(VamDlssNrPlugin).GetField("Log", any);
                M_modInfo = F_modLog != null ? F_modLog.FieldType.GetMethod("Info", any, null, new[] { typeof(string) }, null) : null;

                if (motion != null)
                {
                    harmony.Patch(motion, new HarmonyMethod(me.GetMethod("MotionPrefix", pub)));
                    MotionHooked = true;
                }
            }
            catch (Exception)
            {
                MotionHooked = false;
            }
        }

        public static bool ListenerPrefix(LogEventArgs __1)
        {
            try
            {
                bool own = On(CfgOwn), mod = On(CfgMod);

                if ((own && mod) || __1 == null || __1.Source == null)
                {
                    return true;
                }

                int source = SourceOf(__1.Source.SourceName, OwnName, ModName);
                return source == Other || Written(source, (__1.Level & (LogLevel.Error | LogLevel.Fatal)) != 0, __1.Data as string, own, mod);
            }
            catch (Exception)
            {
                return true;
            }
        }

        public static bool MotionPrefix(string __0)
        {
            if (On(CfgMotionFile))
            {
                return true;
            }

            try
            {
                object log = F_modLog != null ? F_modLog.GetValue(null) : null;

                if (log != null && M_modInfo != null)
                {
                    M_modInfo.Invoke(log, new object[] { __0 });
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        // ---- every second: the native half is told, and says what it has kept off the disk ------

        private static float _next;

        internal static void Tick(float now)
        {
            if (now < _next)
            {
                return;
            }

            _next = now + 1f;
            uint writes = 0, kilobytes = 0;

            if (Native.Loaded)
            {
                Native.Logs(NativeMask(On(CfgNativeFiles), On(CfgNgxFiles)), out writes, out kilobytes);
            }

            bool lines = !On(CfgOwn) || !On(CfgMod), files = !On(CfgMotionFile) || !On(CfgNativeFiles) || !On(CfgNgxFiles);

            Status = !lines && !files ? "" :
                "logs off: " + (lines ? (!On(CfgOwn) && !On(CfgMod) ? "both plugins' lines" : (!On(CfgOwn) ? "this plugin's lines" : "VaM DLSS's lines")) + (Hooked ? "" : " (NOT possible with this BepInEx)") : "") +
                (lines && files ? ", " : "") +
                (files ? (!On(CfgMotionFile) ? "vr_motion.log " : "") + (!On(CfgNativeFiles) ? "vdn.log vdn_fg.log " : "") + (!On(CfgNgxFiles) ? "nvngx*.log " : "") : "").TrimEnd() +
                (writes != 0 ? " -- " + writes + " native writes (" + kilobytes + " KB) not made" : "");
        }
    }
}
