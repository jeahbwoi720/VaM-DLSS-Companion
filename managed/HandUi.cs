// VaM DLSS - Model Resolution: VaM's menus used by hand.
//
// With the tracked hands driving VaM's own hand models (HandDrive.cs), the controllers lie on the
// table and nothing can be pointed at or clicked. This gives the hands what the controllers had,
// by feeding VaM's own paths rather than building a second interface:
//
//   pointing   VaM aims its UI pointer through a camera on each controller
//              (SuperController.left/rightControllerCamera). While a hand is ours that field holds
//              a camera of ours instead, placed at the hand and looking along a line from the
//              shoulder through the index knuckle -- the arm points, not the fingers, so the
//              pointer neither shakes with the fingertips nor jumps when they pinch.
//   clicking   thumb tip against index tip. VaM's input module asks "was the button pressed /
//              released this frame" through four small methods; a pinch closing and opening
//              answers yes to those. Held, it drags: sliders and scrolling come with it.
//   the menu   the left palm turned to the face, and a pinch: SuperController.GetMenuShow says
//              yes for one frame, which opens or closes the menu exactly as the button does.
//              That pinch is not a click, and a palm turned to the face has no pointer.
//
// Everything VaM is lent is taken back the moment a hand is no longer ours.

using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace VamDlssNrWorkScale
{
    internal static class HandUi
    {
        internal static ConfigEntry<bool> CfgOn;

        internal static string Note = "";

        // Thumb tip to index tip, in hand lengths (wrist to middle knuckle, about 9 cm): closed
        // below the first, open again above the second.
        private const float PinchOn = 0.30f, PinchOff = 0.50f;
        private const float Settle = 0.05f;                    // a change has to last this long to count
        private const float FaceOn = 0.55f, FaceOff = 0.35f;   // how squarely the palm faces the head
        // From the head to the shoulder the arm points from, in metres: sideways, down, back.
        private const float ShoulderOut = 0.18f, ShoulderDown = 0.22f, ShoulderBack = 0.04f;

        private sealed class Side
        {
            internal bool Pinched, Changing, MenuPinch, Facing, Pointing, HaveAim, Lent;
            internal bool Down, Up;   // this frame's answers to VaM's "pressed" and "released"
            internal float ChangingSince, Gap;
            internal Vector3 Origin, Aim;
            internal GameObject Holder;
            internal Camera Camera, VamCamera;
            internal LineRenderer Line, VamLine;
            internal object VamDrawer;
        }

        private static readonly Side[] _sides = { new Side(), new Side() };
        private static bool _patched, _failed, _menu;
        private static FieldInfo[] _guiHit, _drawer, _look;
        private static Material _material;
        private static float _noteAt;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type module = typeof(LookInputModule);
                Type self = typeof(HandUi);
                BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                BindingFlags mine = BindingFlags.Static | BindingFlags.Public;

                string[] asked = { "GetSubmitLeftButtonDown", "GetSubmitLeftButtonUp", "GetSubmitRightButtonDown", "GetSubmitRightButtonUp" };
                string[] answer = { "LeftDown", "LeftUp", "RightDown", "RightUp" };

                for (int i = 0; i < asked.Length; i++)
                {
                    MethodInfo m = module.GetMethod(asked[i], any, null, Type.EmptyTypes, null);

                    if (m == null)
                    {
                        throw new MissingMethodException("LookInputModule." + asked[i]);
                    }

                    harmony.Patch(m, null, new HarmonyMethod(self.GetMethod(answer[i], mine)));
                }

                MethodInfo menu = typeof(SuperController).GetMethod("GetMenuShow", any, null, Type.EmptyTypes, null);

                if (menu == null)
                {
                    throw new MissingMethodException("SuperController.GetMenuShow");
                }

                harmony.Patch(menu, null, new HarmonyMethod(self.GetMethod("MenuShow", mine)));

                Type sc = typeof(SuperController);
                _guiHit = new[] { sc.GetField("GUIhitLeft", any), sc.GetField("GUIhitRight", any) };
                _drawer = new[] { sc.GetField("rayLineDrawerLeft", any), sc.GetField("rayLineDrawerRight", any) };
                _look = new[] { module.GetField("lookData", any), module.GetField("lookDataRight", any) };
                _patched = true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Note = "menus by hand are off: " + ex.Message;

                if (Hooks.Warn != null)
                {
                    Hooks.Warn("hands: " + Note);
                }
            }
        }

        public static void LeftDown(ref bool __result)
        {
            __result |= _sides[0].Down;
        }

        public static void LeftUp(ref bool __result)
        {
            __result |= _sides[0].Up;
        }

        public static void RightDown(ref bool __result)
        {
            __result |= _sides[1].Down;
        }

        public static void RightUp(ref bool __result)
        {
            __result |= _sides[1].Up;
        }

        public static void MenuShow(ref bool __result)
        {
            __result |= _menu;
        }

        // Every frame, after HandDrive has decided whose each hand is. What is set here stands until
        // the next call, so VaM's own Update sees each press, release and menu call exactly once
        // whichever of the two runs first.
        internal static void Tick(float now)
        {
            SuperController sc = SuperController.singleton;
            bool on = _patched && !_failed && CfgOn != null && CfgOn.Value && sc != null && HandDrive.Active;
            _menu = false;

            try
            {
                for (int i = 0; i < 2; i++)
                {
                    Side s = _sides[i];
                    s.Down = s.Up = false;

                    if (on && HandDrive.Using(i) && sc.ViveRig != null)
                    {
                        Read(sc, i, s, now);
                    }
                    else
                    {
                        Release(sc, i, s);
                    }
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Note = "menus by hand are off: " + ex.GetType().Name + ": " + ex.Message;

                if (Hooks.Warn != null)
                {
                    Hooks.Warn("hands: " + Note + "\n" + ex.StackTrace);
                }

                for (int i = 0; i < 2 && sc != null; i++)
                {
                    try
                    {
                        Release(sc, i, _sides[i]);
                    }
                    catch (Exception)
                    {
                    }
                }

                return;
            }

            if (!_failed && now >= _noteAt)
            {
                _noteAt = now + 0.25f;
                Note = on ? "menus by hand: " + Say(0, "left") + ", " + Say(1, "right") : "";
            }
        }

        private static string Say(int i, string name)
        {
            Side s = _sides[i];

            return name + " " + (
                !s.Lent ? "-" :
                s.MenuPinch ? "menu" :
                s.Pinched ? "pinch" :
                s.Facing ? "palm" :
                "points") + (s.Lent ? " " + s.Gap.ToString("0.00") : "");
        }

        private static void Read(SuperController sc, int i, Side s, float now)
        {
            Vector3[] p = HandDrive.PointsOf(i);
            Transform room = sc.ViveRig;
            float size = (p[9] - p[0]).magnitude;
            Transform head = sc.centerCameraTarget != null ? sc.centerCameraTarget.transform : (Camera.main != null ? Camera.main.transform : null);

            if (size < 1e-4f || head == null)
            {
                Release(sc, i, s);
                return;
            }

            // The palm: turned to the face or not. (Out of the palm, as HandDrive has it.)
            Vector3 wrist = p[0];
            Vector3 normal = (Vector3.Cross(p[5] - wrist, p[9] - wrist) + Vector3.Cross(p[9] - wrist, p[13] - wrist) + Vector3.Cross(p[13] - wrist, p[17] - wrist)).normalized;

            if (i != 0)
            {
                normal = -normal;
            }

            float facing = Vector3.Dot(normal, (head.position - (wrist + p[9]) * 0.5f).normalized);
            s.Facing = facing > (s.Facing ? FaceOff : FaceOn);

            // The pinch, with a moment's patience either way.
            s.Gap = (p[4] - p[8]).magnitude / size;
            bool closed = s.Gap < (s.Pinched ? PinchOff : PinchOn);

            if (closed == s.Pinched)
            {
                s.Changing = false;
            }
            else if (!s.Changing)
            {
                s.Changing = true;
                s.ChangingSince = now;
            }
            else if (now - s.ChangingSince >= Settle)
            {
                s.Changing = false;
                s.Pinched = closed;

                if (closed)
                {
                    if (i == 0 && s.Facing)
                    {
                        s.MenuPinch = true;
                        _menu = true;
                    }
                    else
                    {
                        s.Down = true;
                    }
                }
                else if (s.MenuPinch)
                {
                    s.MenuPinch = false;
                }
                else
                {
                    s.Up = true;
                }
            }

            // Where it points: from the shoulder through the index knuckle. A pinch under way keeps
            // its pointer even if the palm turns, so a drag is not dropped half way.
            s.Pointing = !s.Facing || (s.Pinched && !s.MenuPinch);

            float scale = room.lossyScale.x;
            Vector3 up = room.up;
            Vector3 ahead = Vector3.ProjectOnPlane(head.forward, up);
            ahead = ahead.sqrMagnitude > 1e-6f ? ahead.normalized : head.forward;
            Vector3 across = Vector3.Cross(up, ahead);
            Vector3 shoulder = head.position + (across * (i == 0 ? -ShoulderOut : ShoulderOut) - up * ShoulderDown - ahead * ShoulderBack) * scale;
            Vector3 aim = (p[5] - shoulder).normalized;

            if (!s.HaveAim)
            {
                s.Origin = p[5];
                s.Aim = aim;
                s.HaveAim = true;
            }
            else
            {
                // Smoothed more the less it moves: steady on a button, quick across the panel.
                float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
                float tau = Mathf.Lerp(0.12f, 0.025f, Mathf.Clamp01(Vector3.Angle(s.Aim, aim) / 3f));
                float a = 1f - Mathf.Exp(-dt / tau);
                s.Aim = Vector3.Slerp(s.Aim, aim, a).normalized;
                s.Origin = Vector3.Lerp(s.Origin, p[5], a);
            }

            Lend(sc, i, s, room);
            s.Holder.transform.position = s.Origin;
            s.Holder.transform.rotation = Quaternion.LookRotation(s.Aim, up);

            if (s.Holder.activeSelf != s.Pointing)
            {
                s.Holder.SetActive(s.Pointing);
            }

            // The line from the hand to where it lands, only while it lands on something of VaM's.
            bool hit = s.Pointing && _guiHit[i] != null && (bool)_guiHit[i].GetValue(sc);

            if (s.Line.enabled != hit)
            {
                s.Line.enabled = hit;
            }

            if (hit)
            {
                float reach = 1.5f * scale;
                LookInputModule module = LookInputModule.singleton;
                PointerEventData data = module != null && _look[i] != null ? _look[i].GetValue(module) as PointerEventData : null;

                if (data != null && data.pointerCurrentRaycast.gameObject != null && data.pointerCurrentRaycast.distance > 0.01f)
                {
                    reach = Mathf.Min(data.pointerCurrentRaycast.distance, 20f * scale);
                }

                s.Line.SetPosition(1, new Vector3(0f, 0f, reach / Mathf.Max(scale, 1e-4f)));
                Color colour = s.Pinched ? new Color(1f, 1f, 1f, 0.95f) : (i == 0 ? new Color(0.35f, 0.65f, 1f, 0.55f) : new Color(1f, 0.6f, 0.2f, 0.55f));
                s.Line.startColor = colour;
                s.Line.endColor = colour;
            }
        }

        // VaM's pointer camera for this side becomes ours, and VaM's own pointer line (drawn from
        // the controller, which is lying somewhere) is put away.
        private static void Lend(SuperController sc, int i, Side s, Transform room)
        {
            if (s.Holder == null)
            {
                s.Holder = new GameObject("VwsHandPointer" + i);
                s.Holder.transform.SetParent(room, true);
                s.Camera = s.Holder.AddComponent<Camera>();
                s.Camera.enabled = false;

                if (_material == null)
                {
                    Shader shader = null;

                    foreach (string name in new[] { "Sprites/Default", "UI/Default", "Hidden/Internal-Colored", "Unlit/Color" })
                    {
                        shader = Shader.Find(name);

                        if (shader != null)
                        {
                            break;
                        }
                    }

                    if (shader == null)
                    {
                        throw new InvalidOperationException("no shader to draw the pointer with");
                    }

                    _material = new Material(shader);
                }

                GameObject line = new GameObject("line");
                line.transform.SetParent(s.Holder.transform, false);
                s.Line = line.AddComponent<LineRenderer>();
                s.Line.useWorldSpace = false;
                s.Line.sharedMaterial = _material;
                s.Line.positionCount = 2;
                s.Line.SetPosition(0, Vector3.zero);
                s.Line.SetPosition(1, Vector3.forward);
                s.Line.startWidth = s.Line.endWidth = 0.003f;
                s.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                s.Line.receiveShadows = false;
                s.Line.enabled = false;
            }

            if (s.Lent)
            {
                return;
            }

            Camera theirs = i == 0 ? sc.leftControllerCamera : sc.rightControllerCamera;

            if (theirs != null && theirs != s.Camera)
            {
                // as VaM's own pointer camera is set up, wherever it then stands
                s.Camera.CopyFrom(theirs);
                s.Camera.enabled = false;
            }

            s.VamCamera = theirs != s.Camera ? theirs : null;
            s.VamDrawer = _drawer[i] != null ? _drawer[i].GetValue(sc) : null;
            s.VamLine = i == 0 ? sc.rayLineLeft : sc.rayLineRight;

            if (i == 0)
            {
                sc.leftControllerCamera = s.Camera;
                sc.rayLineLeft = null;
            }
            else
            {
                sc.rightControllerCamera = s.Camera;
                sc.rayLineRight = null;
            }

            if (_drawer[i] != null)
            {
                _drawer[i].SetValue(sc, null);
            }

            if (s.VamLine != null)
            {
                s.VamLine.gameObject.SetActive(false);
            }

            s.Lent = true;

            if (Hooks.Info != null)
            {
                Hooks.Info("hands: the " + (i == 0 ? "left" : "right") + " hand has VaM's pointer");
            }
        }

        // The hand is not ours (any more): a pinch under way is let go, and VaM has its own back.
        private static void Release(SuperController sc, int i, Side s)
        {
            if (s.Pinched && !s.MenuPinch)
            {
                s.Up = true;
            }

            s.Pinched = s.Changing = s.MenuPinch = s.Facing = s.Pointing = s.HaveAim = false;

            if (s.Holder != null && s.Holder.activeSelf)
            {
                s.Holder.SetActive(false);
            }

            if (!s.Lent || sc == null)
            {
                return;
            }

            if (i == 0)
            {
                if (sc.leftControllerCamera == s.Camera)
                {
                    sc.leftControllerCamera = s.VamCamera;
                }

                sc.rayLineLeft = s.VamLine;
            }
            else
            {
                if (sc.rightControllerCamera == s.Camera)
                {
                    sc.rightControllerCamera = s.VamCamera;
                }

                sc.rayLineRight = s.VamLine;
            }

            if (_drawer[i] != null)
            {
                _drawer[i].SetValue(sc, s.VamDrawer);
            }

            s.VamCamera = null;
            s.VamLine = null;
            s.VamDrawer = null;
            s.Lent = false;

            if (Hooks.Info != null)
            {
                Hooks.Info("hands: the " + (i == 0 ? "left" : "right") + " controller has VaM's pointer back");
            }
        }
    }
}
