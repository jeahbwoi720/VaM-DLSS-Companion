// In-game self-test. NOT part of the release build: compiled only with -define:VWS_SELFTEST, and
// even then inert unless VaM is started with VWS_SELFTEST_DIR set (test\run-ingame.ps1 does both).
//
// It drives the real thing inside the real game -- VamDlssNr running Neural Rendering on VaM's own
// frame -- through every model resolution and mode, with the scene frozen so that successive
// screenshots differ only by what the network and the resolve did. Each step writes a screenshot and
// a line of numbers; test\analyze-ingame.py turns the screenshots into the comparisons that matter.
//
// It then works the in-headset control panel the way a hand would: moves its controls and checks
// the settings followed, changes the settings and checks the controls followed.
//
// VWS_SELFTEST_QUICK=1 skips the resolution sweep and the timing probe (two steps instead of ten).

#if VWS_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    internal sealed class SelfTest : MonoBehaviour
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private string _dir;
        private StreamWriter _out;
        private int _step;
        private bool _quick;
        private int _checks, _failures;

        internal static void Begin(WorkScalePlugin plugin)
        {
            string dir = Environment.GetEnvironmentVariable("VWS_SELFTEST_DIR");

            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            SelfTest test = plugin.gameObject.AddComponent<SelfTest>();
            test._dir = dir;
            test._quick = Environment.GetEnvironmentVariable("VWS_SELFTEST_QUICK") == "1";
        }

        private void Say(string line)
        {
            Hooks.Info("selftest: " + line);

            if (_out != null)
            {
                _out.WriteLine(line);
                _out.Flush();
            }
        }

        private void Check(bool ok, string what)
        {
            _checks++;

            if (!ok)
            {
                _failures++;
            }

            Say((ok ? "  ok    " : "  FAIL  ") + what);
        }

        private static ConfigEntry<T> ModEntry<T>(string field)
        {
            FieldInfo f = typeof(VamDlssNrPlugin).GetField(field, Any);
            return f != null ? f.GetValue(null) as ConfigEntry<T> : null;
        }

        // The in-headset panel: found, filled, and wired both ways.
        private IEnumerator ControlPanelChecks()
        {
            // VaM loads session plugins itself, a little after it starts.
            float until = Time.realtimeSinceStartup + 25f;

            while (ControlPanel.PanelCount == 0 && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Say("control panel: " + ControlPanel.Describe());
            Check(ControlPanel.PanelCount > 0, "a host script was found and filled (VaMVrNrControl among the session plugins)");

            string package = Environment.GetEnvironmentVariable("VWS_SELFTEST_VAR");

            if (!string.IsNullOrEmpty(package))
            {
                MVR.FileManagement.VarPackage var = MVR.FileManagement.FileManager.GetPackage(package);
                Check(var != null && !var.invalid, "VaM registered the package " + package);
                Check(MVR.FileManagement.FileManager.FileExists(package + ":/Custom/Scripts/jeahbwoi720/VaMVrNrControl/VaMVrNrControl.cs"), "the package holds the script where its .cslist says");
            }

            if (ControlPanel.PanelCount == 0)
            {
                yield break;
            }

            Check(ControlPanel.Watching, "it was found by watching VaM's plugin loader, not by searching the scene");

            // For the record: what the search it replaces costs in this scene, each time it runs.
            {
                double best = double.MaxValue, worst = 0;
                int found = 0;

                for (int i = 0; i < 5; i++)
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    found = UnityEngine.Object.FindObjectsOfType<MVRScript>().Length;
                    watch.Stop();
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                    worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
                    yield return null;
                }

                Say("a scene search for plugin scripts takes " + best.ToString("0.00") + "-" + worst.ToString("0.00") + " ms here (" + found + " scripts, " +
                    UnityEngine.Object.FindObjectsOfType<Transform>().Length + " active transforms)");
            }

            // Open it the way a hand would: Edit mode (Session Plugins is not a Play-mode tab), the
            // main menu, that tab, then the plugin's own UI. The menu picks its starting tab on the
            // first frame it is shown, so the tab is asked for only after that.
            SuperController vam = SuperController.singleton;
            SuperController.GameMode modeWas = vam.gameMode;
            vam.gameMode = SuperController.GameMode.Edit;
            vam.ShowMainHUDAuto();
            vam.SetActiveUI("MainMenu");
            yield return null;
            yield return null;
            yield return null;
            vam.SetMainMenuTab("TabSessionPlugins");
            yield return null;
            yield return null;
            Say("panel UI: " + ControlPanel.ShowUi(true));
            yield return StartCoroutine(Wait(1f));
            Check(ControlPanel.UiShowing, "the panel's UI opens where it can be seen");

            ConfigEntry<float> intensity = ModEntry<float>("CfgIntensity");
            ConfigEntry<bool> nr = ModEntry<bool>("CfgEnabled");
            ConfigEntry<int> style = ModEntry<int>("CfgStyle");
            ConfigEntry<int> quality = ModEntry<int>("CfgSrQuality");
            FieldInfo toneField = typeof(VamDlssNrPlugin).GetField("CfgRegionTone", Any);
            ConfigEntry<float>[] regionTone = toneField != null ? toneField.GetValue(null) as ConfigEntry<float>[] : null;

            if (intensity == null || nr == null || style == null || quality == null || regionTone == null)
            {
                Check(false, "VamDlssNr's settings could be reached for the comparison");
                yield break;
            }

            float intensityWas = intensity.Value, scaleWas = Hooks.CfgScale.Value, toneWas = regionTone[1].Value;
            bool nrWas = nr.Value;
            int styleWas = style.Value, qualityWas = quality.Value;

            // A control moved -> the setting follows, at once.
            ControlPanel.Operate("NR intensity", 0.37f);
            Check(Mathf.Abs(intensity.Value - 0.37f) < 0.001f, "slider -> setting: NR intensity is " + intensity.Value.ToString("0.00"));

            ControlPanel.Operate("Neural Rendering", !nrWas);
            Check(nr.Value == !nrWas, "toggle -> setting: Neural Rendering is " + nr.Value);

            ControlPanel.Operate("NR style", "Cinematic");
            Check(style.Value == 2, "popup -> setting: NR style is " + style.Value);

            ControlPanel.Operate("DLSS quality", "Balanced");
            Check(quality.Value == 1, "popup -> setting: DLSS quality is " + quality.Value);

            ControlPanel.Operate("Region tone (x local tone)", 1.5f);
            Check(Mathf.Abs(regionTone[1].Value - 1.5f) < 0.001f, "region slider -> the Head region's tone is " + regionTone[1].Value.ToString("0.00"));

            // The model resolution is a setting at once, and the size in use once the hand is still.
            nr.Value = true;
            Hooks.CfgScale.Value = 1f;
            yield return StartCoroutine(Wait(1f));
            ControlPanel.Operate("NR model resolution (applies when let go)", 0.8f);
            ControlPanel.Operate("NR model resolution (applies when let go)", 0.6f);
            float during = Hooks.Applied;
            Check(Mathf.Abs(Hooks.CfgScale.Value - 0.6f) < 0.001f && during == 1f, "model resolution: setting " + Hooks.CfgScale.Value.ToString("0.00") + " while moving, in use " + during.ToString("0.00"));
            yield return StartCoroutine(Wait(1.5f));
            Check(Hooks.Applied == 0.6f, "model resolution: in use " + Hooks.Applied.ToString("0.00") + " once still -- " + Hooks.Describe());

            // What the hand did is on disk a moment after it stopped, in both settings files.
            yield return StartCoroutine(Wait(0.7f));
            Check(OnDisk(intensity, "0.37"), "saved: " + intensity.Definition.Key + " = 0.37 is in " + Path.GetFileName(intensity.ConfigFile.ConfigFilePath));
            Check(OnDisk(Hooks.CfgScale, "0.6"), "saved: " + Hooks.CfgScale.Definition.Key + " = 0.6 is in " + Path.GetFileName(Hooks.CfgScale.ConfigFile.ConfigFilePath));

            {
                string status = ControlPanel.StatusNow();
                Check(!string.IsNullOrEmpty(status) && status.Contains("<b>NR</b>: running") && status.Contains("model: ") && status.Contains("(60%)"),
                    "the status box is being written, and says what the network runs at");
                Say("status box:\n" + status);
            }

            // A setting changed elsewhere -> the control follows.
            intensity.Value = 0.81f;
            style.Value = 1;
            nr.Value = nrWas;
            yield return StartCoroutine(Wait(0.6f));
            Check(ControlPanel.Read("NR intensity") == "0.81", "setting -> slider: NR intensity reads " + ControlPanel.Read("NR intensity"));
            Check(ControlPanel.Read("NR style") == "Natural", "setting -> popup: NR style reads " + ControlPanel.Read("NR style"));
            Check(ControlPanel.Read("Neural Rendering") == nrWas.ToString(), "setting -> toggle: Neural Rendering reads " + ControlPanel.Read("Neural Rendering"));

            // Closed, the panel is left alone; opened again, it has caught up before it is looked at.
            ControlPanel.ShowUi(false);
            intensity.Value = 0.55f;
            yield return StartCoroutine(Wait(0.6f));
            string whileClosed = ControlPanel.Read("NR intensity");
            ControlPanel.ShowUi(true);
            yield return StartCoroutine(Wait(0.5f));
            Check(whileClosed == "0.81" && ControlPanel.Read("NR intensity") == "0.55",
                "closed, the slider is left at " + whileClosed + "; opened again it reads " + ControlPanel.Read("NR intensity"));

            // Back as it was. (Set from here these are not the hand's changes, so nothing saves them;
            // closing the F10 panel further down does, and the runner restores both files anyway.)
            intensity.Value = intensityWas;
            style.Value = styleWas;
            quality.Value = qualityWas;
            regionTone[1].Value = toneWas;
            nr.Value = nrWas;
            Hooks.CfgScale.Value = scaleWas;
            yield return StartCoroutine(Wait(2.5f));

            // The panel as it looks with the network running small and everything else as the
            // user has it: for the eye, and for the README.
            nr.Value = true;
            Hooks.CfgScale.Value = 0.6f;
            yield return StartCoroutine(Wait(3f));
            string shot = Path.Combine(_dir, "session-panel.png");
            ScreenCapture.CaptureScreenshot(shot);
            yield return null;
            yield return null;
            yield return StartCoroutine(Wait(0.5f));
            Say("session panel: shot=" + File.Exists(shot));
            nr.Value = nrWas;
            Hooks.CfgScale.Value = scaleWas;
            yield return StartCoroutine(Wait(1f));

            ControlPanel.ShowUi(false);
            vam.HideMainHUD();
            vam.gameMode = modeWas;
        }

        // What the sweep's numbers depend on besides the model resolution is held still for its
        // length: one pass, no DLSS upscaling, no frame generation, no mask, default strength and
        // style. Without that the same step measures something else on another day.
        private readonly List<Action> _unpin = new List<Action>();

        private void Pin<T>(string field, T value)
        {
            ConfigEntry<T> entry = ModEntry<T>(field);

            if (entry == null)
            {
                Say("pin: this VamDlssNr has no " + field);
                return;
            }

            T was = entry.Value;
            entry.Value = value;
            _unpin.Add(delegate { entry.Value = was; });
        }

        private void Unpin()
        {
            for (int i = _unpin.Count - 1; i >= 0; i--)
            {
                _unpin[i]();
            }

            _unpin.Clear();
        }

        // Whether the settings file this entry lives in holds this value for it.
        private static bool OnDisk(ConfigEntryBase entry, string value)
        {
            try
            {
                foreach (string line in File.ReadAllLines(entry.ConfigFile.ConfigFilePath))
                {
                    if (line.Trim() == entry.Definition.Key + " = " + value)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static object SuperControllerSingleton()
        {
            Type t = Type.GetType("SuperController, Assembly-CSharp");

            if (t == null)
            {
                return null;
            }

            PropertyInfo p = t.GetProperty("singleton", Any);
            return p != null ? p.GetValue(null, null) : null;
        }

        private static bool VamLoading(object sc)
        {
            PropertyInfo p = sc.GetType().GetProperty("isLoading", Any);
            return p != null && (bool)p.GetValue(sc, null);
        }

        private static void Freeze(object sc, bool on)
        {
            MethodInfo m = sc.GetType().GetMethod("SetFreezeAnimation", Any, null, new[] { typeof(bool) }, null);

            if (m != null)
            {
                m.Invoke(sc, new object[] { on });
            }
        }

        private static string ModString(Type type, string field)
        {
            FieldInfo f = type.GetField(field, Any);
            return f != null ? f.GetValue(null) as string : "?";
        }

        private IEnumerator Wait(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;

            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
        }

        private IEnumerator Start()
        {
            Directory.CreateDirectory(_dir);
            _out = new StreamWriter(Path.Combine(_dir, "selftest.log"), false, Encoding.UTF8);
            Say("begin -- waiting for VaM to finish starting");

            // VaM is up when its controller exists and has not been loading for a while.
            object sc = null;
            float quietSince = -1f;

            while (true)
            {
                yield return null;

                if (sc == null || sc.Equals(null))
                {
                    sc = SuperControllerSingleton();
                    quietSince = -1f;
                    continue;
                }

                if (VamLoading(sc))
                {
                    quietSince = -1f;
                    continue;
                }

                if (quietSince < 0f)
                {
                    quietSince = Time.realtimeSinceStartup;
                }

                if (Time.realtimeSinceStartup - quietSince > 12f && Time.realtimeSinceStartup > 25f)
                {
                    break;
                }

                if (Time.realtimeSinceStartup > 600f)
                {
                    Say("gave up waiting for VaM to settle");
                    break;
                }
            }

            Say("environment: " + Screen.width + "x" + Screen.height + " fullscreen=" + Screen.fullScreen + " vsync=" + QualitySettings.vSyncCount +
                " targetFps=" + Application.targetFrameRate + " gpu=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceType +
                " multithreaded=" + SystemInfo.graphicsMultiThreaded + " colorSpace=" + QualitySettings.activeColorSpace);
            Say("plugin: hooked=" + Hooks.Hooked + " native=" + Native.Loaded + " composeSites=" + Hooks.ComposeSites + " problem='" + Hooks.Problem + "' panel='" + Hooks.PanelProblem + "'");

            // The runner leaves a settings file as an earlier build would have -- another owner
            // prefix, a value that is not the default -- for this build to take over at startup.
            string expected = Environment.GetEnvironmentVariable("VWS_SELFTEST_EXPECT_SCALE");

            if (!string.IsNullOrEmpty(expected) && Hooks.CfgScale != null)
            {
                float want = float.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
                string path = Hooks.CfgScale.ConfigFile.ConfigFilePath;
                int files = Directory.GetFiles(Path.GetDirectoryName(path), "*.vamdlssnr.workscale.cfg").Length;
                Check(Mathf.Abs(Hooks.CfgScale.Value - want) < 0.001f && Path.GetFileName(path) == WorkScalePlugin.Guid + ".cfg" && files == 1,
                    "an earlier build's settings were taken over: ModelResolution " + Hooks.CfgScale.Value.ToString("0.00") + " in " + Path.GetFileName(path) + " (" + files + " such file)");
            }

            FieldInfo cfgEnabledField = typeof(VamDlssNrPlugin).GetField("CfgEnabled", Any);
            ConfigEntry<bool> nrEnabled = cfgEnabledField != null ? cfgEnabledField.GetValue(null) as ConfigEntry<bool> : null;

            if (nrEnabled == null || Hooks.CfgScale == null)
            {
                Say("cannot reach VamDlssNr's Enabled setting -- aborting");
                Finish(false);
                yield break;
            }

            bool nrWas = nrEnabled.Value;
            float scaleWas = Hooks.CfgScale.Value;
            int enlargementWas = Hooks.CfgEnlargement.Value;
            float windowWas = Hooks.CfgWindow.Value;
            bool windowMonitorWas = Hooks.CfgWindowMonitor.Value;
            float monitorWidthWas = Hooks.CfgMonitorWidth.Value, monitorHeightWas = Hooks.CfgMonitorHeight.Value;
            bool monitorFollowWas = Hooks.CfgMonitorFollow.Value;

            // This run is on the monitor, whose window is its own: on for the run, in the middle
            // of the screen (not after the person) so that every run frames the same thing.
            Hooks.CfgWindowMonitor.Value = true;
            Hooks.CfgMonitorFollow.Value = false;
            Say("focus window: " + (Hooks.WindowHooked ? "available" : "NOT available -- " + Hooks.WindowProblem) + " (guide copies " + Hooks.GuideSites + ", SetParams calls " + Hooks.ParamSites + ")");

            Pin("CfgSrEnabled", false);
            Pin("CfgFrameGen", false);
            Pin("CfgPasses", 1);
            Pin("CfgNrBeforeSr", false);
            Pin("CfgControlMask", false);
            Pin("CfgIntensity", 1f);
            Pin("CfgStyle", 0);

            Freeze(sc, true);
            Say("scene frozen; measuring Neural Rendering alone -- one pass, no DLSS upscaling, no frame generation, no mask");
            yield return StartCoroutine(Wait(2f));

            // label, NR on, model resolution, enlargement, debug view, focus window
            object[][] steps =
            {
                new object[] { "nr-off", false, 1f, 0, 0, 1f },
                new object[] { "nr-100", true, 1f, 0, 0, 1f },
                new object[] { "nr-050", true, 0.5f, 0, 0, 1f },
                new object[] { "nr-025", true, 0.25f, 0, 0, 1f },
                new object[] { "nr-075", true, 0.75f, 0, 0, 1f },
                new object[] { "nr-050-classic", true, 0.5f, 1, 0, 1f },
                new object[] { "nr-050-edit", true, 0.5f, 0, 2, 1f },
                new object[] { "nr-050-frame-only", true, 0.5f, 0, 3, 1f },
                new object[] { "nr-window-050", true, 1f, 0, 0, 0.5f },
                new object[] { "nr-window-050-edit", true, 1f, 0, 2, 0.5f },
                new object[] { "nr-window-050-at-050", true, 0.5f, 0, 0, 0.5f },
                new object[] { "nr-100-again", true, 1f, 0, 0, 1f },
                new object[] { "nr-off-again", false, 1f, 0, 0, 1f },
            };

            if (_quick)
            {
                steps = new[] { steps[0], steps[2], steps[8], steps[9] };
            }

            foreach (object[] s in steps)
            {
                string label = (string)s[0];
                nrEnabled.Value = (bool)s[1];
                Hooks.CfgScale.Value = (float)s[2];
                Hooks.CfgEnlargement.Value = (int)s[3];
                Hooks.CfgDebugView.Value = (int)s[4];
                Hooks.CfgMonitorWidth.Value = Hooks.CfgMonitorHeight.Value = (float)s[5];

                // The rebuild, then the network's temporal history settling on a still scene.
                yield return StartCoroutine(Wait(3f));

                int frames0 = Time.frameCount;
                float t0 = Time.realtimeSinceStartup;
                yield return StartCoroutine(Wait(2.5f));
                float fps = (Time.frameCount - frames0) / (Time.realtimeSinceStartup - t0);

                string shot = Path.Combine(_dir, string.Format("step-{0:00}-{1}.png", _step, label));
                ScreenCapture.CaptureScreenshot(shot);
                yield return null;
                yield return null;
                yield return StartCoroutine(Wait(0.5f));

                Say(string.Format("step {0:00} {1}: fps={2:0.0} applied={3:0.00} {4} | {5} | {6} | shot={7}", _step, label, fps, Hooks.Applied,
                    Hooks.Describe(), ModString(typeof(NrCapture), "ChainStatus"), ModString(typeof(NrCapture), "NrGate"), File.Exists(shot)));
                _step++;
            }

            if (!_quick)
            {
            // Timing probe. The resolve pairs the model's answer with the input it was an answer TO,
            // which is only right if VamDlssNr's evaluate has written this frame's answer by the
            // time the resolve runs. So: feed the model a flat grey that changes level at a known
            // frame, show the model's own picture (classic), and read the screen every frame. An
            // answer in step with the input changes on the very frame the level does.
            nrEnabled.Value = true;
            Hooks.CfgScale.Value = 0.5f;
            Hooks.CfgEnlargement.Value = 1;
            Hooks.CfgDebugView.Value = 0;
            Hooks.TestFlat = 0.2f;
            yield return StartCoroutine(Wait(3f));

            {
                Texture2D probe = new Texture2D(8, 8, TextureFormat.RGB24, false);
                int px = Mathf.RoundToInt(Screen.width * 0.04f);
                int py = Mathf.RoundToInt(Screen.height * 0.5f);
                StringBuilder trace = new StringBuilder();
                yield return new WaitForEndOfFrame();

                for (int i = 0; i < 44; i++)
                {
                    // Ten frames low, ten high, ten low, then alternating every frame.
                    float level = i < 10 ? 0.2f : (i < 20 ? 0.7f : (i < 30 ? 0.2f : ((i & 1) == 0 ? 0.7f : 0.2f)));
                    Hooks.TestFlat = level;
                    yield return new WaitForEndOfFrame();

                    RenderTexture.active = null;
                    probe.ReadPixels(new Rect(px, py, 8, 8), 0, 0, false);
                    Color[] c = probe.GetPixels();
                    float sum = 0f;

                    for (int k = 0; k < c.Length; k++)
                    {
                        sum += c[k].g;
                    }

                    trace.Append(i).Append(':').Append(level.ToString("0.0")).Append("->").Append((sum / c.Length * 255f).ToString("0")).Append(' ');
                }

                Hooks.TestFlat = -1f;
                UnityEngine.Object.Destroy(probe);
                Say("timing probe (frame:input level->screen value): " + trace);
            }

            Hooks.CfgEnlargement.Value = 0;
            yield return StartCoroutine(Wait(1f));

            // The slider commits on release: while VamDlssNr's panel says it is held, the applied
            // value must not follow the setting.
            FieldInfo held = typeof(VamDlssNrPanel).GetField("_heldCfg", Any);

            if (held != null)
            {
                Hooks.CfgScale.Value = 1f;
                yield return StartCoroutine(Wait(0.5f));
                held.SetValue(null, Hooks.CfgScale);
                Hooks.CfgScale.Value = 0.6f;
                yield return StartCoroutine(Wait(0.5f));
                float whileHeld = Hooks.Applied;
                held.SetValue(null, null);
                yield return StartCoroutine(Wait(0.5f));
                Say("slider hold: applied " + whileHeld.ToString("0.00") + " while held, " + Hooks.Applied.ToString("0.00") + " after release (want 1.00 then 0.60)");
            }
            else
            {
                Say("slider hold: VamDlssNrPanel._heldCfg not found");
            }
            }

            Hooks.CfgWindow.Value = windowWas;
            Hooks.CfgWindowMonitor.Value = windowMonitorWas;
            Hooks.CfgMonitorWidth.Value = monitorWidthWas;
            Hooks.CfgMonitorHeight.Value = monitorHeightWas;
            Hooks.CfgMonitorFollow.Value = monitorFollowWas;
            Unpin();
            yield return StartCoroutine(ControlPanelChecks());

            // The panel, on the Neural Rendering tab, with the model running at 60%.
            Hooks.CfgScale.Value = 0.6f;
            nrEnabled.Value = true;
            yield return StartCoroutine(Wait(3f));

            FieldInfo panelField = typeof(VamDlssNrPlugin).GetField("Panel", Any);
            VamDlssNrPanel panel = panelField != null ? panelField.GetValue(null) as VamDlssNrPanel : null;

            if (panel != null)
            {
                PropertyInfo visible = typeof(VamDlssNrPanel).GetProperty("Visible", Any);
                FieldInfo tab = typeof(VamDlssNrPanel).GetField("_tab", Any);
                MethodInfo refreshTabs = typeof(VamDlssNrPanel).GetMethod("RefreshTabs", Any);
                MethodInfo renderTab = typeof(VamDlssNrPanel).GetMethod("RenderTab", Any);

                visible.SetValue(panel, true, null);
                tab.SetValue(panel, "nr");

                if (refreshTabs != null)
                {
                    refreshTabs.Invoke(panel, null);
                }

                renderTab.Invoke(panel, null);
                yield return StartCoroutine(Wait(1.5f));

                string shot = Path.Combine(_dir, string.Format("step-{0:00}-panel.png", _step));
                ScreenCapture.CaptureScreenshot(shot);
                yield return null;
                yield return null;
                yield return StartCoroutine(Wait(0.5f));

                // What the tab's rows are, in order, so the placement can be checked without eyes.
                FieldInfo rowsField = typeof(VamDlssNrPanel).GetField("_rows", Any);
                IList rows = rowsField != null ? rowsField.GetValue(panel) as IList : null;
                StringBuilder names = new StringBuilder();

                if (rows != null)
                {
                    foreach (object row in rows)
                    {
                        RectTransform rt = row.GetType().GetField("Rt", Any).GetValue(row) as RectTransform;
                        names.Append(rt != null ? rt.name : "?").Append(rt != null && !rt.gameObject.activeSelf ? "(hidden)" : "").Append(", ");
                    }
                }

                Say("panel rows: " + names);
                Say("panel: shot=" + File.Exists(shot) + " problem='" + Hooks.PanelProblem + "' " + Hooks.Describe());

                // Everything back as it was BEFORE the panel closes, because closing it saves.
                nrEnabled.Value = nrWas;
                Hooks.CfgScale.Value = scaleWas;
                Hooks.CfgEnlargement.Value = enlargementWas;
                Hooks.CfgDebugView.Value = 0;
                yield return StartCoroutine(Wait(0.5f));
                visible.SetValue(panel, false, null);
            }
            else
            {
                Say("panel: VamDlssNrPlugin.Panel not found");
                nrEnabled.Value = nrWas;
                Hooks.CfgScale.Value = scaleWas;
                Hooks.CfgEnlargement.Value = enlargementWas;
                Hooks.CfgDebugView.Value = 0;
            }

            Freeze(sc, false);
            yield return StartCoroutine(Wait(3f));
            Say("after restore: applied=" + Hooks.Applied.ToString("0.00") + " " + Hooks.Describe() + " problem='" + Hooks.Problem + "'");
            Finish(true);
        }

        private void Finish(bool ok)
        {
            Say("verdict: " + _checks + " checks, " + _failures + " failure(s)");
            Say(ok ? "done" : "aborted");

            if (_out != null)
            {
                _out.Dispose();
                _out = null;
            }

            Application.Quit();
        }
    }
}
#endif
