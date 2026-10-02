// In-game self-test. NOT part of the release build: compiled only with -define:VWS_SELFTEST, and
// even then inert unless VaM is started with VWS_SELFTEST_DIR set (test\run-ingame.ps1 does both).
//
// It drives the real thing inside the real game -- VamDlssNr running Neural Rendering on VaM's own
// frame -- through every model resolution and mode, with the scene frozen so that successive
// screenshots differ only by what the network and the resolve did. Each step writes a screenshot and
// a line of numbers; test\analyze-ingame.py turns the screenshots into the comparisons that matter.

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

        internal static void Begin(WorkScalePlugin plugin)
        {
            string dir = Environment.GetEnvironmentVariable("VWS_SELFTEST_DIR");

            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            SelfTest test = plugin.gameObject.AddComponent<SelfTest>();
            test._dir = dir;
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

            Freeze(sc, true);
            Say("scene frozen");
            yield return StartCoroutine(Wait(2f));

            // label, NR on, model resolution, enlargement, debug view
            object[][] steps =
            {
                new object[] { "nr-off", false, 1f, 0, 0 },
                new object[] { "nr-100", true, 1f, 0, 0 },
                new object[] { "nr-050", true, 0.5f, 0, 0 },
                new object[] { "nr-025", true, 0.25f, 0, 0 },
                new object[] { "nr-075", true, 0.75f, 0, 0 },
                new object[] { "nr-050-classic", true, 0.5f, 1, 0 },
                new object[] { "nr-050-edit", true, 0.5f, 0, 2 },
                new object[] { "nr-050-frame-only", true, 0.5f, 0, 3 },
                new object[] { "nr-100-again", true, 1f, 0, 0 },
                new object[] { "nr-off-again", false, 1f, 0, 0 },
            };

            foreach (object[] s in steps)
            {
                string label = (string)s[0];
                nrEnabled.Value = (bool)s[1];
                Hooks.CfgScale.Value = (float)s[2];
                Hooks.CfgEnlargement.Value = (int)s[3];
                Hooks.CfgDebugView.Value = (int)s[4];

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

            // The panel, on the Neural Rendering tab, with the model running at 60%.
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
