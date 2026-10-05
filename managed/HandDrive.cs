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
                            _local[p] = new Vector3(hands[f], hands[f + 1], -hands[f + 2]);
                        }

                        _close[side] = Closeness(_local);
                        _curlOk[side] = CurlsIn[i * 6] > 0.5f;

                        for (int k = 0; k < 5; k++)
                        {
                            _curl[side, k] = CurlsIn[i * 6 + 1 + k];
                        }

                        if (CfgPinch != null && CfgPinch.Value)
                        {
                            ClosePinch(_local);
                        }

                        for (int p = 0; p < Points; p++)
                        {
                            _points[side][p] = room.TransformPoint(_local[p]);
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

        internal static BepInEx.Configuration.ConfigEntry<bool> CfgPinch;
        internal static BepInEx.Configuration.ConfigEntry<float> CfgPinchClosed, CfgPinchOpen;
        private static readonly Vector3[] _local = new Vector3[Points];

        // The thumb's tip and the index finger's brought together when they nearly are.
        //
        // The tracker's fingertips stop short of each other: on a recording of a hand pinching,
        // the two tips it gives are one and a half to two centimetres apart at their closest, and
        // VaM's hand, built from them, never closes (with Mercury following they read 2.5 to 3.5
        // cm). So a gap under PinchClosed is taken for a pinch and closed, and between that and
        // PinchOpen it is closed in part, so that nothing jumps: each
        // of the two fingers is turned, whole, about its own knuckle (the thumb about its second
        // joint), far enough that its tip comes to where the two are to meet. Metres, in the
        // room, before the game's own scale.
        private static void ClosePinch(Vector3[] p)
        {
            float closed = CfgPinchClosed != null ? CfgPinchClosed.Value : 0.045f;
            float open = Mathf.Max(closed + 0.005f, CfgPinchOpen != null ? CfgPinchOpen.Value : 0.08f);
            float gap = (p[4] - p[8]).magnitude;

            if (gap >= open || gap < 1e-4f)
            {
                return;
            }

            float left = gap <= closed ? 0f : (gap - closed) / (open - closed);
            left = left * left * (3f - 2f * left);
            Vector3 middle = (p[4] + p[8]) * 0.5f;

            Turn(p, 2, 4, Vector3.Lerp(middle, p[4], left));
            Turn(p, 5, 8, Vector3.Lerp(middle, p[8], left));
        }

        // How nearly the tracker's thumb tip and index tip touch: 1 from PinchClosed down, 0 from
        // PinchOpen up.
        private static float Closeness(Vector3[] p)
        {
            float closed = CfgPinchClosed != null ? CfgPinchClosed.Value : 0.045f;
            float open = Mathf.Max(closed + 0.005f, CfgPinchOpen != null ? CfgPinchOpen.Value : 0.08f);
            float apart = Mathf.Clamp01(((p[4] - p[8]).magnitude - closed) / (open - closed));
            return 1f - apart * apart * (3f - 2f * apart);
        }

        // ---- VaM's own fingertips, brought together ------------------------------------------
        //
        // Closing the pinch in the tracker's points is not enough: VaM's hand is not built from
        // points but from an angle a joint, on fingers of its own lengths, and with the same
        // angles its fingertips end up somewhere else -- the wearer's thumb and index touch, VaM's
        // stay apart. So the gap is closed where it shows: while the tracker has a pinch, the
        // distance between the tips of VaM's own thumb and index finger is measured each frame,
        // and both are curled a little further for as long as that brings them nearer. Curl that
        // stops helping (the tips passing each other) is taken back to where it helped most.
        internal static BepInEx.Configuration.ConfigEntry<bool> CfgCurls;
        internal static readonly float[] CurlsIn = new float[12];
        private static readonly float[,] _curl = new float[2, 5];
        private static readonly bool[] _curlOk = new bool[2];
        private static readonly float[] _saidAt = new float[2];

        // Mercury's bend of a finger (radians, more bent = more negative) as a share of a fist:
        // what it reads for an open hand and for a fist, finger by finger, on recordings.
        private static readonly float[] CurlOpen = { 0.4f, 0.4f, 0.5f, 0.9f, 1.0f };
        private static readonly float[] CurlFist = { 1.2f, 1.6f, 2.2f, 2.0f, 2.1f };

        private static float Share(int side, int finger)
        {
            float s = Mathf.Clamp01((-_curl[side, finger] - CurlOpen[finger]) / (CurlFist[finger] - CurlOpen[finger]));
            // (a finger is mostly out or mostly in: the middle is where the number is least to be trusted)
            s = Mathf.Clamp01((s - 0.1f) / 0.8f);
            return s * s * (3f - 2f * s);
        }

        private static readonly float[] _close = new float[2];
        private static readonly float[] _extra = new float[2], _nearest = new float[] { 1e9f, 1e9f }, _nearestAt = new float[2];
        private static readonly bool[] _past = new bool[2], _added = new bool[2];
        private static readonly MeshVR.Hands.HandOutput[] _fingers = new MeshVR.Hands.HandOutput[2];
        private static bool _loopFailed;
        internal static string PinchNote = "";

        // After every Update has run: VaM has set its fingers from the hand's input by now.
        internal static void Late(float now)
        {
            if (_loopFailed)
            {
                return;
            }

            try
            {
                PinchNote = "";
                SuperController sc = SuperController.singleton;

                if (sc == null)
                {
                    return;
                }

                bool wanted = _active && CfgPinch != null && CfgPinch.Value;
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

                for (int side = 0; side < 2; side++)
                {
                    bool on = wanted && _use[side] && now - _seenAt[side] < 0.3f;

                    if (!on && !_added[side])
                    {
                        _extra[side] = 0f;
                        _curlOk[side] = false;
                        continue;
                    }

                    Transform hand = side == 0 ? sc.leftHand : sc.rightHand;

                    if (_fingers[side] == null || !_fingers[side].isActiveAndEnabled)
                    {
                        _fingers[side] = FindFingers(side, hand, now);
                    }

                    MeshVR.Hands.HandOutput f = _fingers[side];

                    if (f == null || f.indexProximal == null || f.indexMiddle == null || f.indexDistal == null || f.thumbProximal == null || f.thumbMiddle == null || f.thumbDistal == null)
                    {
                        _added[side] = false;

                        if (on && _close[side] > 0.5f)
                        {
                            PinchNote = "pinch: VaM's hand has no finger joints to curl (" + (hand == null ? "no hand" : hand.name) + ")";
                        }

                        continue;
                    }

                    // The tips: a joint's place is where its bone begins, so each tip is a bone's length past the last joint.
                    Vector3 indexTip = f.indexDistal.transform.position + (f.indexDistal.transform.position - f.indexMiddle.transform.position) * 0.75f;
                    Vector3 thumbTip = f.thumbDistal.transform.position + (f.thumbDistal.transform.position - f.thumbMiddle.transform.position) * 0.85f;
                    float bone = (f.indexMiddle.transform.position - f.indexProximal.transform.position).magnitude;

                    if (bone < 1e-6f)
                    {
                        continue;
                    }

                    // metres on a hand of ordinary size (whose index finger's first bone is 4 cm), whatever the game's scale
                    float apart = (indexTip - thumbTip).magnitude / bone * 0.04f;
                    float close = on ? _close[side] : 0f;

                    if (close < 0.02f)
                    {
                        _extra[side] = Mathf.MoveTowards(_extra[side], 0f, 150f * dt);
                        _past[side] = false;
                        _nearest[side] = 1e9f;
                    }
                    else
                    {
                        // touching when the pinch is closed, up to 5 cm apart as it opens
                        float error = apart - (0.006f + (1f - close) * 0.05f);

                        if (apart < _nearest[side] - 0.0005f)
                        {
                            _nearest[side] = apart;
                            _nearestAt[side] = _extra[side];
                        }

                        if (!_past[side] && error > 0f && _extra[side] > _nearestAt[side] + 10f && apart > _nearest[side] + 0.004f)
                        {
                            _past[side] = true;
                        }

                        if (_past[side])
                        {
                            _extra[side] = Mathf.MoveTowards(_extra[side], _nearestAt[side], 90f * dt);
                        }
                        else
                        {
                            _extra[side] = Mathf.Clamp(_extra[side] + Mathf.Clamp(error * 5000f, -150f, 150f) * dt, 0f, 45f);
                        }

                        if (close < 0.3f)
                        {
                            _past[side] = false;
                            _nearest[side] = 1e9f;
                        }

                        PinchNote += (PinchNote.Length != 0 ? "; " : "pinch: ") + (side == 0 ? "left" : "right") + " " + (apart * 1000f).ToString("0") + " mm between VaM's fingertips, +" +
                            _extra[side].ToString("0") + " deg" + (_past[side] ? " (no nearer with more)" : "");
                    }

                    float x = _extra[side];
                    bool left = side == 0;

                    // What VaM has each joint at from the hand's input (the tracker's points, through its Leap rig)...
                    float ip = left ? MeshVR.Hands.HandInput.leftIndexProximalBend : MeshVR.Hands.HandInput.rightIndexProximalBend;
                    float im = left ? MeshVR.Hands.HandInput.leftIndexMiddleBend : MeshVR.Hands.HandInput.rightIndexMiddleBend;
                    float id = left ? MeshVR.Hands.HandInput.leftIndexDistalBend : MeshVR.Hands.HandInput.rightIndexDistalBend;
                    float mp = left ? MeshVR.Hands.HandInput.leftMiddleProximalBend : MeshVR.Hands.HandInput.rightMiddleProximalBend;
                    float mm = left ? MeshVR.Hands.HandInput.leftMiddleMiddleBend : MeshVR.Hands.HandInput.rightMiddleMiddleBend;
                    float md = left ? MeshVR.Hands.HandInput.leftMiddleDistalBend : MeshVR.Hands.HandInput.rightMiddleDistalBend;
                    float rp = left ? MeshVR.Hands.HandInput.leftRingProximalBend : MeshVR.Hands.HandInput.rightRingProximalBend;
                    float rm = left ? MeshVR.Hands.HandInput.leftRingMiddleBend : MeshVR.Hands.HandInput.rightRingMiddleBend;
                    float rd = left ? MeshVR.Hands.HandInput.leftRingDistalBend : MeshVR.Hands.HandInput.rightRingDistalBend;
                    float pp = left ? MeshVR.Hands.HandInput.leftPinkyProximalBend : MeshVR.Hands.HandInput.rightPinkyProximalBend;
                    float pm = left ? MeshVR.Hands.HandInput.leftPinkyMiddleBend : MeshVR.Hands.HandInput.rightPinkyMiddleBend;
                    float pd = left ? MeshVR.Hands.HandInput.leftPinkyDistalBend : MeshVR.Hands.HandInput.rightPinkyDistalBend;
                    float tp = left ? MeshVR.Hands.HandInput.leftThumbProximalBend : MeshVR.Hands.HandInput.rightThumbProximalBend;
                    float tm = left ? MeshVR.Hands.HandInput.leftThumbMiddleBend : MeshVR.Hands.HandInput.rightThumbMiddleBend;
                    float td = left ? MeshVR.Hands.HandInput.leftThumbDistalBend : MeshVR.Hands.HandInput.rightThumbDistalBend;
                    bool byCurl = on && _curlOk[side] && CfgCurls != null && CfgCurls.Value;

                    // ...and, with Mercury, each finger bent as a whole by its share of a fist (VaM's
                    // own fist, from a controller's grip, is 100 at every joint; the thumb's 60).
                    if (byCurl)
                    {
                        float si = Share(side, 1), sm = Share(side, 2), sr = Share(side, 3), sp = Share(side, 4), st = Share(side, 0);
                        ip = 85f * si; im = 100f * si; id = 80f * si;
                        mp = 85f * sm; mm = 100f * sm; md = 80f * sm;
                        rp = 85f * sr; rm = 100f * sr; rd = 80f * sr;
                        pp = 85f * sp; pm = 100f * sp; pd = 80f * sp;
                        tm = 60f * st; td = 60f * st;
                    }

                    Bend(f.indexProximal, ip + x * 0.45f);
                    Bend(f.indexMiddle, im + x);
                    Bend(f.indexDistal, id + x * 0.6f);
                    Bend(f.thumbProximal, tp + x * 0.25f);
                    Bend(f.thumbMiddle, tm + x * 0.5f);
                    Bend(f.thumbDistal, td + x * 0.5f);

                    if (f.middleProximal != null && f.middleMiddle != null && f.middleDistal != null && f.ringProximal != null && f.ringMiddle != null && f.ringDistal != null &&
                        f.pinkyProximal != null && f.pinkyMiddle != null && f.pinkyDistal != null)
                    {
                        Bend(f.middleProximal, mp); Bend(f.middleMiddle, mm); Bend(f.middleDistal, md);
                        Bend(f.ringProximal, rp); Bend(f.ringMiddle, rm); Bend(f.ringDistal, rd);
                        Bend(f.pinkyProximal, pp); Bend(f.pinkyMiddle, pm); Bend(f.pinkyDistal, pd);
                    }

                    _added[side] = x > 0.01f || byCurl;

                    // For reading afterwards: there is no looking into a headset from outside.
                    if (on && now - _saidAt[side] > 2f)
                    {
                        _saidAt[side] = now;
                        Say((left ? "left" : "right") + " fingers: " + (byCurl ? "by curl" : "by points") +
                            (_curlOk[side] ? ", Mercury T " + _curl[side, 0].ToString("0.00") + " I " + _curl[side, 1].ToString("0.00") + " M " + _curl[side, 2].ToString("0.00") + " R " + _curl[side, 3].ToString("0.00") + " L " + _curl[side, 4].ToString("0.00") : ", no curls") +
                            "; index bends " + f.indexProximal.currentBend.ToString("0") + "/" + f.indexMiddle.currentBend.ToString("0") + "/" + f.indexDistal.currentBend.ToString("0") +
                            ", thumb " + f.thumbProximal.currentBend.ToString("0") + "/" + f.thumbMiddle.currentBend.ToString("0") + "/" + f.thumbDistal.currentBend.ToString("0") +
                            "; pinch " + close.ToString("0.00") + ", VaM's fingertips " + (apart * 1000f).ToString("0") + " mm apart, +" + x.ToString("0") + " deg" + (_past[side] ? " (no nearer with more)" : ""));
                    }
                }
            }
            catch (Exception ex)
            {
                _loopFailed = true;
                PinchNote = "";
                Say("VaM's fingertips are left as they are: closing the pinch on them failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        // The finger joints of the wearer's own hand model. They are not under the hand's transform:
        // the model is a body of its own, held to that transform by a joint (which is why a search
        // down from it found nothing, and neither the finger bends nor the pinch ever reached
        // VaM's hand -- a session's log had not one line from here). So every hand output in the
        // scene is looked at: the one for this side that is switched on and belongs to no atom
        // (a person's hands have them too). Looked for once a second at most while there is none.
        private static readonly float[] _soughtAt = { -100f, -100f };

        private static MeshVR.Hands.HandOutput FindFingers(int side, Transform hand, float now)
        {
            MeshVR.Hands.HandOutput found = hand != null ? hand.GetComponentInChildren<MeshVR.Hands.HandOutput>() : null;

            if (found != null || now - _soughtAt[side] < 1f)
            {
                return found;
            }

            _soughtAt[side] = now;
            MeshVR.Hands.HandOutput.Hand wanted = side == 0 ? MeshVR.Hands.HandOutput.Hand.Left : MeshVR.Hands.HandOutput.Hand.Right;
            int seen = 0;

            foreach (MeshVR.Hands.HandOutput output in UnityEngine.Object.FindObjectsOfType<MeshVR.Hands.HandOutput>())
            {
                seen++;

                if (output != null && output.hand == wanted && output.isActiveAndEnabled && output.GetComponentInParent<Atom>() == null)
                {
                    found = output;
                    break;
                }
            }

            Say((side == 0 ? "left" : "right") + " fingers: " + (found != null ? "VaM's hand model is '" + found.name + "'" :
                "no hand model with finger joints found for this side (" + seen + " hand outputs in the scene) -- VaM's fingers stay as its own input has them"));
            return found;
        }

        private static void Bend(MeshVR.Hands.FingerOutput finger, float bend)
        {
            if (Mathf.Abs(finger.currentBend - bend) > 0.01f)
            {
                finger.currentBend = bend;
                finger.UpdateOutput();
            }
        }

        // The joints after `root` up to `tip`, turned about `root` so that the tip points at `to`.
        private static void Turn(Vector3[] p, int root, int tip, Vector3 to)
        {
            Vector3 was = p[tip] - p[root], want = to - p[root];

            if (was.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f)
            {
                return;
            }

            Quaternion turn = Quaternion.FromToRotation(was, want);

            for (int i = root + 1; i <= tip; i++)
            {
                p[i] = p[root] + turn * (p[i] - p[root]);
            }
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
