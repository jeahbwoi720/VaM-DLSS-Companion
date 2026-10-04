// VaM DLSS - Model Resolution: VaM's own hands, moved by the tracked ones.
//
// VaM has had Leap Motion support all along: a pair of rigged hands (`Leap Rig`) that a
// HandModelManager poses from Leap frames, a mount on each that the wearer's physical hand model is
// hung on while Leap has that hand, and finger angles read off the rigged hand's bones. Nothing in
// it asks where the frames come from. So the tracked points are made into Leap frames -- a wrist,
// a palm's turn, a turn for each finger bone, in Unity's world -- and handed to that manager
// through a provider of our own; VaM does the rest as if a Leap sensor were plugged in.
//
// What VaM is not asked to do is decide who has a hand. Its own rule (CheckAutoConnectLeapHands)
// wants its Leap switch on, which would start a search for the Leap service and be written into
// the user's preferences; while this is driving, that rule is held off and the hand is hung on
// the Leap mount, or given back to the controller, from here:
//   Auto         a controller that is being moved (or squeezed) has its hand; a hand the cameras
//                see, whose controller lies still, is the cameras'
//   Hands        the cameras have every hand they see
//   Controllers  nothing of this runs

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using LArm = Leap.Arm;
using LBone = Leap.Bone;
using LFinger = Leap.Finger;
using LFrame = Leap.Frame;
using LHand = Leap.Hand;
using LQuaternion = Leap.LeapQuaternion;
using LVector = Leap.Vector;

namespace VamDlssNrWorkScale
{
    // What VaM's Leap hand models listen to, in place of the provider that asks the Leap service.
    internal sealed class HandFrames : Leap.Unity.LeapProvider
    {
        private LFrame _frame = new LFrame(0, 0, 60f, new List<LHand>());

        public override LFrame CurrentFrame
        {
            get { return _frame; }
        }

        public override LFrame CurrentFixedFrame
        {
            get { return _frame; }
        }

        internal void Send(LFrame frame)
        {
            _frame = frame;
            DispatchUpdateFrameEvent(frame);
        }
    }

    internal static class HandDrive
    {
        internal static ConfigEntry<int> CfgMode;

        // What is driving each hand, for the panel.
        internal static string Note = "";

        private const int Points = 21, Floats = 4 + Points * 3;
        private const float ForgetAfter = 0.35f;     // a hand the cameras lost keeps its last place this long
        private const float ControllerHolds = 1.0f;  // a controller that moved has its hand for this long after
        private const float MovedMetres = 0.015f, MovedDegrees = 6f, MovedWithin = 0.25f;

        private static bool _active, _failed;
        private static HandFrames _provider;
        private static Leap.Unity.HandModelManager _manager;
        private static Leap.Unity.LeapProvider _providerBefore;
        private static bool _rigBefore, _pinchBefore;
        private static MethodInfo[] _connect, _disconnect;
        private static FieldInfo[] _connected;

        private static readonly Vector3[][] _points = { new Vector3[Points], new Vector3[Points] };
        private static readonly float[] _seenAt = { -100f, -100f };
        private static readonly float[] _movedAt = { -100f, -100f };
        private static readonly Vector3[] _markPlace = new Vector3[2];
        private static readonly Quaternion[] _markTurn = { Quaternion.identity, Quaternion.identity };
        private static readonly float[] _markAt = new float[2];
        private static readonly bool[] _use = new bool[2];
        private static readonly LHand[] _hand = new LHand[2];
        private static readonly LFrame _frame = new LFrame(0, 0, 60f, new List<LHand>(2));
        private static long _frameId;
        private static readonly float[] _waitingSince = { -1f, -1f };
        private static bool _saidMissing;

        internal static bool Active
        {
            get { return _active; }
        }

        // Is this side's hand (0 left, 1 right) the cameras' at the moment, and where its 21 points
        // are in the scene. For what is built on the hands (HandUi.cs).
        internal static bool Using(int side)
        {
            return _active && _use[side];
        }

        internal static Vector3[] PointsOf(int side)
        {
            return _points[side];
        }

        internal static void Apply(Harmony harmony)
        {
            MethodInfo check = AccessTools.Method(typeof(SuperController), "CheckAutoConnectLeapHands");

            if (check == null)
            {
                _failed = true;
                Note = "this VaM has no Leap hand rule to take over";
                return;
            }

            try
            {
                harmony.Patch(check, new HarmonyMethod(typeof(HandDrive).GetMethod("AutoConnectPrefix", BindingFlags.Static | BindingFlags.Public)));
            }
            catch (Exception)
            {
                // without it VaM would take each hand back the frame after it was given
                _failed = true;
                Note = "VaM's Leap hand rule could not be held off";
                throw;
            }
        }

        // VaM's own rule for who has a hand: held off while this is deciding it.
        public static bool AutoConnectPrefix()
        {
            return !_active;
        }

        private static bool Start(SuperController sc)
        {
            _manager = sc.leapHandModelControl != null ? sc.leapHandModelControl.handModelManager : null;

            if (_manager == null || sc.LeapRig == null || sc.leftHand == null || sc.rightHand == null || sc.leapHandMountLeft == null || sc.leapHandMountRight == null || sc.ViveRig == null)
            {
                _failed = true;
                Note = "VaM's Leap hands are not where they are expected";
                return false;
            }

            if (_connect == null)
            {
                Type t = typeof(SuperController);
                _connect = new[] { AccessTools.Method(t, "ConnectLeapHandLeft"), AccessTools.Method(t, "ConnectLeapHandRight") };
                _disconnect = new[] { AccessTools.Method(t, "DisconnectLeapHandLeft"), AccessTools.Method(t, "DisconnectLeapHandRight") };
                _connected = new[] { AccessTools.Field(t, "_leapHandLeftConnected"), AccessTools.Field(t, "_leapHandRightConnected") };

                if (_connect[0] == null || _connect[1] == null || _disconnect[0] == null || _disconnect[1] == null || _connected[0] == null || _connected[1] == null)
                {
                    _failed = true;
                    Note = "this VaM's Leap hand methods are not the ones expected";
                    return false;
                }
            }

            if (_provider == null)
            {
                GameObject go = new GameObject("VwsHandFrames");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _provider = go.AddComponent<HandFrames>();
            }

            _providerBefore = _manager.leapProvider;
            _manager.leapProvider = _provider;
            _rigBefore = sc.LeapRig.gameObject.activeSelf;
            sc.LeapRig.gameObject.SetActive(true);

            // Pinching to grab comes with VaM's Leap hands; it is a later step here.
            _pinchBefore = sc.leapHandModelControl.allowPinchGrab;
            sc.leapHandModelControl.allowPinchGrab = false;

            _use[0] = _use[1] = false;
            _seenAt[0] = _seenAt[1] = -100f;
            _active = true;

            if (Hooks.Info != null)
            {
                Hooks.Info("hands: VaM's hand models are now driven through its Leap rig");
            }

            return true;
        }

        internal static void Stop()
        {
            if (!_active)
            {
                return;
            }

            SuperController sc = SuperController.singleton;

            try
            {
                // No hands: once for each, the manager lets one go a frame.
                _frame.Hands.Clear();
                _provider.Send(_frame);
                _provider.Send(_frame);

                if (sc != null)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        if ((bool)_connected[side].GetValue(sc))
                        {
                            _disconnect[side].Invoke(sc, null);
                        }
                    }

                    if (_manager != null && _manager.leapProvider == _provider)
                    {
                        _manager.leapProvider = _providerBefore;
                    }

                    if (sc.leapHandModelControl != null)
                    {
                        sc.leapHandModelControl.allowPinchGrab = _pinchBefore;
                    }

                    if (sc.LeapRig != null && !sc.leapMotionEnabled)
                    {
                        sc.LeapRig.gameObject.SetActive(_rigBefore);
                    }
                }
            }
            finally
            {
                // VaM's own rule again from the next frame.
                _active = false;
                _use[0] = _use[1] = false;
                Note = "";
            }
        }

        internal static void Fail(Exception ex)
        {
            try
            {
                Stop();
            }
            catch (Exception)
            {
                _active = false;
            }

            _failed = true;
            Note = "VaM's hands are left to the controllers: " + ex.GetType().Name + ": " + ex.Message;

            if (Hooks.Warn != null)
            {
                Hooks.Warn(Note + "\n" + ex.StackTrace);
            }
        }

        // From Hands, every frame hand tracking is on: `hands` as the tracker gave them (in SteamVR's
        // room), `fresh` whether they are of now.
        internal static void Tick(float now, float[] hands, bool fresh)
        {
            SuperController sc = SuperController.singleton;
            int mode = CfgMode != null ? CfgMode.Value : 0;

            if (mode == 0 || _failed || sc == null || !sc.isOpenVR)
            {
                Stop();
                return;
            }

            // A real Leap sensor in use: its hands, not ours.
            if (sc.leapMotionEnabled)
            {
                Stop();
                Note = "VaM's Leap Motion is switched on: the hands are left to it";
                return;
            }

            if (!_active && !Start(sc))
            {
                return;
            }

            // (the camera's object, switched on again, hands the manager back to Leap's own provider)
            if (_manager.leapProvider != _provider)
            {
                _manager.leapProvider = _provider;
            }

            Transform room = sc.ViveRig;
            _frame.Hands.Clear();

            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;

                for (int i = 0; fresh && i < 2; i++)
                {
                    int at = i * Floats;

                    if (hands[at] > 0.5f && (hands[at + 1] > 0.5f) == left)
                    {
                        for (int p = 0; p < Points; p++)
                        {
                            int f = at + 4 + p * 3;
                            // SteamVR's room is right-handed, Unity's left: z turns round
                            _points[side][p] = room.TransformPoint(new Vector3(hands[f], hands[f + 1], -hands[f + 2]));
                        }

                        _seenAt[side] = now;
                    }
                }

                // Is this side's controller in use? Moved by more than its own tremor lately, or squeezed.
                Transform controller = left ? sc.viveObjectLeft : sc.viveObjectRight;

                if (controller != null && controller.gameObject.activeInHierarchy)
                {
                    Vector3 place = room.InverseTransformPoint(controller.position);
                    Quaternion turn = Quaternion.Inverse(room.rotation) * controller.rotation;

                    if ((place - _markPlace[side]).magnitude > MovedMetres || Quaternion.Angle(turn, _markTurn[side]) > MovedDegrees ||
                        (left ? sc.GetLeftGrabVal() : sc.GetRightGrabVal()) > 0.1f)
                    {
                        _movedAt[side] = now;
                        _markPlace[side] = place;
                        _markTurn[side] = turn;
                        _markAt[side] = now;
                    }
                    else if (now - _markAt[side] > MovedWithin)
                    {
                        _markPlace[side] = place;
                        _markTurn[side] = turn;
                        _markAt[side] = now;
                    }
                }

                bool seen = now - _seenAt[side] < ForgetAfter;
                bool held = mode == 1 && now - _movedAt[side] < ControllerHolds;
                _use[side] = seen && !held;

                if (_use[side])
                {
                    if (_hand[side] == null)
                    {
                        _hand[side] = Make(left);
                    }

                    Fill(_hand[side], _points[side], left);
                    _frame.Hands.Add(_hand[side]);
                }
            }

            _frame.Id = ++_frameId;
            _frame.Timestamp = (long)(now * 1e6);

            for (int i = 0; i < _frame.Hands.Count; i++)
            {
                _frame.Hands[i].FrameId = _frameId;
            }

            _provider.Send(_frame);

            // The physical hand goes onto the Leap rig's mount once that hand is there, and back to
            // the controller when it is no longer ours.
            for (int side = 0; side < 2; side++)
            {
                bool connected = (bool)_connected[side].GetValue(sc);
                Transform mount = side == 0 ? sc.leapHandMountLeft : sc.leapHandMountRight;

                if (_use[side] && !connected && mount.gameObject.activeInHierarchy)
                {
                    _connect[side].Invoke(sc, null);
                    Say((side == 0 ? "left" : "right") + " hand to the cameras");
                }
                else if (!_use[side] && connected)
                {
                    _disconnect[side].Invoke(sc, null);
                    Say((side == 0 ? "left" : "right") + " hand back to its controller");
                }

                // A hand that is ours and whose Leap model does not come up: said once, with what
                // the manager holds, since nothing in the headset would show why.
                if (_use[side] && !connected && !mount.gameObject.activeInHierarchy)
                {
                    if (_waitingSince[side] < 0f)
                    {
                        _waitingSince[side] = now;
                    }
                    else if (!_saidMissing && now - _waitingSince[side] > 1.5f)
                    {
                        _saidMissing = true;

                        if (Hooks.Warn != null)
                        {
                            Hooks.Warn("hands: VaM's Leap hand model does not come up for the " + (side == 0 ? "left" : "right") + " hand -- " + Describe(sc));
                        }
                    }
                }
                else
                {
                    _waitingSince[side] = -1f;
                }
            }

            Note = "VaM's hands: left by " + (_use[0] ? "the cameras" : "its controller") + ", right by " + (_use[1] ? "the cameras" : "its controller");
        }

        private static void Say(string what)
        {
            if (Hooks.Info != null)
            {
                Hooks.Info("hands: " + what);
            }
        }

        // What VaM's Leap side looks like, for the log.
        private static string Describe(SuperController sc)
        {
            string said = "rig " + (sc.LeapRig.gameObject.activeInHierarchy ? "on" : "off") + ", manager " + (_manager.isActiveAndEnabled ? "on" : "off") +
                ", its provider " + (_manager.leapProvider == _provider ? "ours" : "not ours") +
                ", models " + (sc.leapHandLeft != null && sc.leapHandLeft.gameObject.activeInHierarchy ? "L on" : "L off") + " " +
                (sc.leapHandRight != null && sc.leapHandRight.gameObject.activeInHierarchy ? "R on" : "R off");

            try
            {
                System.Collections.IList pool = AccessTools.Field(typeof(Leap.Unity.HandModelManager), "ModelPool").GetValue(_manager) as System.Collections.IList;
                said += ", groups " + (pool != null ? pool.Count : -1);

                for (int i = 0; pool != null && i < pool.Count; i++)
                {
                    Leap.Unity.HandModelManager.ModelGroup g = pool[i] as Leap.Unity.HandModelManager.ModelGroup;

                    if (g != null)
                    {
                        said += " [" + g.GroupName + (g.IsEnabled ? "" : " off") + ": " + g.modelList.Count + " waiting, " + g.modelsCheckedOut.Count + " out, L " +
                            (g.LeftModel != null ? g.LeftModel.name : "none") + ", R " + (g.RightModel != null ? g.RightModel.name : "none") + "]";
                    }
                }
            }
            catch (Exception ex)
            {
                said += ", pool unreadable (" + ex.GetType().Name + ")";
            }

            return said;
        }

        // ---- a Leap hand from 21 points ----

        private static LHand Make(bool left)
        {
            List<LFinger> fingers = new List<LFinger>(5);
            int id = left ? 1 : 2;

            for (int f = 0; f < 5; f++)
            {
                LFinger finger = new LFinger();
                finger.Type = (LFinger.FingerType)f;
                finger.Id = id * 10 + f;
                finger.HandId = id;
                finger.Width = 0.016f;
                finger.IsExtended = true;

                for (int b = 0; b < 4; b++)
                {
                    LBone bone = new LBone();
                    bone.Type = (LBone.BoneType)b;
                    bone.Width = 0.016f;
                    finger.bones[b] = bone;
                }

                fingers.Add(finger);
            }

            LHand hand = new LHand();
            hand.Id = id;
            hand.IsLeft = left;
            hand.Confidence = 1f;
            hand.Fingers = fingers;
            hand.Arm = new LArm();
            hand.Arm.Width = 0.06f;
            return hand;
        }

        private static LVector V(Vector3 v)
        {
            return new LVector(v.x, v.y, v.z);
        }

        private static LQuaternion Q(Quaternion q)
        {
            return new LQuaternion(q.x, q.y, q.z, q.w);
        }

        private static void SetBone(LBone bone, Vector3 from, Vector3 to, Quaternion turn)
        {
            Vector3 along = to - from;
            float length = along.magnitude;
            bone.PrevJoint = V(from);
            bone.NextJoint = V(to);
            bone.Center = V((from + to) * 0.5f);
            bone.Length = length;
            bone.Direction = V(length > 1e-6f ? along / length : turn * Vector3.forward);
            bone.Rotation = Q(turn);
        }

        // The tracker's points (wrist 0; thumb 1-4; index 5-8; middle 9-12; ring 13-16; little
        // 17-20; in Unity's world) as Leap describes a hand there: every turn is "forward along the
        // bone, up out of the back of the hand".
        private static void Fill(LHand hand, Vector3[] p, bool left)
        {
            Vector3 wrist = p[0];
            Vector3 along = (p[9] - wrist).normalized;

            // Out of the palm: across the knuckles one way for a left hand, the other for a right. From
            // the fan of all four knuckles, not two: a point a centimetre out in depth tilts it less.
            Vector3 normal = (Vector3.Cross(p[5] - wrist, p[9] - wrist) + Vector3.Cross(p[9] - wrist, p[13] - wrist) + Vector3.Cross(p[13] - wrist, p[17] - wrist)).normalized;

            if (!left)
            {
                normal = -normal;
            }

            Vector3 back = -normal;
            Quaternion palm = Quaternion.LookRotation(along, back);
            Vector3 side = palm * Vector3.right;

            hand.PalmPosition = hand.StabilizedPalmPosition = V((wrist + p[9]) * 0.5f);
            hand.PalmNormal = V(normal);
            hand.Direction = V(palm * Vector3.forward);
            hand.Rotation = Q(palm);
            hand.WristPosition = V(wrist);
            hand.PalmWidth = (p[5] - p[17]).magnitude * 1.3f;
            hand.PinchDistance = (p[4] - p[8]).magnitude * 1000f;
            hand.PinchStrength = Mathf.Clamp01(1f - (p[4] - p[8]).magnitude / 0.08f);
            hand.TimeVisible += Time.unscaledDeltaTime;

            SetBone(hand.Arm, wrist - along * 0.25f, wrist, palm);

            // The thumb's own "up" is not the hand's: it faces out to the thumb's side (the same
            // lean as in Leap's own model hand).
            Vector3 thumbSide = left ? side : -side;
            Vector3 thumbUp = (0.805f * thumbSide + 0.447f * (palm * Vector3.up) - 0.390f * (palm * Vector3.forward)).normalized;

            for (int f = 0; f < 5; f++)
            {
                LFinger finger = hand.Fingers[f];
                int first = 1 + f * 4;

                for (int b = 0; b < 4; b++)
                {
                    Vector3 from, to;

                    if (f == 0)
                    {
                        // Leap's thumb has no first bone: its other three are the thumb's three.
                        from = b == 0 ? p[1] : p[b];
                        to = b == 0 ? p[1] : p[b + 1];
                    }
                    else
                    {
                        from = b == 0 ? wrist : p[first + b - 1];
                        to = p[first + b];
                    }

                    Vector3 d = to - from;
                    Quaternion turn;

                    if (d.sqrMagnitude < 1e-10f)
                    {
                        Vector3 next = p[2] - p[1];
                        turn = next.sqrMagnitude > 1e-10f ? Quaternion.LookRotation(next, thumbUp) : palm;
                    }
                    else if (f == 0)
                    {
                        turn = Quaternion.LookRotation(d, thumbUp);
                    }
                    else
                    {
                        // a finger bends about the hand's side-to-side: up is whatever is square to that and the bone
                        turn = Quaternion.LookRotation(d, Vector3.Cross(d, side));
                    }

                    SetBone(finger.bones[b], from, to, turn);
                }

                Vector3 tip = p[first + 3];
                finger.TipPosition = V(tip);
                finger.Direction = finger.bones[3].Direction;
                finger.Length = finger.bones[1].Length + finger.bones[2].Length + finger.bones[3].Length;
                finger.TimeVisible = hand.TimeVisible;
                Vector3 d1 = p[first + 1] - p[first], d3 = tip - p[first + 2];
                finger.IsExtended = Vector3.Angle(d1, d3) < 50f;
            }
        }
    }
}
