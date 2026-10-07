// VaM DLSS - Model Resolution: the menu pointers rest while they are not on a menu.
//
// Every frame VaM asks, for each thing that can point at a menu, what it points at
// (SuperController.ProcessUI): the left controller, the right controller and the mouse, one after
// the other. Before each it hands that pointer's camera to every world-space canvas of the scene
// (AssignUICamera), then has the event system cast through all of them (LookInputModule's
// ProcessMain, ProcessRight and ProcessMouseAlt). On the monitor only the mouse's pass runs, and
// it came to 0.12 ms of a frame; in a headset the three came to 2.4-4.0 ms -- of a main thread that
// needs 18 ms where the headset's 60 fps step allows 16.7.
//
// Most of the time in a scene neither controller is on a menu and the mouse lies still. So here a
// pointer that was not on a menu the last time it was asked is asked on one frame in four only
// (each pointer on a frame of its own), and at once again every frame from the moment it is on
// one. A pointer is never rested while it holds something pressed or dragged, while a text field
// has the keyboard, or -- the mouse -- while it moves, scrolls or has a button down.
//
// A headset only: on the monitor there is the mouse's pass alone, and it is cheap.

using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

namespace VamDlssNrWorkScale
{
    internal static class MenuPointers
    {
        internal static ConfigEntry<bool> CfgOn;

        internal static string Problem = "";
        internal static bool Hooked;
        internal static bool Trial; // the profile's trial has them rest whatever the setting says

        private const int Left = 0, Right = 1, Mouse = 2;
        private const int Every = 4;

        // ---- which frames a resting pointer is asked on: plain numbers (the host test runs this) ----

        // A pointer that is `busy` (on a menu, holding something) is asked every frame; one at
        // rest on every fourth, and no two pointers on the same one.
        internal static bool Asked(int frame, int which, bool busy)
        {
            return busy || (frame + which) % Every == 0;
        }

        // ---- VaM's side -----------------------------------------------------------------------------

        private static FieldInfo F_guiHit, F_pressed, F_pressedRight, F_dragging, F_draggingRight;

        internal static void Apply(Harmony harmony)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type module = typeof(LookInputModule);

            try
            {
                F_guiHit = module.GetField("_guiRaycastHit", any);
                F_pressed = module.GetField("currentPressed", any);
                F_pressedRight = module.GetField("currentPressedRight", any);
                F_dragging = module.GetField("currentDragging", any);
                F_draggingRight = module.GetField("currentDraggingRight", any);
                MethodInfo assign = typeof(SuperController).GetMethod("AssignUICamera", any, null, new[] { typeof(Camera) }, null);
                MethodInfo main = module.GetMethod("ProcessMain", any, null, Type.EmptyTypes, null);
                MethodInfo right = module.GetMethod("ProcessRight", any, null, Type.EmptyTypes, null);
                MethodInfo mouse = module.GetMethod("ProcessMouseAlt", any, null, new[] { typeof(bool) }, null);

                foreach (object needed in new object[] { F_guiHit, F_pressed, F_pressedRight, F_dragging, F_draggingRight, assign, main, right, mouse })
                {
                    if (needed == null)
                    {
                        Problem = "menu pointers: this build of VaM asks its pointers another way, and they cannot be rested";
                        return;
                    }
                }

                Type me = typeof(MenuPointers);
                const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;
                harmony.Patch(assign, new HarmonyMethod(me.GetMethod("AssignPrefix", pub)));
                harmony.Patch(main, new HarmonyMethod(me.GetMethod("MainPrefix", pub)), new HarmonyMethod(me.GetMethod("MainPostfix", pub)));
                harmony.Patch(right, new HarmonyMethod(me.GetMethod("RightPrefix", pub)), new HarmonyMethod(me.GetMethod("RightPostfix", pub)));
                harmony.Patch(mouse, new HarmonyMethod(me.GetMethod("MousePrefix", pub)));
                Hooked = true;
            }
            catch (Exception ex)
            {
                Hooked = false;
                Problem = "menu pointers: could not be rested (" + ex.GetType().Name + ": " + ex.Message + ")";

                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- a frame --------------------------------------------------------------------------------

        private static int _pass = -1;        // the pass AssignUICamera has just been called for
        private static bool _rests;           // ...and whether it is left out this frame
        private static int _ran = -1;         // the pass that is running now
        private static readonly bool[] _hit = new bool[2]; // was on a menu the last time it was asked
        private static Vector3 _mouseAt;
        private static int _asked, _rested;

        private static bool Holds(LookInputModule module, int which)
        {
            if (which == Mouse)
            {
                Vector3 at = Input.mousePosition;
                bool moved = at != _mouseAt;
                _mouseAt = at;

                if (moved || Input.mouseScrollDelta != Vector2.zero)
                {
                    return true;
                }

                for (int button = 0; button < 3; button++)
                {
                    if (Input.GetMouseButton(button) || Input.GetMouseButtonUp(button))
                    {
                        return true;
                    }
                }

                return false;
            }

            return _hit[which] || (which == Left ? F_pressed : F_pressedRight).GetValue(module) != null || (which == Left ? F_dragging : F_draggingRight).GetValue(module) != null;
        }

        private static bool Selected()
        {
            EventSystem events = EventSystem.current;
            return ((object)events != null && events.currentSelectedGameObject != null) || UITabSelector.activeTabSelector != null;
        }

        private static bool Pushed(LookInputModule module)
        {
            return Mathf.Abs(JoystickControl.GetAxis(module.controlAxis)) > 0.01f || Mathf.Abs(JoystickControl.GetAxis(module.discreteControlAxis)) > 0.01f;
        }

        // Before a pass VaM hands its camera to the canvases: here the pass is told apart by that
        // camera, and when it rests this frame the handing over is left out with it.
        public static bool AssignPrefix(SuperController __instance, Camera __0)
        {
            _pass = -1;
            _rests = false;

            try
            {
                if (!(Trial || (CfgOn != null && CfgOn.Value)) || (object)__0 == null || __instance.useLookSelect || !XRSettings.enabled || __instance.IsMonitorRigActive)
                {
                    return true;
                }

                int which = (object)__0 == (object)__instance.leftControllerCamera ? Left
                    : (object)__0 == (object)__instance.rightControllerCamera ? Right
                    : (object)__0 == (object)__instance.MonitorCenterCamera ? Mouse : -1;
                LookInputModule module = LookInputModule.singleton;

                if (which < 0 || (object)module == null)
                {
                    return true;
                }

                // (a text field takes its keys in every pass; where a pointer's buttons count while it
                // is off the menus, the left one's pass has the stick move what is selected -- and a
                // button stays selected long after it was pressed, so only while the stick is pushed;
                // and the mouse in 'free move' is cast from where it is whether it moved or not)
                bool busy = module.inputFieldActive || (which == Left && !module.ignoreInputsWhenLookAway && Selected() && Pushed(module)) ||
                    (which == Mouse && __instance.currentSelectMode == SuperController.SelectMode.FreeMoveMouse) || Holds(module, which);
                _pass = which;
                _rests = !Asked(Time.frameCount, which, busy);

                if (_rests)
                {
                    _rested++;
                    return false;
                }

                _asked++;
                return true;
            }
            catch (Exception)
            {
                _pass = -1;
                _rests = false;
                return true;
            }
        }

        private static bool Pass(LookInputModule module, int which)
        {
            bool mine = _pass == which;
            bool rests = mine && _rests;
            _pass = -1;
            _rests = false;
            _ran = mine && !rests ? which : -1;

            if (rests && which != Mouse)
            {
                // VaM reads this right after the pass, as "this controller is on a menu"
                F_guiHit.SetValue(module, false);
            }

            return !rests;
        }

        public static bool MainPrefix(LookInputModule __instance)
        {
            return Pass(__instance, Left);
        }

        public static bool RightPrefix(LookInputModule __instance)
        {
            return Pass(__instance, Right);
        }

        public static bool MousePrefix(LookInputModule __instance)
        {
            return Pass(__instance, Mouse);
        }

        public static void MainPostfix(LookInputModule __instance)
        {
            if (_ran == Left)
            {
                _hit[Left] = __instance.guiRaycastHit;
            }

            _ran = -1;
        }

        public static void RightPostfix(LookInputModule __instance)
        {
            if (_ran == Right)
            {
                _hit[Right] = __instance.guiRaycastHit;
            }

            _ran = -1;
        }

        // ---- for the profile's log --------------------------------------------------------------------

        // How many passes were asked and how many rested since this was last called.
        internal static string Counted()
        {
            string line = _asked + _rested == 0 ? "" : "menu pointers: " + _asked + " passes asked, " + _rested + " rested";
            _asked = 0;
            _rested = 0;
            return line;
        }

        // What the passes have to go through, as the scene is now.
        internal static string Scene()
        {
            try
            {
                Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                int world = 0;

                foreach (Canvas canvas in canvases)
                {
                    if (canvas.renderMode == RenderMode.WorldSpace)
                    {
                        world++;
                    }
                }

                int known = -1;
                FieldInfo all = typeof(SuperController).GetField("allCanvases", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                System.Collections.ICollection list = all != null && SuperController.singleton != null ? all.GetValue(SuperController.singleton) as System.Collections.ICollection : null;

                if (list != null)
                {
                    known = list.Count;
                }

                SuperController sc = SuperController.singleton;
                return "the menus: " + (known < 0 ? "?" : known.ToString()) + " canvases on VaM's list (each is handed a camera before every pointer's pass), " + canvases.Length + " canvases active now (" + world + " in the world), " +
                    UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>().Length + " of them cast through, " + UnityEngine.Object.FindObjectsOfType<Graphic>().Length + " drawn elements; " +
                    (sc == null ? "" : "pointers: " + (sc.useLookSelect ? "the look" : (sc.leftControllerCamera != null && sc.leftControllerCamera.gameObject.activeInHierarchy ? "left " : "") +
                    (sc.rightControllerCamera != null && sc.rightControllerCamera.gameObject.activeInHierarchy ? "right " : "") + "mouse") + (sc.IsMonitorRigActive ? ", on the monitor" : ", in the headset")) +
                    (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null ? "; '" + EventSystem.current.currentSelectedGameObject.name + "' is selected" : "") +
                    (LookInputModule.singleton != null && !LookInputModule.singleton.ignoreInputsWhenLookAway ? "; buttons count off the menus too" : "");
            }
            catch (Exception ex)
            {
                return "the menus could not be counted (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }
    }
}
