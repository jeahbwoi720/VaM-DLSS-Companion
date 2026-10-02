// The in-headset control panel.
//
// VaM DLSS's own panel (F10) is a desktop overlay: in a headset it lives on the mirror window and
// takes the mouse. What can be used in VR is a session plugin's custom UI -- VaM's own sliders,
// toggles and popups, worked with the controllers' pointer.
//
// A session plugin cannot reach a BepInEx plugin's settings: VaM compiles scripts in a sandbox with
// no reflection and no file access. So the script is only the panel's frame
// (vam\Custom\Scripts\jeahbwoi720\VaMVrNrControl\VaMVrNrControl.cs: a status box and a marker),
// and the controls are put into it from here, where everything is reachable. Each one is bound to
// the ConfigEntry the F10 panel edits, both ways: moving a slider here sets the entry; an entry
// changed anywhere else (the F10 panel, a shortcut, the .cfg) moves the slider.
//
// The controls are not registered with VaM as storables, on purpose. The settings already have a
// home -- the two .cfg files -- and a second copy in a session-plugin preset would overwrite them
// with whatever the preset last held on every launch.
//
// None of this may cost a frame in a headset. The script is recognised at the moment VaM creates
// it (a hook on the one method that creates every plugin script) rather than by searching a scene
// that can hold a few hundred thousand objects, and a panel nobody has open is not kept up to date.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    internal static class ControlPanel
    {
        // A public constant of this name on a session script makes it a host for the panel.
        private const string Marker = "VamDlssControlHost";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private abstract class Binding
        {
            internal string Label;

            // Config -> control, when the entry was changed somewhere else.
            internal abstract void Pull(bool force);
        }

        private sealed class FloatBinding : Binding
        {
            internal Func<ConfigEntry<float>> Entry;
            internal JSONStorableFloat Storable;
            private float _last = float.NaN;

            internal void Push(float value)
            {
                ConfigEntry<float> entry = Entry();

                if (entry == null)
                {
                    return;
                }

                entry.Value = value;
                _last = entry.Value;
                Changed();
            }

            internal override void Pull(bool force)
            {
                ConfigEntry<float> entry = Entry();

                if (entry == null)
                {
                    return;
                }

                float value = entry.Value;

                if (force || value != _last)
                {
                    _last = value;
                    Storable.valNoCallback = value;
                }
            }
        }

        private sealed class BoolBinding : Binding
        {
            internal ConfigEntry<bool> Entry;
            internal JSONStorableBool Storable;
            private bool _last;
            private bool _known;

            internal void Push(bool value)
            {
                Entry.Value = value;
                _last = Entry.Value;
                _known = true;
                Changed();
            }

            internal override void Pull(bool force)
            {
                bool value = Entry.Value;

                if (force || !_known || value != _last)
                {
                    _last = value;
                    _known = true;
                    Storable.valNoCallback = value;
                }
            }
        }

        private sealed class ChoiceBinding : Binding
        {
            internal ConfigEntry<int> Entry;
            internal JSONStorableStringChooser Storable;
            internal string[] Names;
            internal int[] Values;
            private int _last = int.MinValue;

            internal string NameOf(int value)
            {
                for (int i = 0; i < Values.Length; i++)
                {
                    if (Values[i] == value)
                    {
                        return Names[i];
                    }
                }

                // A value the list does not offer (a multiplier the card went past, say): the
                // nearest one below it, so the control still says something true-ish.
                int best = 0;

                for (int i = 0; i < Values.Length; i++)
                {
                    if (Values[i] <= value)
                    {
                        best = i;
                    }
                }

                return Names[best];
            }

            internal void Push(string name)
            {
                for (int i = 0; i < Names.Length; i++)
                {
                    if (Names[i] == name)
                    {
                        Entry.Value = Values[i];
                        _last = Entry.Value;
                        Changed();
                        return;
                    }
                }
            }

            internal override void Pull(bool force)
            {
                int value = Entry.Value;

                if (force || value != _last)
                {
                    _last = value;
                    Storable.valNoCallback = NameOf(value);
                }
            }
        }

        private sealed class Panel
        {
            internal MVRScript Script;
            internal JSONStorableString Status;
            internal readonly List<Binding> Bindings = new List<Binding>();
            internal readonly List<Binding> RegionBindings = new List<Binding>();
            internal int Region = 1;
            internal bool Showing;
        }

        // VamDlssNr's six mask regions, in the order its own panel lists them.
        private static readonly string[] RegionNames = { "Head", "Torso", "Limbs", "Genitals", "Clothing", "Scene (everything else)" };
        private static readonly int[] RegionIndex = { 1, 2, 3, 4, 5, 0 };

        private static readonly List<Panel> _panels = new List<Panel>();
        private static readonly HashSet<int> _refused = new HashSet<int>();

        // Set when Tick has thrown: the panel is then left alone for the rest of the session.
        internal static bool Off;

        private static bool _watching, _scanned;
        private static float _nextScan, _nextPull, _nextStatus;
        private static float _saveAt = -1f;
        private static bool _resolved;
        private static readonly List<string> _missing = new List<string>();

        private static MethodInfo M_statusLine;
        private static FieldInfo F_chain, F_upscale, F_fault, F_pace, F_modPanel;
        private static PropertyInfo P_panelVisible;

        internal static int PanelCount
        {
            get { return _panels.Count; }
        }

        // ---- reaching VamDlssNr's settings ---------------------------------------------------

        private static ConfigEntry<T> Mod<T>(string field)
        {
            FieldInfo f = typeof(VamDlssNrPlugin).GetField(field, Any);
            ConfigEntry<T> entry = f != null ? f.GetValue(null) as ConfigEntry<T> : null;

            if (entry == null && !_missing.Contains(field))
            {
                _missing.Add(field);
            }

            return entry;
        }

        private static ConfigEntry<float>[] ModArray(string field)
        {
            FieldInfo f = typeof(VamDlssNrPlugin).GetField(field, Any);
            ConfigEntry<float>[] entries = f != null ? f.GetValue(null) as ConfigEntry<float>[] : null;

            if (entries == null && !_missing.Contains(field))
            {
                _missing.Add(field);
            }

            return entries;
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            Type plugin = typeof(VamDlssNrPlugin);
            Type capture = typeof(NrCapture);
            Type spike = capture.Assembly.GetType("VamDlssNr.SpikeWatch");

            M_statusLine = plugin.GetMethod("StatusLine", Any, null, Type.EmptyTypes, null);
            F_chain = capture.GetField("ChainStatus", Any);
            F_upscale = capture.GetField("UpscaleStatus", Any);
            F_fault = capture.GetField("ComposeFault", Any);
            F_pace = spike != null ? spike.GetField("Pace", Any) : null;
            F_modPanel = plugin.GetField("Panel", Any);
            P_panelVisible = typeof(VamDlssNrPanel).GetProperty("Visible", Any);
        }

        private static string Text(FieldInfo field)
        {
            try
            {
                return field != null ? field.GetValue(null) as string : null;
            }
            catch
            {
                return null;
            }
        }

        // ---- what a change sets off ------------------------------------------------------------

        // Saving is put off until the hand has been still for a moment: a slider reports every
        // step of a drag, and each save writes both .cfg files.
        private static void Changed()
        {
            _saveAt = Time.unscaledTime + 1.5f;
        }

        private static void SaveNow()
        {
            Hooks.SaveAll();

            // If the F10 panel is open on the mirror it is showing the old values; have it redraw.
            try
            {
                VamDlssNrPanel panel = F_modPanel != null ? F_modPanel.GetValue(null) as VamDlssNrPanel : null;

                if (panel != null && P_panelVisible != null && (bool)P_panelVisible.GetValue(panel, null))
                {
                    Hooks.RedrawModPanel(panel);
                }
            }
            catch (Exception ex)
            {
                Hooks.Warn("could not refresh VamDlssNr's panel: " + ex.Message);
            }
        }

        // ---- building the panel ----------------------------------------------------------------

        private static void Range(ConfigEntry<float> entry, out float lo, out float hi)
        {
            AcceptableValueRange<float> range = entry.Description != null ? entry.Description.AcceptableValues as AcceptableValueRange<float> : null;
            lo = range != null ? range.MinValue : 0f;
            hi = range != null ? range.MaxValue : 1f;
        }

        private static FloatBinding Slider(Panel p, bool right, string label, Func<ConfigEntry<float>> entry, string format)
        {
            ConfigEntry<float> first = entry();

            if (first == null)
            {
                return null;
            }

            float lo, hi;
            Range(first, out lo, out hi);

            FloatBinding b = new FloatBinding();
            b.Label = label;
            b.Entry = entry;
            b.Storable = new JSONStorableFloat(label, first.Value, new JSONStorableFloat.SetFloatCallback(b.Push), lo, hi, true, true);
            b.Storable.defaultVal = (float)first.DefaultValue;

            UIDynamicSlider ui = p.Script.CreateSlider(b.Storable, right);

            if (ui != null)
            {
                ui.label = label;
                ui.valueFormat = format;
                ui.rangeAdjustEnabled = false;
            }

            p.Bindings.Add(b);
            return b;
        }

        private static FloatBinding Slider(Panel p, bool right, string label, ConfigEntry<float> entry, string format)
        {
            return entry != null ? Slider(p, right, label, delegate { return entry; }, format) : null;
        }

        private static void Toggle(Panel p, bool right, string label, ConfigEntry<bool> entry)
        {
            if (entry == null)
            {
                return;
            }

            BoolBinding b = new BoolBinding();
            b.Label = label;
            b.Entry = entry;
            b.Storable = new JSONStorableBool(label, entry.Value, new JSONStorableBool.SetBoolCallback(b.Push));
            b.Storable.defaultVal = (bool)entry.DefaultValue;

            UIDynamicToggle ui = p.Script.CreateToggle(b.Storable, right);

            if (ui != null)
            {
                ui.label = label;
            }

            p.Bindings.Add(b);
        }

        private static void Choice(Panel p, bool right, string label, ConfigEntry<int> entry, string[] names, int[] values)
        {
            if (entry == null)
            {
                return;
            }

            ChoiceBinding b = new ChoiceBinding();
            b.Label = label;
            b.Entry = entry;
            b.Names = names;
            b.Values = values;
            b.Storable = new JSONStorableStringChooser(label, new List<string>(names), b.NameOf(entry.Value), label, new JSONStorableStringChooser.SetStringCallback(b.Push));
            b.Storable.defaultVal = b.NameOf((int)entry.DefaultValue);

            UIDynamicPopup ui = p.Script.CreateScrollablePopup(b.Storable, right);

            if (ui != null)
            {
                ui.label = label;
            }

            p.Bindings.Add(b);
        }

        private static void Button(Panel p, bool right, string label, Action action)
        {
            UIDynamicButton ui = p.Script.CreateButton(label, right);

            if (ui != null && ui.button != null)
            {
                ui.button.onClick.AddListener(delegate
                {
                    try
                    {
                        action();
                        Changed();

                        foreach (Binding b in p.Bindings)
                        {
                            b.Pull(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Hooks.Error("control panel: '" + label + "' failed: " + ex.Message);
                    }
                });
            }
        }

        private static void ToDefault<T>(ConfigEntry<T> entry)
        {
            if (entry != null)
            {
                entry.Value = (T)entry.DefaultValue;
            }
        }

        private static void Build(Panel p)
        {
            const bool Left = false, Right = true;

            // ---- left: Neural Rendering ----
            ConfigEntry<bool> nr = Mod<bool>("CfgEnabled");
            ConfigEntry<float> intensity = Mod<float>("CfgIntensity");
            ConfigEntry<float> tone = Mod<float>("CfgLocalTone");
            ConfigEntry<float> structure = Mod<float>("CfgLocalStructure");

            Toggle(p, Left, "Neural Rendering", nr);
            Slider(p, Left, "NR model resolution (applies when let go)", Hooks.CfgScale, "F2");

            // The focus window: only where its hooks went in, or the slider would move nothing.
            if (Hooks.WindowHooked)
            {
                Slider(p, Left, "NR window (1.00 = whole view)", Hooks.CfgWindow, "F2");
                Slider(p, Left, "NR window edge softness", Hooks.CfgWindowFeather, "F2");
                Toggle(p, Left, "NR window follows gaze", Hooks.CfgWindowGaze);
            }

            Slider(p, Left, "NR intensity", intensity, "F2");
            Slider(p, Left, "NR local tone", tone, "F2");
            Slider(p, Left, "NR local structure", structure, "F2");
            Choice(p, Left, "NR style", Mod<int>("CfgStyle"), new[] { "Default", "Natural", "Cinematic" }, new[] { 0, 1, 2 });
            Choice(p, Left, "NR passes", Mod<int>("CfgPasses"), new[] { "1", "2", "3" }, new[] { 1, 2, 3 });
            Toggle(p, Left, "NR before DLSS (always, in VR)", Mod<bool>("CfgNrBeforeSr"));

            // The mask: one region at a time, picked from a list, rather than eighteen sliders.
            ConfigEntry<bool> mask = Mod<bool>("CfgControlMask");
            ConfigEntry<float>[] regionIntensity = ModArray("CfgRegionIntensity");
            ConfigEntry<float>[] regionTone = ModArray("CfgRegionTone");
            ConfigEntry<float>[] regionStructure = ModArray("CfgRegionStructure");

            Toggle(p, Left, "Per-region control (mask)", mask);
            Slider(p, Left, "Region edge softness (px)", Mod<float>("CfgMaskSoftness"), "F0");

            if (regionIntensity != null && regionTone != null && regionStructure != null && regionIntensity.Length >= 6 && regionTone.Length >= 6 && regionStructure.Length >= 6)
            {
                JSONStorableStringChooser region = new JSONStorableStringChooser("Region to tune", new List<string>(RegionNames), RegionNames[0], "Region to tune", delegate(string name)
                {
                    for (int i = 0; i < RegionNames.Length; i++)
                    {
                        if (RegionNames[i] == name)
                        {
                            p.Region = RegionIndex[i];
                        }
                    }

                    foreach (Binding b in p.RegionBindings)
                    {
                        b.Pull(true);
                    }
                });

                UIDynamicPopup regionUi = p.Script.CreateScrollablePopup(region, Left);

                if (regionUi != null)
                {
                    regionUi.label = "Region to tune";
                }

                p.RegionBindings.Add(Slider(p, Left, "Region intensity (x NR intensity)", delegate { return regionIntensity[p.Region]; }, "F2"));
                p.RegionBindings.Add(Slider(p, Left, "Region tone (x local tone)", delegate { return regionTone[p.Region]; }, "F2"));
                p.RegionBindings.Add(Slider(p, Left, "Region structure (x local structure)", delegate { return regionStructure[p.Region]; }, "F2"));
                p.RegionBindings.RemoveAll(delegate(Binding b) { return b == null; });
            }

            // ---- right: status (the script's own box), DLSS, frame generation, resets ----
            Toggle(p, Right, "DLSS Super Resolution", Mod<bool>("CfgSrEnabled"));
            Choice(p, Right, "DLSS quality", Mod<int>("CfgSrQuality"),
                new[] { "DLAA (native)", "Ultra Quality", "Quality", "Balanced", "Performance", "Ultra Performance" }, new[] { 5, 4, 2, 1, 0, 3 });
            Choice(p, Right, "DLSS model", Mod<int>("CfgSrModel"), new[] { "DLSS 3", "DLSS 4", "DLSS 4.5" }, new[] { 0, 1, 2 });
            Slider(p, Right, "DLSS texture sharpening", Mod<float>("CfgMipBiasStrength"), "F2");

            Toggle(p, Right, "Frame generation (monitor only)", Mod<bool>("CfgFrameGen"));
            Choice(p, Right, "Frame generation multiplier", Mod<int>("CfgFgMultiplier"), new[] { "2x", "3x", "4x" }, new[] { 2, 3, 4 });
            Toggle(p, Right, "Frame generation: even pacing", Mod<bool>("CfgFgPacing"));

            // The monitor's window: its own shape, and aimed at the person rather than the middle.
            if (Hooks.WindowHooked)
            {
                Toggle(p, Right, "NR window on monitor", Hooks.CfgWindowMonitor);
                Slider(p, Right, "Monitor window width", Hooks.CfgMonitorWidth, "F2");
                Slider(p, Right, "Monitor window height", Hooks.CfgMonitorHeight, "F2");
                Toggle(p, Right, "Monitor window: follow", Hooks.CfgMonitorFollow);
                Toggle(p, Right, "Monitor window: fit people", Hooks.CfgMonitorFit);
            }

            // What the network changed, on its own: grey where it changed nothing, so a focus
            // window shows as the patch it is. Only has an effect while the model runs small or
            // through a window.
            Choice(p, Right, "NR debug view", Hooks.CfgDebugView, new[] { "Off", "Only what NR changed", "Frame without NR" }, new[] { 0, 2, 3 });

            // DLSS's sign switches. A picture that will not hold still under DLSS is nearly always
            // one of these pointing the wrong way for the setup it is running on, and from inside
            // a headset the .cfg is a long way off: here they can be flipped while looking at the
            // result. In a headset VamDlssNr reads the "headset" pair for motion, on the monitor
            // the other.
            ConfigEntry<bool> autoExposure = Mod<bool>("CfgSrAutoExposure");
            ConfigEntry<bool> jitter = Mod<bool>("CfgSrJitter");
            ConfigEntry<bool> jitterX = Mod<bool>("CfgSrJitterInvertX");
            ConfigEntry<bool> jitterY = Mod<bool>("CfgSrJitterInvertY");
            ConfigEntry<bool> vrMotionX = Mod<bool>("CfgVrInvertMotionX");
            ConfigEntry<bool> vrMotionY = Mod<bool>("CfgVrInvertMotionY");
            ConfigEntry<bool> motionX = Mod<bool>("CfgSrInvertMotionX");
            ConfigEntry<bool> motionY = Mod<bool>("CfgSrInvertMotionY");

            Toggle(p, Right, "DLSS fix: auto exposure", autoExposure);
            Toggle(p, Right, "DLSS fix: jitter", jitter);
            Toggle(p, Right, "DLSS fix: jitter Y flip", jitterY);
            Toggle(p, Right, "DLSS fix: jitter X flip", jitterX);
            Toggle(p, Right, "DLSS fix: motion Y flip VR", vrMotionY);
            Toggle(p, Right, "DLSS fix: motion X flip VR", vrMotionX);
            Toggle(p, Right, "DLSS fix: motion Y flip 2D", motionY);
            Toggle(p, Right, "DLSS fix: motion X flip 2D", motionX);

            Button(p, Right, "DLSS fix: reset to defaults", delegate
            {
                ToDefault(autoExposure);
                ToDefault(jitter);
                ToDefault(jitterX);
                ToDefault(jitterY);
                ToDefault(vrMotionX);
                ToDefault(vrMotionY);
                ToDefault(motionX);
                ToDefault(motionY);
            });

            Button(p, Right, "Reset NR strengths to defaults", delegate
            {
                ToDefault(Hooks.CfgScale);
                ToDefault(intensity);
                ToDefault(tone);
                ToDefault(structure);
            });

            if (regionIntensity != null && regionTone != null && regionStructure != null)
            {
                Button(p, Right, "Reset every region to 1.00", delegate
                {
                    for (int i = 0; i < 6 && i < regionIntensity.Length && i < regionTone.Length && i < regionStructure.Length; i++)
                    {
                        ToDefault(regionIntensity[i]);
                        ToDefault(regionTone[i]);
                        ToDefault(regionStructure[i]);
                    }
                });
            }
        }

        private static bool Attach(MVRScript script, Type type)
        {
            FieldInfo statusField = type.GetField("hostStatus", BindingFlags.Instance | BindingFlags.Public);
            FieldInfo attachedField = type.GetField("hostAttached", BindingFlags.Instance | BindingFlags.Public);
            JSONStorableString status = statusField != null ? statusField.GetValue(script) as JSONStorableString : null;

            // Its Init has not run yet; it will be seen again on the next scan.
            if (status == null)
            {
                return false;
            }

            Resolve();

            Panel p = new Panel();
            p.Script = script;
            p.Status = status;
            Build(p);

            if (attachedField != null && attachedField.FieldType == typeof(bool))
            {
                attachedField.SetValue(script, true);
            }

            status.val = StatusText();
            _panels.Add(p);
            Hooks.Info("control panel: filled " + type.FullName + " with " + p.Bindings.Count + " controls" +
                (_missing.Count != 0 ? " (this VamDlssNr has no " + string.Join(", ", _missing.ToArray()) + " -- those are left out)" : ""));
            return true;
        }

        // ---- finding the script ----------------------------------------------------------------

        // Every plugin script VaM runs -- session, scene or atom -- is created by
        // MVRPluginManager.CreateScriptController, which returns once the script's own Init has run
        // and its UI exists. Watching that one method finds the host the moment there is one.
        internal static void Watch()
        {
            try
            {
                MethodInfo create = typeof(MVRPluginManager).GetMethod("CreateScriptController", Any);

                if (create == null || !typeof(MVRScriptController).IsAssignableFrom(create.ReturnType))
                {
                    throw new MissingMethodException("MVRPluginManager.CreateScriptController");
                }

                new Harmony(WorkScalePlugin.Guid + ".panel").Patch(create, null,
                    new HarmonyMethod(typeof(ControlPanel).GetMethod("ScriptCreated", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)));
                _watching = true;
            }
            catch (Exception ex)
            {
                Hooks.Warn("VaM's plugin loader could not be watched (" + ex.GetType().Name + ": " + ex.Message + ") -- looking for the in-headset panel's script every two seconds instead");
            }
        }

        // Runs inside VaM's plugin loading, for every plugin: nothing may escape from here.
        internal static void ScriptCreated(MVRScriptController __result)
        {
            try
            {
                if (!Off && __result != null && __result.script != null)
                {
                    Offer(__result.script);
                }
            }
            catch (Exception ex)
            {
                Hooks.Error("control panel: " + ex.GetType().Name + " -- " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static void Offer(MVRScript script)
        {
            Type type = script.GetType();

            if (type.GetField(Marker, BindingFlags.Static | BindingFlags.Public) == null)
            {
                return;
            }

            for (int k = 0; k < _panels.Count; k++)
            {
                if (ReferenceEquals(_panels[k].Script, script))
                {
                    return;
                }
            }

            if (!Attach(script, type))
            {
                Hooks.Warn("control panel: " + type.FullName + " carries the marker but has no hostStatus -- its Init failed, or it is a different script; left alone");
            }
        }

        // The other way to find it, by looking: once at startup, and on a slow repeat only if the
        // loader could not be watched.
        private static void Scan()
        {
            MVRScript[] scripts = UnityEngine.Object.FindObjectsOfType<MVRScript>();

            for (int i = 0; i < scripts.Length; i++)
            {
                MVRScript script = scripts[i];
                int id = script.GetInstanceID();

                if (_refused.Contains(id))
                {
                    continue;
                }

                bool bound = false;

                for (int k = 0; k < _panels.Count; k++)
                {
                    bound = bound || ReferenceEquals(_panels[k].Script, script);
                }

                if (bound)
                {
                    continue;
                }

                Type type = script.GetType();

                if (type.GetField(Marker, BindingFlags.Static | BindingFlags.Public) == null)
                {
                    _refused.Add(id);
                    continue;
                }

                try
                {
                    Attach(script, type);
                }
                catch (Exception ex)
                {
                    _refused.Add(id);
                    Hooks.Error("control panel: could not fill " + type.FullName + ": " + ex.GetType().Name + " -- " + ex.Message + "\n" + ex.StackTrace);
                }
            }
        }

        // ---- the status box --------------------------------------------------------------------

        // The box is about thirty-five characters wide: lines are kept short enough not to wrap
        // where they are ours to word.
        private static string StatusText()
        {
            StringBuilder sb = new StringBuilder(320);
            string line = null;

            try
            {
                line = M_statusLine != null ? M_statusLine.Invoke(null, null) as string : null;
            }
            catch
            {
            }

            sb.Append("<b>NR</b>: ").Append(string.IsNullOrEmpty(line) ? "?" : line).Append('\n');

            string model = Hooks.ModelLine();

            if (model.Length != 0)
            {
                sb.Append(model).Append('\n');
            }

            foreach (FieldInfo field in new[] { F_chain, F_upscale, F_pace })
            {
                string s = Text(field);

                if (!string.IsNullOrEmpty(s))
                {
                    sb.Append(s).Append('\n');
                }
            }

            string fault = Text(F_fault);

            if (!string.IsNullOrEmpty(fault))
            {
                sb.Append("<color=#b03000>").Append(fault).Append("</color>\n");
            }

            string problem = Hooks.ProblemText();

            if (problem.Length != 0)
            {
                sb.Append("<color=#b03000>").Append(problem).Append("</color>\n");
            }

            sb.Append("\nThe same settings as VaM DLSS's F10 panel. Saved when you let go.");
            return sb.ToString();
        }

        // ---- every frame ---------------------------------------------------------------------

        // Whether anyone can see the panel: its UI is open, and so is the menu it lives in.
        private static bool IsShowing(Panel p)
        {
            Transform ui = p.Script.UITransform;
            return ui != null && ui.gameObject.activeInHierarchy;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;

            if (!_scanned || (!_watching && now >= _nextScan))
            {
                _scanned = true;
                _nextScan = now + 2f;
                Scan();
            }

            if (_saveAt >= 0f && now >= _saveAt)
            {
                _saveAt = -1f;
                SaveNow();
            }

            if (_panels.Count == 0 || now < _nextPull)
            {
                return;
            }

            _nextPull = now + 0.2f;
            bool statusDue = now >= _nextStatus;
            string text = null;

            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                Panel p = _panels[i];

                // The script was removed or reloaded; its controls went with it.
                if (p.Script == null)
                {
                    _panels.RemoveAt(i);
                    continue;
                }

                bool showing = IsShowing(p);
                bool opened = showing && !p.Showing;
                p.Showing = showing;

                // A panel nobody has open is left as it is; it catches up when it is opened.
                if (!showing)
                {
                    continue;
                }

                for (int k = 0; k < p.Bindings.Count; k++)
                {
                    p.Bindings[k].Pull(false);
                }

                if (statusDue || opened)
                {
                    text = text ?? StatusText();

                    if (p.Status != null && p.Status.val != text)
                    {
                        p.Status.val = text;
                    }
                }
            }

            if (statusDue)
            {
                _nextStatus = now + 0.33f;
            }
        }

        // ---- for the self-test -----------------------------------------------------------------

        internal static string Describe()
        {
            StringBuilder sb = new StringBuilder("panels=" + _panels.Count + (_watching ? " (found by watching VaM's plugin loader)" : " (found by scanning)"));

            foreach (Panel p in _panels)
            {
                sb.Append(" [").Append(p.Script != null ? p.Script.GetType().FullName : "gone").Append(": ").Append(p.Bindings.Count).Append(" controls, region ").Append(p.Region).Append(']');
            }

            if (_missing.Count != 0)
            {
                sb.Append(" missing=").Append(string.Join(",", _missing.ToArray()));
            }

            return sb.ToString();
        }

        internal static bool Watching
        {
            get { return _watching; }
        }

        // Opens or closes the first panel's UI, as "Open Custom UI" and its close button do, and
        // says where that UI sits and whether it can now be seen.
        internal static string ShowUi(bool show)
        {
            if (_panels.Count == 0 || _panels[0].Script == null)
            {
                return "no panel";
            }

            Transform ui = _panels[0].Script.UITransform;

            if (ui == null)
            {
                return "the script has no UI";
            }

            ui.gameObject.SetActive(show);

            StringBuilder path = new StringBuilder();

            for (Transform t = ui; t != null; t = t.parent)
            {
                path.Insert(0, "/" + t.name + (t.gameObject.activeSelf ? "" : "(off)"));
            }

            return (ui.gameObject.activeInHierarchy ? "visible" : "not visible") + " at " + path;
        }

        internal static bool UiShowing
        {
            get { return _panels.Count != 0 && _panels[0].Script != null && IsShowing(_panels[0]); }
        }

        // Moves the control with this label the way a hand on it would, and says what it now reads.
        internal static string Operate(string label, object value)
        {
            foreach (Panel p in _panels)
            {
                foreach (Binding b in p.Bindings)
                {
                    if (b.Label != label)
                    {
                        continue;
                    }

                    FloatBinding f = b as FloatBinding;
                    BoolBinding t = b as BoolBinding;
                    ChoiceBinding c = b as ChoiceBinding;

                    if (f != null)
                    {
                        f.Storable.val = (float)value;
                        return f.Storable.val.ToString("0.00");
                    }

                    if (t != null)
                    {
                        t.Storable.val = (bool)value;
                        return t.Storable.val.ToString();
                    }

                    if (c != null)
                    {
                        c.Storable.val = (string)value;
                        return c.Storable.val;
                    }
                }
            }

            return null;
        }

        internal static string Read(string label)
        {
            foreach (Panel p in _panels)
            {
                foreach (Binding b in p.Bindings)
                {
                    if (b.Label != label)
                    {
                        continue;
                    }

                    FloatBinding f = b as FloatBinding;
                    BoolBinding t = b as BoolBinding;
                    ChoiceBinding c = b as ChoiceBinding;
                    return f != null ? f.Storable.val.ToString("0.00") : (t != null ? t.Storable.val.ToString() : (c != null ? c.Storable.val : null));
                }
            }

            return null;
        }

        internal static string StatusNow()
        {
            return _panels.Count != 0 && _panels[0].Status != null ? _panels[0].Status.val : null;
        }
    }
}
