// VaM DLSS - Model Resolution: the wearer's hands, from the headset's cameras.
//
// The native half finds them (vws_hand.h): 21 points a hand, in the room, from the same camera
// frames passthrough shows. This file switches that on, keeps the camera running for it when
// passthrough itself is off, and each frame reads where the hands are.
//
// For now the hands are only shown, as a skeleton of lines and dots in the scene, to judge the
// tracking by; they do not touch anything yet.

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace VamDlssNrWorkScale
{
    internal static class Hands
    {
        internal static ConfigEntry<bool> CfgOn, CfgShow, CfgSwap, CfgBoth;
        internal static ConfigEntry<int> CfgEvery;
        internal static ConfigEntry<float> CfgSmoothing, CfgQuick;

        internal static string Status = "";

        // The panel's button: the camera's next frame is saved beside the plugin, to try the tracker on.
        internal static bool CaptureWanted;
        private static string _saved = "";

        // The panel's other button: after a few seconds to put the controller down, the camera's
        // frames are saved for a while, eight a second, as they came (a byte a pixel, no header;
        // size.txt beside them says how large). To play back through the tracker afterwards.
        internal static bool RecordWanted;
        private const float RecordWait = 4f, RecordFor = 8f, RecordEvery = 0.125f;
        private static float _recordAt = -1f, _recordNext;
        private static int _recordCount;
        private static string _recordFolder;
        private static byte[] _recordFrame;

        private const int Count = 2, Points = 21, Floats = 4 + Points * 3;

        // The tracker's numbers (vwshand::Setting), the ones without a setting as vwshand::Defaults has them.
        private const int SPitch = 0, SReach = 1, SPalmMin = 2, SHandMin = 3, SGapMost = 4, SLookEvery = 5, SCutoff = 6, SBeta = 7, SSwap = 8, SHold = 9, SMost = 10, SBright = 11, SBrightNear = 12, SExposure = 15, SExposureNear = 16, SBrightLow = 17;
        private static readonly float[] _set = new float[24];
        private static readonly float[] _hands = new float[Count * Floats];

        // Wrist to each fingertip, then across the knuckles.
        private static readonly int[][] Chains =
        {
            new[] { 0, 1, 2, 3, 4 }, new[] { 0, 5, 6, 7, 8 }, new[] { 0, 9, 10, 11, 12 }, new[] { 0, 13, 14, 15, 16 }, new[] { 0, 17, 18, 19, 20 },
            new[] { 5, 9, 13, 17 },
        };

        private static bool _loaded, _failed, _on;
        private static float _statusAt;
        private static uint _serialThen;

        private sealed class Drawn
        {
            internal GameObject Root;
            internal LineRenderer[] Lines;
            internal Transform[] Dots;
        }

        private static readonly Drawn[] _drawn = new Drawn[Count];
        private static Material _material;
        private static Mesh _sphere;

        // From the plugin, every frame.
        internal static void Tick(float now)
        {
            if (CfgOn == null || !CfgOn.Value || !Native.Loaded || _failed)
            {
                HandDrive.Stop();

                if (_on)
                {
                    Native.HandConfigure(false, 2, null);
                    _on = false;
                    Hide();
                }

                if (!_failed)
                {
                    Status = "";
                }

                return;
            }

            if (!_loaded)
            {
                if (!Native.HandLoad())
                {
                    _failed = true;
                    Status = "hand tracking is off: " + Native.HandError();

                    if (Hooks.Warn != null)
                    {
                        Hooks.Warn(Status);
                    }

                    return;
                }

                _loaded = true;
            }

            _set[SPitch] = 0.35f;
            _set[SReach] = 1f;
            _set[SPalmMin] = 0.3f;
            _set[SHandMin] = 0.5f;
            _set[SGapMost] = 0.03f;
            _set[SLookEvery] = 3f;
            _set[SCutoff] = CfgSmoothing.Value;
            _set[SBeta] = CfgQuick.Value;
            _set[SSwap] = CfgSwap.Value ? 1f : 0f;
            _set[SHold] = 10f;
            _set[SMost] = CfgBoth.Value ? 2f : 1f;
            // By day and under a lamp at night: the palm finder looks at each picture at two
            // strengths, and a picture aimed at a hand is brought up by the hand itself.
            _set[SBright] = 0.7f;
            _set[SBrightLow] = 0.3f;
            _set[SBrightNear] = 0.6f;
            _set[SExposure] = 1f;
            _set[SExposureNear] = 1f;
            Native.HandConfigure(true, CfgEvery.Value, _set);
            _on = true;

            string problem = Passthrough.Hold(now);

            if (problem != null)
            {
                Status = "hand tracking: " + problem;
                Hide();
                HandDrive.Stop();
                return;
            }

            if (CaptureWanted)
            {
                CaptureWanted = false;
                string name = "hand-capture-" + DateTime.Now.ToString("HHmmss") + ".png";
                string folder = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                _saved = Passthrough.SaveCamera(System.IO.Path.Combine(folder, name)) ? "\nsaved " + name : "\nno camera frame to save yet";
                _statusAt = 0f;
            }

            Record(now);

            uint serial, micros, inRoom;
            float age;
            Native.HandRead(_hands, out serial, out micros, out age, out inRoom);

            // Frames that have stopped coming say nothing of where a hand is now.
            bool fresh = age >= 0f && age < 300f && inRoom != 0;

            // VaM's own hands, if they are to follow: a fault there takes only that down.
            try
            {
                HandDrive.Tick(now, _hands, fresh);
            }
            catch (Exception ex)
            {
                HandDrive.Fail(ex);
            }
            int live = 0;

            for (int i = 0; i < Count; i++)
            {
                bool shown = fresh && _hands[i * Floats] > 0.5f;
                live += shown ? 1 : 0;
                Draw(i, shown && CfgShow.Value);
            }

            if (now >= _statusAt)
            {
                float rate = _statusAt > 0f ? (serial - _serialThen) / Mathf.Max(0.001f, now - (_statusAt - 1f)) : 0f;
                _serialThen = serial;
                _statusAt = now + 1f;

                Status = "hand tracking: " + (
                    age < 0f ? "waiting for the camera's first frame" :
                    !fresh ? (inRoom == 0 ? "the camera's frames come without the head's place" : "the camera's frames have stopped") :
                    live == 0 ? "no hand in view" :
                    live + (live == 1 ? " hand (" + Side(0) + Side(1) + ")" : " hands")) +
                    ", " + rate.ToString("0") + " looks a second, " + (micros / 1000f).ToString("0") + " ms each" + (HandDrive.Note.Length != 0 ? "\n" + HandDrive.Note : "") + _saved;
            }
        }

        private static void Record(float now)
        {
            if (RecordWanted)
            {
                RecordWanted = false;
                _recordAt = now + RecordWait;
                _recordNext = _recordAt;
                _recordCount = 0;
                _recordFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "hand-record-" + DateTime.Now.ToString("HHmmss"));
            }

            if (_recordAt < 0f)
            {
                return;
            }

            if (now < _recordAt)
            {
                _saved = "\nrecording starts in " + Mathf.CeilToInt(_recordAt - now) + " s -- put the controllers down";
                _statusAt = 0f;
                return;
            }

            if (now >= _recordAt + RecordFor)
            {
                _saved = "\nrecorded " + _recordCount + " frames into " + System.IO.Path.GetFileName(_recordFolder);
                _recordAt = -1f;
                _statusAt = 0f;
                return;
            }

            if (now < _recordNext)
            {
                return;
            }

            _recordNext += RecordEvery;
            uint width, height;

            if (!Passthrough.ReadCamera(ref _recordFrame, out width, out height))
            {
                return;
            }

            if (_recordCount == 0)
            {
                System.IO.Directory.CreateDirectory(_recordFolder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(_recordFolder, "size.txt"), width + " " + height);
            }

            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_recordFolder, _recordCount.ToString("000") + ".gray"), _recordFrame);
            _recordCount++;
            _saved = "\nRECORDING " + Mathf.CeilToInt(_recordAt + RecordFor - now) + " s";
            _statusAt = 0f;
        }

        private static string Side(int i)
        {
            return _hands[i * Floats] > 0.5f ? (_hands[i * Floats + 1] > 0.5f ? "left" : "right") : "";
        }

        internal static void Stop()
        {
            HandDrive.Stop();

            if (_on && Native.Loaded)
            {
                Native.HandConfigure(false, 2, null);
            }

            _on = false;
        }

        private static void Hide()
        {
            for (int i = 0; i < Count; i++)
            {
                if (_drawn[i] != null && _drawn[i].Root != null && _drawn[i].Root.activeSelf)
                {
                    _drawn[i].Root.SetActive(false);
                }
            }
        }

        // The room SteamVR's poses are in, as the scene has it: the rig the headset and controllers
        // are placed in. Its z runs the other way.
        private static Transform Room()
        {
            SuperController sc = SuperController.singleton;
            return sc != null ? sc.ViveRig : null;
        }

        private static Drawn Make(Transform room, int index)
        {
            if (_material == null)
            {
                // Whichever of these the game was built with: all draw a plain colour.
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
                    throw new InvalidOperationException("no shader to draw the hands with");
                }

                _material = new Material(shader);
            }

            if (_sphere == null)
            {
                _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            }

            Drawn d = new Drawn();
            d.Root = new GameObject("VwsTrackedHand" + index);
            d.Root.transform.SetParent(room, false);
            d.Lines = new LineRenderer[Chains.Length];
            d.Dots = new Transform[Points];

            for (int c = 0; c < Chains.Length; c++)
            {
                GameObject go = new GameObject("chain" + c);
                go.transform.SetParent(d.Root.transform, false);
                LineRenderer line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.sharedMaterial = _material;
                line.positionCount = Chains[c].Length;
                line.startWidth = line.endWidth = 0.004f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                d.Lines[c] = line;
            }

            for (int p = 0; p < Points; p++)
            {
                // A mesh and a renderer, no collider: shown only, nothing in the scene is to feel it.
                GameObject dot = new GameObject("point" + p);
                dot.AddComponent<MeshFilter>().sharedMesh = _sphere;
                dot.AddComponent<MeshRenderer>();
                dot.transform.SetParent(d.Root.transform, false);
                // the fingertips a little larger
                dot.transform.localScale = Vector3.one * ((p != 0 && p % 4 == 0) ? 0.012f : 0.008f);
                Renderer renderer = dot.GetComponent<Renderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                d.Dots[p] = dot.transform;
            }

            return d;
        }

        private static void Draw(int index, bool shown)
        {
            Drawn d = _drawn[index];

            if (!shown)
            {
                if (d != null && d.Root != null && d.Root.activeSelf)
                {
                    d.Root.SetActive(false);
                }

                return;
            }

            Transform room = Room();

            if (room == null)
            {
                return;
            }

            // A scene change takes the drawn hand with it: made again.
            if (d == null || d.Root == null)
            {
                d = _drawn[index] = Make(room, index);
            }

            if (d.Root.transform.parent != room)
            {
                d.Root.transform.SetParent(room, false);
            }

            if (!d.Root.activeSelf)
            {
                d.Root.SetActive(true);
            }

            int at = index * Floats;
            bool left = _hands[at + 1] > 0.5f;
            Color colour = left ? new Color(0.2f, 0.8f, 1f, 1f) : new Color(1f, 0.6f, 0.1f, 1f);

            for (int c = 0; c < Chains.Length; c++)
            {
                LineRenderer line = d.Lines[c];
                line.startColor = line.endColor = colour;

                for (int k = 0; k < Chains[c].Length; k++)
                {
                    line.SetPosition(k, Point(at, Chains[c][k]));
                }
            }

            for (int p = 0; p < Points; p++)
            {
                d.Dots[p].localPosition = Point(at, p);
            }
        }

        // SteamVR's room is right-handed, Unity's left: z turns round.
        private static Vector3 Point(int at, int p)
        {
            int i = at + 4 + p * 3;
            return new Vector3(_hands[i], _hands[i + 1], -_hands[i + 2]);
        }

        internal static void Fail(Exception ex)
        {
            _failed = true;
            Stop();
            Hide();
            Status = "hand tracking is off: " + ex.GetType().Name + ": " + ex.Message;

            if (Hooks.Warn != null)
            {
                Hooks.Warn(Status + "\n" + ex.StackTrace);
            }
        }
    }
}
