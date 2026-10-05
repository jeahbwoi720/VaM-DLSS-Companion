// VaM DLSS - Model Resolution: the part that has to run before the game has a window.
//
// A BepInEx preloader patcher (it goes in BepInEx\patchers, not in plugins): it patches no assembly,
// it is only a way to run at the moment the preloader does -- while Unity is starting its scripting
// runtime, before it creates its graphics device and its window's swap chain. The plugin itself is
// loaded long after that, when the swap chain exists and can no longer be made any other way.
//
// All it does is load the plugin's native half and arm its flip-model presentation (see
// native\vws_flip.h), in monitor mode and only if the plugin's settings say so.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace VamDlssNrWorkScale.Early
{
    public static class FlipModelPatcher
    {
        private const string PluginGuid = "jeahbwoi720.vamdlssnr.workscale";

        public static IEnumerable<string> TargetDLLs
        {
            get { return new string[0]; }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ArmFn();

        public static void Initialize()
        {
            ManualLogSource log = Logger.CreateLogSource("VamDlssNrWorkScale.Early");

            try
            {
                if (!MonitorMode())
                {
                    log.LogInfo("flip-model window: not in monitor mode (-vrmode None), nothing to do");
                    return;
                }

                if (!Wanted())
                {
                    log.LogInfo("flip-model window: not switched on ([Presentation] FlipModel)");
                    return;
                }

                string native = Path.Combine(Path.Combine(Paths.PluginPath, "VamDlssNrWorkScale"), "VamDlssNrWorkScaleNative.dll");

                if (!File.Exists(native))
                {
                    log.LogWarning("flip-model window: " + native + " is missing");
                    return;
                }

                IntPtr module = LoadLibraryW(native);

                if (module == IntPtr.Zero)
                {
                    log.LogWarning("flip-model window: the native half would not load (win32 error " + Marshal.GetLastWin32Error() + ")");
                    return;
                }

                IntPtr arm = GetProcAddress(module, "vws_flip_arm");

                if (arm == IntPtr.Zero)
                {
                    log.LogWarning("flip-model window: the native half is an older build without it");
                    return;
                }

                int armed = ((ArmFn)Marshal.GetDelegateForFunctionPointer(arm, typeof(ArmFn)))();
                log.LogInfo(armed != 0 ? "flip-model window: armed" : "flip-model window: could not be armed (the plugin's log says why)");
            }
            catch (Exception ex)
            {
                log.LogWarning("flip-model window: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // VaM's monitor mode is started with "-vrmode None". In a headset the window is only a
        // mirror, and is left as it is.
        private static bool MonitorMode()
        {
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "-vrmode", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(args[i + 1], "None", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        // The plugin's own settings file, read as text: the plugin is not loaded yet, and a second
        // owner of the file would write over what the first one saves. Off unless it says on.
        private static bool Wanted()
        {
            string path = Path.Combine(Paths.ConfigPath, PluginGuid + ".cfg");

            if (!File.Exists(path))
            {
                return false;
            }

            bool inSection = false;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();

                if (line.StartsWith("["))
                {
                    inSection = string.Equals(line, "[Presentation]", StringComparison.OrdinalIgnoreCase);
                }
                else if (inSection && line.StartsWith("FlipModel", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    return eq >= 0 && string.Equals(line.Substring(eq + 1).Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
    }
}
