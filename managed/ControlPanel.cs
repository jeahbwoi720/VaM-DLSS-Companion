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
// The controls are spread over pages, one a feature, with a row of tabs across the top: one list of
// everything had grown longer than a pointer cares to scroll.
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

        // A colour that is three settings, on VaM's own colour picker. Picking one there makes the
        // list of ready-made colours say "Custom"; picking from the list moves the picker.
        private sealed class ColourBinding : Binding
        {
            internal ConfigEntry<float> Red, Green, Blue;
            internal ConfigEntry<int> Preset;
            internal JSONStorableColor Storable;
            private float _r = -1f, _g = -1f, _b = -1f;

            internal void Push(float h, float s, float v)
            {
                Color c = HSVColorPicker.HSVToRGB(h, s, v);

                if (Preset != null && Preset.Value != 0)
                {
                    Preset.Value = 0;
                }

                Red.Value = c.r;
                Green.Value = c.g;
                Blue.Value = c.b;
                _r = Red.Value;
                _g = Green.Value;
                _b = Blue.Value;
                Changed();
            }

            internal override void Pull(bool force)
            {
                float r = Red.Value, g = Green.Value, b = Blue.Value;

                if (force || r != _r || g != _g || b != _b)
                {
                    _r = r;
                    _g = g;
                    _b = b;
                    Storable.valNoCallback = HSVColorPicker.RGBToHSV(r, g, b);
                }
            }
        }

        private sealed class Panel
        {
            internal MVRScript Script;
            internal JSONStorableString Status;

            // The pages: their names, each control with the page it is on, the tab of each page.
            internal readonly List<string> Pages = new List<string>();
            internal readonly List<KeyValuePair<GameObject, int>> Items = new List<KeyValuePair<GameObject, int>>();
            internal readonly List<UIDynamicButton> Tabs = new List<UIDynamicButton>();
            internal Color TabColour = Color.white;
            internal int Building, Page;

            // Sections of a page that fold away under a button: which section each folded control
            // is in, whether each is open, and the one being built (-1: none).
            internal readonly Dictionary<GameObject, int> Folded = new Dictionary<GameObject, int>();
            internal readonly List<ConfigEntry<bool>> Folds = new List<ConfigEntry<bool>>();
            internal int Folding = -1;
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

        // The page the panel was last on: it opens there again.
        internal static ConfigEntry<int> CfgPage;
        internal static ConfigEntry<bool> CfgFoldBelow;

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

        internal static ConfigEntry<T> Mod<T>(string field)
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

        // What a control is for, shown in the panel's status box when it is pointed at: the
        // description its setting has in the .cfg, which is a long way off from inside a headset.
        // Only the two pointer events are taken -- a component that took them all would keep the
        // wheel and the drag from the panel's own scrolling.
        //
        // The descriptions are long and the box has to be scrolled, and the way to the box leads
        // over other controls. So a control has to be pointed at for half a second before its
        // text takes the box, and the text then stays: for as long as the pointer is on the box
        // itself, and for some seconds after it has left everything.
        internal sealed class Hint : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
        {
            internal const float Dwell = 0.5f, Stay = 6f;

            internal string Title, Text;
            internal bool Box;            // this one is the status box itself, not a control
            internal static Hint Over;    // the control whose text is shown
            internal static bool Changed;
            private static Hint _pointed; // what the pointer is on now, and since when
            private static float _pointedAt, _leftAt;
            private static bool _onBox;

            internal static void OnBox(UIDynamic box)
            {
                if (box != null && box.gameObject.GetComponent<Hint>() == null)
                {
                    box.gameObject.AddComponent<Hint>().Box = true;
                }
            }

            // Every frame: a control pointed at long enough takes the box; what is shown goes when it has been left long enough.
            internal static void Step(float now)
            {
                if ((object)_pointed != null && !ReferenceEquals(_pointed, Over) && now - _pointedAt >= Dwell)
                {
                    Over = _pointed;
                    Changed = true;
                }

                if ((object)Over != null && (object)_pointed == null && !_onBox && now - _leftAt > Stay)
                {
                    Over = null;
                    Changed = true;
                }
            }

            internal static void On(UIDynamic ui, string title, BepInEx.Configuration.ConfigDescription described)
            {
                if (ui == null || described == null || string.IsNullOrEmpty(described.Description))
                {
                    return;
                }

                Hint hint = ui.gameObject.GetComponent<Hint>();
                hint = hint != null ? hint : ui.gameObject.AddComponent<Hint>();
                hint.Title = title;
                hint.Text = described.Description;
            }

            public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData data)
            {
                if (Box)
                {
                    _onBox = true;
                    return;
                }

                _pointed = this;
                _pointedAt = Time.unscaledTime;
            }

            public void OnPointerExit(UnityEngine.EventSystems.PointerEventData data)
            {
                _leftAt = Time.unscaledTime;

                if (Box)
                {
                    _onBox = false;
                }
                else if (ReferenceEquals(_pointed, this))
                {
                    _pointed = null;
                }
            }

            // The text for the status box, or null when nothing is pointed at.
            internal static string Shown()
            {
                Hint hint = Over;

                if ((object)hint == null || hint == null || !hint.isActiveAndEnabled)
                {
                    Over = null;
                    _pointed = ReferenceEquals(_pointed, hint) ? null : _pointed;
                    return null;
                }

                return hint.Title + "\n\n" + hint.Text;
            }
        }

        // Every control belongs to the page that was being built when it was made.
        private static void Put(Panel p, UIDynamic ui)
        {
            if (ui != null)
            {
                p.Items.Add(new KeyValuePair<GameObject, int>(ui.gameObject, p.Building));

                if (p.Folding >= 0)
                {
                    p.Folded[ui.gameObject] = p.Folding;
                }
            }
        }

        // A section that folds away: a button across the column, and under it whatever is made
        // until EndFold -- shown only while the section is open, which is remembered. For the
        // settings that most people leave alone, so that the page is not a wall of sliders.
        private static void BeginFold(Panel p, bool right, string title, ConfigEntry<bool> open)
        {
            if (open == null)
            {
                return;
            }

            UIDynamicButton ui = p.Script.CreateButton((open.Value ? "- " : "+ ") + title, right);
            Put(p, ui);
            int index = p.Folds.Count;
            p.Folds.Add(open);
            p.Folding = index;

            if (ui != null && ui.button != null)
            {
                ui.buttonColor = new Color(0.82f, 0.82f, 0.9f);
                ui.button.onClick.AddListener(delegate
                {
                    open.Value = !open.Value;

                    if (ui.buttonText != null)
                    {
                        ui.buttonText.text = (open.Value ? "- " : "+ ") + title;
                    }

                    Show(p, p.Page);
                });
            }
        }

        private static void EndFold(Panel p)
        {
            p.Folding = -1;
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

            Hint.On(ui, label, first.Description);
            Put(p, ui);
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

            Hint.On(ui, label, entry.Description);
            Put(p, ui);
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

            Hint.On(ui, label, entry.Description);
            Put(p, ui);
            p.Bindings.Add(b);
        }

        // VaM's own colour picker, for a colour kept as three settings.
        private static void Colour(Panel p, bool right, string label, ConfigEntry<float> red, ConfigEntry<float> green, ConfigEntry<float> blue, ConfigEntry<int> preset)
        {
            if (red == null || green == null || blue == null)
            {
                return;
            }

            ColourBinding b = new ColourBinding();
            b.Label = label;
            b.Red = red;
            b.Green = green;
            b.Blue = blue;
            b.Preset = preset;
            b.Storable = new JSONStorableColor(label, HSVColorPicker.RGBToHSV(red.Value, green.Value, blue.Value), new JSONStorableColor.SetHSVColorCallback(b.Push));
            b.Storable.defaultVal = HSVColorPicker.RGBToHSV((float)red.DefaultValue, (float)green.DefaultValue, (float)blue.DefaultValue);

            UIDynamicColorPicker ui = p.Script.CreateColorPicker(b.Storable, right);

            if (ui != null)
            {
                ui.label = label;
            }

            Put(p, ui);
            p.Bindings.Add(b);
        }

        private static void Button(Panel p, bool right, string label, Action action)
        {
            UIDynamicButton ui = p.Script.CreateButton(label, right);
            Put(p, ui);

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

        // ---- pages -----------------------------------------------------------------------------
        //
        // One long list of everything had the last controls a long way down a scroll bar that is
        // awkward to work with a pointer. Each feature has a page instead; a row of tabs across the
        // top of both columns picks it. The controls of the other pages still exist -- they are
        // switched off, which costs nothing and keeps every binding alive.

        private static void Page(Panel p, string name)
        {
            p.Building = p.Pages.Count;
            p.Pages.Add(name);
        }

        private static void Show(Panel p, int page)
        {
            if (p.Pages.Count == 0)
            {
                return;
            }

            page = Mathf.Clamp(page, 0, p.Pages.Count - 1);
            p.Page = page;

            for (int i = 0; i < p.Items.Count; i++)
            {
                GameObject go = p.Items[i].Key;

                if (go == null)
                {
                    continue;
                }

                int fold;
                bool on = p.Items[i].Value == page && (!p.Folded.TryGetValue(go, out fold) || p.Folds[fold].Value);

                if (go.activeSelf != on)
                {
                    go.SetActive(on);
                }
            }

            for (int i = 0; i < p.Tabs.Count; i++)
            {
                if (p.Tabs[i] != null)
                {
                    p.Tabs[i].buttonColor = i == page ? new Color(0.55f, 0.78f, 1f) : p.TabColour;
                }
            }

            if (CfgPage != null && CfgPage.Value != page)
            {
                CfgPage.Value = page;
            }
        }

        // The tabs: half of them above the left column, the rest above the right. Each row is one of
        // VaM's own buttons with its face switched off, so that the column lays it out as it does any
        // other control, holding one small button a tab side by side.
        private static void Tabs(Panel p)
        {
            Transform prefab = p.Script.manager != null ? p.Script.manager.configurableButtonPrefab : null;
            int count = p.Pages.Count;
            int leftCount = (count + 1) / 2;

            for (int column = 0; column < 2 && prefab != null && count > 1; column++)
            {
                int from = column == 0 ? 0 : leftCount, to = column == 0 ? leftCount : count;

                if (from >= to)
                {
                    continue;
                }

                UIDynamicButton holder = p.Script.CreateButton("", column == 1);

                if (holder == null)
                {
                    continue;
                }

                holder.transform.SetAsFirstSibling();

                if (holder.button != null)
                {
                    holder.button.enabled = false;
                }

                if (holder.buttonImage != null)
                {
                    holder.buttonImage.enabled = false;
                }

                if (holder.buttonText != null)
                {
                    holder.buttonText.gameObject.SetActive(false);
                }

                UnityEngine.UI.HorizontalLayoutGroup row = holder.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                row.spacing = 6f;
                row.childControlWidth = true;
                row.childControlHeight = true;
                row.childForceExpandWidth = true;
                row.childForceExpandHeight = true;

                for (int i = from; i < to; i++)
                {
                    Transform t = UnityEngine.Object.Instantiate(prefab);
                    t.SetParent(holder.transform, false);
                    t.gameObject.SetActive(true);
                    UIDynamicButton tab = t.GetComponent<UIDynamicButton>();

                    // every tab the same share of the row, whatever its word
                    UnityEngine.UI.LayoutElement size = t.GetComponent<UnityEngine.UI.LayoutElement>();

                    if (size == null)
                    {
                        size = t.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                    }

                    size.minWidth = 0f;
                    size.preferredWidth = 0f;
                    size.flexibleWidth = 1f;

                    while (p.Tabs.Count <= i)
                    {
                        p.Tabs.Add(null);
                    }

                    p.Tabs[i] = tab;

                    if (tab == null)
                    {
                        continue;
                    }

                    tab.label = p.Pages[i];
                    p.TabColour = tab.buttonColor;

                    if (tab.buttonText != null)
                    {
                        tab.buttonText.resizeTextForBestFit = true;
                        tab.buttonText.resizeTextMinSize = 14;
                        tab.buttonText.resizeTextMaxSize = 28;
                    }

                    if (tab.button != null)
                    {
                        int page = i;
                        tab.button.onClick.AddListener(delegate
                        {
                            try
                            {
                                Show(p, page);
                            }
                            catch (Exception ex)
                            {
                                Hooks.Error("control panel: page '" + p.Pages[page] + "' could not be shown: " + ex.Message);
                            }
                        });
                    }
                }
            }

            Show(p, CfgPage != null ? CfgPage.Value : 0);
        }

        private static void Build(Panel p)
        {
            const bool Left = false, Right = true;

            // ---- Neural Rendering: the network on the left, its per-region mask on the right ----
            Page(p, "NR");

            ConfigEntry<bool> nr = Mod<bool>("CfgEnabled");
            ConfigEntry<float> intensity = Mod<float>("CfgIntensity");
            ConfigEntry<float> tone = Mod<float>("CfgLocalTone");
            ConfigEntry<float> structure = Mod<float>("CfgLocalStructure");

            Toggle(p, Left, "Neural Rendering", nr);
            Slider(p, Left, "NR model resolution (applies when let go)", Hooks.CfgScale, "F2");
            BeginFold(p, Left, "Below 100% model resolution: options", CfgFoldBelow);
                Toggle(p, Left, "Edit follows edges (costs fps)", Hooks.CfgEditFollow);
            Slider(p, Left, "Edit sharpening (0 = off)", Hooks.CfgEditSharpen, "F1");
            Slider(p, Left, "Edit denoise (0 = off)", Hooks.CfgEditDenoise, "F2");
            Toggle(p, Left, "Edit kept over frames (steadier)", Hooks.CfgEditSteady);
            Slider(p, Left, "Kept over frames: share of each new frame", Hooks.CfgEditSteadyTake, "F2");
            Toggle(p, Left, "Build detail over frames (experimental)", Hooks.CfgEditDetail);
            Slider(p, Left, "Detail over frames: strength", Hooks.CfgEditDetailOwn, "F2");
            Slider(p, Left, "Input sharpening (0 = off)", Hooks.CfgInputSharpen, "F2");
                EndFold(p);
            Slider(p, Left, "NR intensity", intensity, "F2");
            Slider(p, Left, "NR local tone", tone, "F2");
            Slider(p, Left, "NR local structure", structure, "F2");
            Choice(p, Left, "NR style", Mod<int>("CfgStyle"), new[] { "Default", "Natural", "Cinematic" }, new[] { 0, 1, 2 });
            Choice(p, Left, "NR passes", Mod<int>("CfgPasses"), new[] { "1", "2", "3" }, new[] { 1, 2, 3 });
            Toggle(p, Left, "NR before DLSS (always, in VR)", Mod<bool>("CfgNrBeforeSr"));

            // What the network changed, on its own: grey where it changed nothing, so a focus
            // window shows as the patch it is. Only has an effect while the model runs small or
            // through a window.
            Choice(p, Left, "NR debug view", Hooks.CfgDebugView, new[] { "Off", "Only what NR changed", "Frame without NR" }, new[] { 0, 2, 3 });

            Button(p, Left, "Reset NR strengths to defaults", delegate
            {
                // Not the model resolution: that one is a frame-rate setting, and putting it back to
                // 100% from a button about strengths halves the frame rate without saying so.
                ToDefault(intensity);
                ToDefault(tone);
                ToDefault(structure);
            });

            // The mask: one region at a time, picked from a list, rather than eighteen sliders.
            ConfigEntry<bool> mask = Mod<bool>("CfgControlMask");
            ConfigEntry<float>[] regionIntensity = ModArray("CfgRegionIntensity");
            ConfigEntry<float>[] regionTone = ModArray("CfgRegionTone");
            ConfigEntry<float>[] regionStructure = ModArray("CfgRegionStructure");

            Toggle(p, Right, "Per-region control (mask)", mask);
            Slider(p, Right, "Region edge softness (px)", Mod<float>("CfgMaskSoftness"), "F0");

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

                UIDynamicPopup regionUi = p.Script.CreateScrollablePopup(region, Right);

                if (regionUi != null)
                {
                    regionUi.label = "Region to tune";
                }

                Put(p, regionUi);
                p.RegionBindings.Add(Slider(p, Right, "Region intensity (x NR intensity)", delegate { return regionIntensity[p.Region]; }, "F2"));
                p.RegionBindings.Add(Slider(p, Right, "Region tone (x local tone)", delegate { return regionTone[p.Region]; }, "F2"));
                p.RegionBindings.Add(Slider(p, Right, "Region structure (x local structure)", delegate { return regionStructure[p.Region]; }, "F2"));
                p.RegionBindings.RemoveAll(delegate(Binding b) { return b == null; });

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

            // ---- the focus window: the headset's on the left, the monitor's on the right. Only
            // where its hooks went in, or the sliders would move nothing. ----
            if (Hooks.WindowHooked)
            {
                Page(p, "Window");
                Slider(p, Left, "NR window (1.00 = whole view)", Hooks.CfgWindow, "F2");
                Slider(p, Left, "NR window edge softness", Hooks.CfgWindowFeather, "F2");
                Toggle(p, Left, "NR window follows gaze", Hooks.CfgWindowGaze);

                // The monitor's window: its own shape, and aimed at the person rather than the middle.
                Toggle(p, Right, "NR window on monitor", Hooks.CfgWindowMonitor);
                Slider(p, Right, "Monitor window width", Hooks.CfgMonitorWidth, "F2");
                Slider(p, Right, "Monitor window height", Hooks.CfgMonitorHeight, "F2");
                Toggle(p, Right, "Monitor window: follow", Hooks.CfgMonitorFollow);
                Toggle(p, Right, "Monitor window: fit people", Hooks.CfgMonitorFit);
                Toggle(p, Right, "Show the window's outline (for testing)", Hooks.CfgWindowOutline);
            }

            // ---- DLSS and the picture on the left, its sign switches on the right ----
            Page(p, "DLSS");
            Toggle(p, Left, "DLSS Super Resolution", Mod<bool>("CfgSrEnabled"));
            Choice(p, Left, "DLSS quality", Mod<int>("CfgSrQuality"),
                new[] { "DLAA (native)", "Ultra Quality", "Quality", "Balanced", "Performance", "Ultra Performance" }, new[] { 5, 4, 2, 1, 0, 3 });
            Choice(p, Left, "DLSS model", Mod<int>("CfgSrModel"), new[] { "DLSS 3", "DLSS 4", "DLSS 4.5" }, new[] { 0, 1, 2 });
            // Not a sharpening filter: how far textures are biased towards their sharper mips while
            // DLSS upscales. It does nothing at DLAA.
            Slider(p, Left, "DLSS texture detail (not DLAA)", Mod<float>("CfgMipBiasStrength"), "F2");

            if (HeadsetUi.Hooked && HeadsetUi.CfgSharpen != null)
            {
                Slider(p, Left, "Sharpening (0 = off)", HeadsetUi.CfgSharpen, "F2");
            }

            if (HeadsetUi.Hooked && HeadsetUi.CfgOn != null)
            {
                Toggle(p, Left, "Headset menu at full size", HeadsetUi.CfgOn);
            }

            if (HeadsetUi.Hooked && SceneUi.CfgOn != null)
            {
                Toggle(p, Left, "Scene UI atoms: after DLSS / NR", SceneUi.CfgOn);
            }

            Toggle(p, Left, "Frame generation (monitor only)", Mod<bool>("CfgFrameGen"));
            Choice(p, Left, "Frame generation multiplier", Mod<int>("CfgFgMultiplier"), new[] { "2x", "3x", "4x" }, new[] { 2, 3, 4 });
            Toggle(p, Left, "Frame generation: even pacing", Mod<bool>("CfgFgPacing"));

            // Read when VaM starts: without it the compositor drops most generated frames.
            if (Presentation.CfgFlip != null)
            {
                Toggle(p, Left, "Monitor: flip-model window (restart)", Presentation.CfgFlip);
                Toggle(p, Left, "Frame generation: paced by queue", Presentation.CfgPace);
            }

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

            // ---- foveated shading of the scene ----
            if (Foveation.CfgOn != null)
            {
                Page(p, "Foveation");
                Toggle(p, Left, "Foveated shading (NVIDIA)", Foveation.CfgOn);
                Toggle(p, Left, "Foveation follows gaze", Foveation.CfgGaze);
                Slider(p, Left, "Foveation: full detail within", Foveation.CfgInner, "F2");
                Slider(p, Left, "Foveation: coarsest beyond", Foveation.CfgOuter, "F2");
                Toggle(p, Left, "Foveation: strong at the edges", Foveation.CfgStrong);

                Toggle(p, Right, "Foveation: show the zones", Foveation.CfgShow);
                Toggle(p, Right, "Foveation: zone is upside down", Foveation.CfgTopDown);
                Toggle(p, Right, "Foveation on the monitor too", Foveation.CfgMonitor);
            }

            // ---- passthrough: what is cut out on the left, how the room is shown on the right ----
            if (HeadsetUi.Hooked && Passthrough.CfgOn != null)
            {
                Page(p, "Passthrough");
                Toggle(p, Left, "Passthrough (headset)", Passthrough.CfgOn);
                Choice(p, Left, "Passthrough key colour", Passthrough.CfgPreset, new[] { "Custom", "Green", "Blue", "Magenta", "Black", "White" }, new[] { 0, 1, 2, 3, 4, 5 });
                // VaM's own picker; picking a colour there makes the list above say "Custom".
                Colour(p, Left, "Key colour", Passthrough.CfgRed, Passthrough.CfgGreen, Passthrough.CfgBlue, Passthrough.CfgPreset);
                Slider(p, Left, "Passthrough tolerance", Passthrough.CfgTolerance, "F2");
                Slider(p, Left, "Passthrough edge softness", Passthrough.CfgSoftness, "F2");

                Choice(p, Right, "Passthrough mode", Passthrough.CfgMode, new[] { "Own overlay (camera's pace)", "In the game's frame" }, new[] { 0, 1 });
                Slider(p, Right, "Passthrough overlay distance (m)", Passthrough.CfgOverlayDistance, "F0");
                Toggle(p, Right, "Passthrough overlay: room behind", Passthrough.CfgRoomBehind);
                Slider(p, Right, "Passthrough distance (m)", Passthrough.CfgDistance, "F2");
                Slider(p, Right, "Passthrough brightness", Passthrough.CfgBrightness, "F2");
                Slider(p, Right, "Passthrough room size", Passthrough.CfgFocal, "F0");
                Toggle(p, Right, "Passthrough follows head", Passthrough.CfgFollowHead);
                Choice(p, Right, "Passthrough view", Passthrough.CfgView, new[] { "Picture", "Matte", "Camera everywhere", "Room depth", "Scene depth" }, new[] { 0, 1, 2, 3, 4 });

                if (Passthrough.CfgDepth != null)
                {
                    Toggle(p, Right, "Passthrough depth (experimental)", Passthrough.CfgDepth);
                    Slider(p, Right, "Passthrough depth margin", Passthrough.CfgDepthMargin, "F2");
                }

                Button(p, Right, "Passthrough: save a capture", delegate
                {
                    Passthrough.CaptureWanted = true;
                });
            }

            // ---- the wearer's hands from the same cameras: what they do on the left, how they are
            // found and the tools for looking into it on the right ----
            if (Hands.CfgOn != null)
            {
                Page(p, "Hands");
                Toggle(p, Left, "Hand tracking (experimental)", Hands.CfgOn);
                Choice(p, Left, "Hand tracking: VaM's hands follow", HandDrive.CfgMode, new[] { "Controllers", "Auto", "Tracked hands" }, new[] { 0, 1, 2 });
                Toggle(p, Left, "Hand tracking: menus (not working yet)", HandUi.CfgOn);
                Toggle(p, Left, "Hand tracking: Mercury model (restart)", Hands.CfgMercury);
                Toggle(p, Left, "Hand tracking: full-size model (restart)", Hands.CfgFull);
                Toggle(p, Left, "Hand tracking: fingers by curl (Mercury)", HandDrive.CfgCurls);
                Toggle(p, Left, "Hand tracking: pinch assist", HandDrive.CfgPinch);
                Toggle(p, Left, "Hand tracking: show skeleton", Hands.CfgShow);
                Toggle(p, Left, "Hand tracking: both hands", Hands.CfgBoth);
                Choice(p, Left, "Hand tracking: looks a second", Hands.CfgEvery, new[] { "60", "30", "20", "15" }, new[] { 1, 2, 3, 4 });
                Slider(p, Left, "Hand tracking: steadiness", Hands.CfgSmoothing, "F1");
                Slider(p, Left, "Hand tracking: quickness", Hands.CfgQuick, "F0");
                Toggle(p, Left, "Hand tracking: swap left and right", Hands.CfgSwap);

                Button(p, Right, "Hand tracking: save a camera frame", delegate
                {
                    Hands.CaptureWanted = true;
                });
                Button(p, Right, "Hand tracking: record 8 s (starts in 4 s)", delegate
                {
                    Hands.RecordWanted = true;
                });

                // What the headset's cameras give SteamVR, written to the log: the first question any
                // passthrough has to ask.
                Button(p, Right, "Probe headset camera (to log)", delegate
                {
                    CameraProbe.Begin(Time.unscaledTime);
                });
            }

            Tabs(p);
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
            Hint.OnBox(status.dynamicText);
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

            string menu = HeadsetUi.Line();

            if (menu.Length != 0)
            {
                sb.Append(menu).Append('\n');
            }

            if (Passthrough.Status.Length != 0)
            {
                sb.Append(Passthrough.Status).Append('\n');
            }

            if (SceneUi.Status.Length != 0)
            {
                sb.Append(SceneUi.Status).Append('\n');
            }

            if (Presentation.Status.Length != 0)
            {
                sb.Append(Presentation.Status).Append('\n');
            }

            if (Foveation.Status.Length != 0)
            {
                sb.Append(Foveation.Status).Append('\n');
            }

            if (Hands.Status.Length != 0)
            {
                sb.Append(Hands.Status).Append('\n');
            }

            if (HandUi.Note.Length != 0)
            {
                sb.Append(HandUi.Note).Append('\n');
            }

            if (CameraProbe.Summary.Length != 0)
            {
                sb.Append(CameraProbe.Summary).Append('\n');
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

            Hint.Step(now);

            if (_panels.Count == 0 || (now < _nextPull && !Hint.Changed))
            {
                return;
            }

            _nextPull = now + 0.2f;
            bool statusDue = now >= _nextStatus || Hint.Changed;
            string text = Hint.Shown();
            Hint.Changed = false;

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
