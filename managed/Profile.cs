// VaM DLSS - Model Resolution: where the main thread's time goes.
//
// VaM is, more often than not, held back by one thread of the processor and not by the graphics
// card, and its own monitor says little about why: its "physics" is everything between the first
// FixedUpdate of a frame and the first Update -- every script that has a FixedUpdate as well as
// the physics engine, times however many physics steps the frame needed -- and its "scripts" is
// the rest up to rendering.
//
// For half a minute, on request, this takes the frame apart:
//
//   - every MonoBehaviour's FixedUpdate, Update, LateUpdate and render callbacks (VaM's, the
//     plugins' and the scene scripts' alike) are given a stopwatch, by name;
//   - the frame's phases are marked off -- each physics step from its first FixedUpdate to the
//     point where Unity has finished the step (a WaitForFixedUpdate), Update, the engine's work
//     between Update and LateUpdate, LateUpdate, the engine's work before rendering, rendering as
//     far as the main thread does it, and the gap to the next frame's first script;
//   - what a phase took beyond its scripts is the engine's own: in a physics step that is the
//     physics engine;
//   - and beside each phase's length, how much of it this thread was at work (by the processor's
//     own count of the thread's cycles): a phase that is long and idle is the thread WAITING --
//     for the graphics card, the headset's next frame, or its own helper threads -- and no
//     quicker processor would shorten it.
//
// It all goes to the log, every ten seconds and by frame: nothing is drawn. The stopwatches are
// taken off again at the end. While it runs it costs something itself (a quarter of a microsecond
// a call or so): the calls are counted beside the times so that this can be judged.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VamDlssNrWorkScale
{
    internal static class Profile
    {
        internal static ConfigEntry<int> CfgSeconds, CfgStartAfter;
        internal static ConfigEntry<bool> CfgScripts, CfgPlugins, CfgTry;
        internal static ConfigEntry<string> CfgInside;

        // For the panel's status box: what is going on, or nothing.
        internal static string Line = "";

        // ---- the sums, and what is said of them (plain numbers: the host test runs this) --------

        internal const int Fixed = 0, Update = 1, Late = 2, Render = 3;

        // Not a phase: a method timed inside one of the scripts above, to see what that script's time is made of.
        internal const int Inside = 4;

        internal static readonly string[] PhaseNames = { "FixedUpdate", "Update", "LateUpdate", "render" };

        internal sealed class Slot
        {
            internal string Name;
            internal int Phase;
            internal long Ticks;
            internal int Calls;
        }

        // What ten seconds of frames came to. Times in the stopwatch's ticks.
        internal sealed class Window
        {
            internal int Frames, Steps, Collections;
            internal long All, Worst;
            internal long Wait;        // from the end of a frame to the next one's first script
            internal long FixedSpan;   // the physics steps, each from its first FixedUpdate to the step's end
            internal long UpdateSpan, Between, LateSpan, BeforeRender, RenderSpan;
            internal readonly long[] Scripts = new long[4]; // the stopwatches' sum, by phase
            // this thread's own cycles over the same spans: Wait, FixedSpan, UpdateSpan, Between, LateSpan, BeforeRender, RenderSpan
            internal readonly long[] Busy = new long[7];
            internal readonly float[] Vam = new float[4];   // VaM's own monitor, summed: physics, scripts, render, total

            internal void Clear()
            {
                Frames = Steps = Collections = 0;
                All = Worst = Wait = FixedSpan = UpdateSpan = Between = LateSpan = BeforeRender = RenderSpan = 0;
                Array.Clear(Scripts, 0, Scripts.Length);
                Array.Clear(Busy, 0, Busy.Length);
                Array.Clear(Vam, 0, Vam.Length);
            }

            private static string Ms(double ms)
            {
                return ms.ToString("0.00").PadLeft(7) + " ms";
            }

            // A window in one line: what a trial is compared by.
            internal string Short(double ticksPerMs, double cyclesPerMs)
            {
                if (Frames <= 0 || ticksPerMs <= 0.0)
                {
                    return "no frames were seen";
                }

                double frame = All / (Frames * ticksPerMs);
                long busy = 0;

                foreach (long b in Busy)
                {
                    busy += b;
                }

                return (1000.0 / frame).ToString("0.0") + " fps, " + frame.ToString("0.00") + " ms a frame" + (cyclesPerMs > 0.0 ? " (at work " + (busy / (Frames * cyclesPerMs)).ToString("0.00") + ")" : "") +
                    ", the longest " + (Worst / ticksPerMs).ToString("0.0") +
                    "; " + (Steps / (double)Frames).ToString("0.00") + " physics steps a frame" +
                    (Steps > 0 ? ", " + (FixedSpan / (Steps * ticksPerMs)).ToString("0.00") + " ms each (scripts " + (Scripts[Fixed] / (Steps * ticksPerMs)).ToString("0.00") + ", engine " +
                        ((FixedSpan - Scripts[Fixed]) / (Steps * ticksPerMs)).ToString("0.00") + ")" : "");
            }

            // The lines for the log. `slots` are sorted here by what they took; `most` of them are named.
            // `cyclesPerMs`: what a millisecond of this thread at work is in its cycles (0: not known,
            // and nothing is said of it).
            internal List<string> Describe(double ticksPerMs, List<Slot> slots, int most, double cyclesPerMs)
            {
                List<string> lines = new List<string>();

                if (Frames <= 0 || ticksPerMs <= 0.0)
                {
                    lines.Add("no frames were seen");
                    return lines;
                }

                double per = 1.0 / (Frames * ticksPerMs);
                double frame = All * per, steps = Steps / (double)Frames;
                double perCycle = cyclesPerMs > 0.0 ? 1.0 / (Frames * cyclesPerMs) : 0.0;
                string[] work = new string[7];
                long busy = 0;

                for (int i = 0; i < 7; i++)
                {
                    work[i] = perCycle > 0.0 ? "  [at work " + (Busy[i] * perCycle).ToString("0.00") + "]" : "";
                    busy += Busy[i];
                }
                double fixedAll = FixedSpan * per, fixedScripts = Scripts[Fixed] * per;
                lines.Add(Frames + " frames: " + (1000.0 / frame).ToString("0.0") + " fps, " + frame.ToString("0.00") + " ms a frame (the longest " + (Worst / ticksPerMs).ToString("0.0") + ")" +
                    (perCycle > 0.0 ? "; this thread at work " + (busy * perCycle).ToString("0.00") + " ms of it, waiting the rest" : ""));
                lines.Add(Ms(fixedAll) + "  physics steps: " + steps.ToString("0.00") + " a frame" +
                    (Steps > 0 ? ", " + (FixedSpan / (Steps * ticksPerMs)).ToString("0.00") + " ms each -- scripts' FixedUpdate " + (Scripts[Fixed] / (Steps * ticksPerMs)).ToString("0.00") +
                        ", the physics engine and the rest " + ((FixedSpan - Scripts[Fixed]) / (Steps * ticksPerMs)).ToString("0.00") : "") +
                    (Steps > 0 ? "  [a frame: scripts " + fixedScripts.ToString("0.00") + ", engine " + (fixedAll - fixedScripts).ToString("0.00") + "]" : "") + work[1]);
                lines.Add(Ms(UpdateSpan * per) + "  Update (scripts " + (Scripts[Update] * per).ToString("0.00") + ")" + work[2]);
                lines.Add(Ms(Between * per) + "  the engine between Update and LateUpdate (animation, coroutines)" + work[3]);
                lines.Add(Ms(LateSpan * per) + "  LateUpdate (scripts " + (Scripts[Late] * per).ToString("0.00") + ")" + work[4]);
                lines.Add(Ms(BeforeRender * per) + "  the engine before rendering (where it waits for the graphics card to take the last frame)" + work[5]);
                lines.Add(Ms(RenderSpan * per) + "  rendering on this thread (scripts' render callbacks and OnGUI " + (Scripts[Render] * per).ToString("0.00") + ")" + work[6]);
                lines.Add(Ms(Wait * per) + "  between frames (presenting, waiting for the headset or the screen, the engine's start of frame)" + work[0]);

                double named = (FixedSpan + UpdateSpan + Between + LateSpan + BeforeRender + RenderSpan + Wait) * per;
                lines.Add(Ms(frame - named) + "  not accounted for");
                lines.Add("garbage collections: " + Collections + "; VaM's own monitor over the same frames: physics " + (Vam[0] / Frames).ToString("0.00") + ", scripts " + (Vam[1] / Frames).ToString("0.00") +
                    ", render " + (Vam[2] / Frames).ToString("0.00") + ", total " + (Vam[3] / Frames).ToString("0.00") + " ms");

                if (slots != null && slots.Count != 0)
                {
                    List<Slot> sorted = new List<Slot>(slots);
                    sorted.Sort(delegate(Slot a, Slot b) { return b.Ticks.CompareTo(a.Ticks); });
                    lines.Add("scripts, by what they take of a frame (calls a frame beside them):");

                    for (int i = 0; i < sorted.Count && i < most; i++)
                    {
                        Slot s = sorted[i];

                        if (s.Ticks <= 0)
                        {
                            break;
                        }

                        lines.Add(Ms(s.Ticks * per) + "  " + PhaseNames[s.Phase].PadRight(11) + " " + s.Name + "  (" + (s.Calls / (double)Frames).ToString("0.#") + ")");
                    }
                }

                return lines;
            }
        }

        // ---- the stopwatches --------------------------------------------------------------------

        private static readonly Dictionary<MethodBase, Slot> _slots = new Dictionary<MethodBase, Slot>();
        private static readonly List<Slot> _all = new List<Slot>();
        private static readonly Window _window = new Window();
        private static Harmony _harmony;
        private static int _mainThread;
        private static bool _measuring;

        // the frame in hand, in ticks (0: not reached yet)
        private static long _frameStart, _first, _stepStart, _updateStart, _afterUpdate, _lateStart, _lateEnd, _cullStart;
        // ...and this thread's cycles at the same marks
        private static long _frameStartC, _firstC, _stepStartC, _updateStartC, _afterUpdateC, _lateStartC, _lateEndC, _cullStartC;
        private static double _cyclesPerMs;

        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        private static bool _noCycles;

        // The cycles this thread has run for, as Windows counts them: time it spent waiting is not in them.
        private static long Cycles()
        {
            if (_noCycles)
            {
                return 0;
            }

            try
            {
                ulong cycles;
                return QueryThreadCycleTime(GetCurrentThread(), out cycles) ? (long)cycles : 0;
            }
            catch (Exception)
            {
                _noCycles = true;
                return 0;
            }
        }

        // What a millisecond at work is in cycles: this thread kept busy for a fiftieth of a second, once a run.
        private static double CyclesPerMs()
        {
            long ticks = Stopwatch.GetTimestamp(), cycles = Cycles(), until = ticks + Stopwatch.Frequency / 50, now;

            do
            {
                now = Stopwatch.GetTimestamp();
            }
            while (now < until);

            long ran = Cycles() - cycles;
            return cycles != 0 && ran > 0 ? ran / ((now - ticks) * 1000.0 / Stopwatch.Frequency) : 0.0;
        }
        private static bool _inStep;
        private static int _frameSteps;

        private static bool Mine(out long now)
        {
            now = 0;

            if (!_measuring || Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                return false;
            }

            now = Stopwatch.GetTimestamp();
            return true;
        }

        public static void BeforeFixed(out long __state)
        {
            if (!Mine(out __state))
            {
                return;
            }

            if (!_inStep)
            {
                _inStep = true;
                _stepStart = __state;
                _stepStartC = Cycles();
                _frameSteps++;

                if (_first == 0)
                {
                    _first = __state;
                    _firstC = _stepStartC;
                }
            }
        }

        public static void BeforeUpdate(out long __state)
        {
            if (!Mine(out __state))
            {
                return;
            }

            if (_updateStart == 0)
            {
                EndStep(__state);
                _updateStart = __state;
                _updateStartC = Cycles();

                if (_first == 0)
                {
                    _first = __state;
                    _firstC = _updateStartC;
                }
            }
        }

        public static void BeforeLate(out long __state)
        {
            if (Mine(out __state) && _lateStart == 0)
            {
                _lateStart = __state;
                _lateStartC = Cycles();
            }
        }

        public static void BeforeRender(out long __state)
        {
            Mine(out __state);
        }

        public static void After(MethodBase __originalMethod, long __state)
        {
            if (__state == 0 || !_measuring)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            Slot slot;

            if (__originalMethod == null || !_slots.TryGetValue(__originalMethod, out slot))
            {
                return;
            }

            long took = now - __state;
            slot.Ticks += took;
            slot.Calls++;

            if (slot.Phase == Inside)
            {
                return;
            }

            _window.Scripts[slot.Phase] += took;

            if (slot.Phase == Late)
            {
                _lateEnd = now;
                _lateEndC = Cycles();
            }
        }

        private static void EndStep(long now)
        {
            if (_inStep)
            {
                _inStep = false;
                _window.FixedSpan += now - _stepStart;
                _window.Busy[1] += Math.Max(0, Cycles() - _stepStartC);
            }
        }

        private static void OnCull(Camera camera)
        {
            if (_measuring && _cullStart == 0)
            {
                _cullStart = Stopwatch.GetTimestamp();
                _cullStartC = Cycles();
            }
        }

        // ---- the marks Unity gives -------------------------------------------------------------

        private static readonly WaitForFixedUpdate AfterStep = new WaitForFixedUpdate();
        private static readonly WaitForEndOfFrame AfterFrame = new WaitForEndOfFrame();

        private static IEnumerator Steps()
        {
            while (_measuring)
            {
                yield return AfterStep;
                EndStep(Stopwatch.GetTimestamp());
            }
        }

        private static IEnumerator Updates()
        {
            while (_measuring)
            {
                yield return null;

                if (_updateStart != 0 && _afterUpdate == 0)
                {
                    _afterUpdate = Stopwatch.GetTimestamp();
                    _afterUpdateC = Cycles();
                }
            }
        }

        private static readonly FieldInfo[] VamMonitor = new FieldInfo[4];
        private static int _collections;

        private static IEnumerator Frames()
        {
            while (_measuring)
            {
                yield return AfterFrame;
                SpanTo(0);
                long now = Stopwatch.GetTimestamp(), nowC = Cycles();

                // A frame is counted when all of it was seen: from the end of the one before.
                if (_frameStart != 0 && _first != 0 && _updateStart != 0)
                {
                    Window w = _window;
                    EndStep(_updateStart);
                    long afterUpdate = _afterUpdate != 0 ? _afterUpdate : _updateStart;
                    long lateStart = _lateStart != 0 ? _lateStart : afterUpdate;
                    long lateEnd = _lateEnd > lateStart ? _lateEnd : lateStart;
                    long cull = _cullStart > lateEnd ? _cullStart : lateEnd;
                    w.Frames++;
                    w.Steps += _frameSteps;
                    w.All += now - _frameStart;
                    w.Worst = Math.Max(w.Worst, now - _frameStart);
                    w.Wait += _first - _frameStart;
                    w.UpdateSpan += afterUpdate - _updateStart;
                    w.Between += lateStart - afterUpdate;
                    w.LateSpan += lateEnd - lateStart;
                    w.BeforeRender += cull - lateEnd;
                    w.RenderSpan += now - cull;

                    // The same spans in this thread's cycles; a mark that was not reached takes the one before it.
                    long afterUpdateC = _afterUpdate != 0 ? _afterUpdateC : _updateStartC;
                    long lateStartC = _lateStart != 0 ? _lateStartC : afterUpdateC;
                    long lateEndC = _lateEnd > lateStart ? _lateEndC : lateStartC;
                    long cullC = _cullStart > lateEnd ? _cullStartC : lateEndC;
                    w.Busy[0] += Math.Max(0, _firstC - _frameStartC);
                    w.Busy[2] += Math.Max(0, afterUpdateC - _updateStartC);
                    w.Busy[3] += Math.Max(0, lateStartC - afterUpdateC);
                    w.Busy[4] += Math.Max(0, lateEndC - lateStartC);
                    w.Busy[5] += Math.Max(0, cullC - lateEndC);
                    w.Busy[6] += Math.Max(0, nowC - cullC);

                    for (int i = 0; i < 4; i++)
                    {
                        if (VamMonitor[i] != null)
                        {
                            w.Vam[i] += (float)VamMonitor[i].GetValue(null);
                        }
                    }

                    int collections = GC.CollectionCount(0);
                    w.Collections += collections - _collections;
                    _collections = collections;
                    ReadMarkers();
                }

                _frameStart = now;
                _frameStartC = nowC;
                _first = _stepStart = _updateStart = _afterUpdate = _lateStart = _lateEnd = _cullStart = 0;
                _inStep = false;
                _frameSteps = 0;
            }
        }

        // ---- inside the engine -------------------------------------------------------------------

        // Unity times its own work under these names; where the player still has them (a release
        // player has few or none) they say what a physics step is made of.
        private static readonly string[] MarkerNames =
        {
            "FixedUpdate.PhysicsFixedUpdate", "Physics.Simulate", "Physics.Processing", "Physics.ProcessingCloth", "Physics.FetchResults", "Physics.UpdateBodies", "Physics.ProcessReports",
            "Physics.TriggerEnterExits", "Physics.TriggerStays", "Physics.Contacts", "Physics.Interpolation", "Physics.UpdateCloth", "Physics.SyncColliderTransform", "Physics.SyncRigidbodyTransform",
            "Physics.UpdateJoints", "Physics.JointBreaks", "Physics.Raycast", "FixedBehaviourUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate", "FixedUpdate.DirectorFixedUpdate",
            "BehaviourUpdate", "LateBehaviourUpdate", "Animators.Update", "Director.Update", "MeshSkinning.Update", "PostLateUpdate.UpdateAllSkinnedMeshes", "PostLateUpdate.UpdateAllRenderers",
            "Camera.Render", "Culling", "Render.OpaqueGeometry", "Render.TransparentGeometry", "Shadows.RenderShadowMap", "Canvas.SendWillRenderCanvases", "UGUI.Rendering.UpdateBatches",
            "Gfx.WaitForPresent", "WaitForTargetFPS", "XR.WaitForGPU", "VR.WaitForGPU", "GC.Collect", "Loading.UpdatePreloading", "Particles.Update"
        };

        private static readonly List<UnityEngine.Profiling.Recorder> _recorders = new List<UnityEngine.Profiling.Recorder>();
        private static readonly List<string> _recorderNames = new List<string>();
        private static long[] _markerNs = new long[0];
        private static long[] _markerCalls = new long[0];

        private static void OpenMarkers()
        {
            _recorders.Clear();
            _recorderNames.Clear();

            foreach (string name in MarkerNames)
            {
                try
                {
                    UnityEngine.Profiling.Recorder recorder = UnityEngine.Profiling.Recorder.Get(name);

                    if (recorder != null && recorder.isValid)
                    {
                        recorder.enabled = true;
                        _recorders.Add(recorder);
                        _recorderNames.Add(name);
                    }
                }
                catch (Exception)
                {
                }
            }

            _markerNs = new long[_recorders.Count];
            _markerCalls = new long[_recorders.Count];
            Say(_recorders.Count == 0 ? "Unity's own timings: this player has none of them" : "Unity's own timings: " + _recorders.Count + " of " + MarkerNames.Length + " are there (" + string.Join(", ", _recorderNames.ToArray()) + ")");
        }

        private static void CloseMarkers()
        {
            foreach (UnityEngine.Profiling.Recorder recorder in _recorders)
            {
                try
                {
                    recorder.enabled = false;
                }
                catch (Exception)
                {
                }
            }

            _recorders.Clear();
            _recorderNames.Clear();
        }

        // What the frame before was, by Unity's own count (read at each frame's end).
        private static void ReadMarkers()
        {
            for (int i = 0; i < _recorders.Count; i++)
            {
                _markerNs[i] += _recorders[i].elapsedNanoseconds;
                _markerCalls[i] += _recorders[i].sampleBlockCount;
            }
        }

        private static void SayMarkers(int frames)
        {
            if (_recorders.Count == 0 || frames <= 0)
            {
                return;
            }

            List<int> order = new List<int>();

            for (int i = 0; i < _recorders.Count; i++)
            {
                if (_markerNs[i] > 0)
                {
                    order.Add(i);
                }
            }

            order.Sort(delegate(int a, int b) { return _markerNs[b].CompareTo(_markerNs[a]); });
            StringBuilder sb = new StringBuilder("Unity's own timings, a frame (they overlap: one may hold another): ");

            for (int n = 0; n < order.Count; n++)
            {
                int i = order[n];
                sb.Append(n != 0 ? "; " : "").Append(_recorderNames[i]).Append(' ').Append((_markerNs[i] / (frames * 1e6)).ToString("0.00")).Append(" ms (").Append((_markerCalls[i] / (double)frames).ToString("0.#")).Append(')');
            }

            Say(order.Count != 0 ? sb.ToString() : "Unity's own timings: all of them read nothing");
            Array.Clear(_markerNs, 0, _markerNs.Length);
            Array.Clear(_markerCalls, 0, _markerCalls.Length);
        }

        // What the physics engine has to work on: counted once, at the start of a run.
        private static Rigidbody[] _bodies = new Rigidbody[0];

        private static string Census()
        {
            StringBuilder sb = new StringBuilder();

            try
            {
                _bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                int moving = 0, awake = 0, most = 0, mostVelocity = 0;
                long sum = 0;
                Dictionary<int, int> byIterations = new Dictionary<int, int>();

                foreach (Rigidbody body in _bodies)
                {
                    if (body.isKinematic)
                    {
                        continue;
                    }

                    moving++;
                    awake += body.IsSleeping() ? 0 : 1;
                    most = Math.Max(most, body.solverIterations);
                    mostVelocity = Math.Max(mostVelocity, body.solverVelocityIterations);
                    sum += body.solverIterations;
                    int had;
                    byIterations.TryGetValue(body.solverIterations, out had);
                    byIterations[body.solverIterations] = had + 1;
                }

                Joint[] joints = UnityEngine.Object.FindObjectsOfType<Joint>();
                int configurable = 0;

                foreach (Joint joint in joints)
                {
                    configurable += joint is ConfigurableJoint ? 1 : 0;
                }

                Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
                int on = 0, triggers = 0, meshes = 0;

                foreach (Collider collider in colliders)
                {
                    if (!collider.enabled)
                    {
                        continue;
                    }

                    on++;
                    triggers += collider.isTrigger ? 1 : 0;
                    meshes += collider is MeshCollider ? 1 : 0;
                }

                sb.Append(_bodies.Length).Append(" rigidbodies, ").Append(moving).Append(" of them moved by the engine (").Append(awake).Append(" awake); solver iterations ");
                List<int> kinds = new List<int>(byIterations.Keys);
                kinds.Sort();

                foreach (int kind in kinds)
                {
                    sb.Append(kind).Append(" on ").Append(byIterations[kind]).Append(", ");
                }

                sb.Append("the most ").Append(most).Append('/').Append(mostVelocity).Append(" (the engine's default ").Append(Physics.defaultSolverIterations).Append('/').Append(Physics.defaultSolverVelocityIterations).Append("); ");
                sb.Append(joints.Length).Append(" joints (").Append(configurable).Append(" configurable); ").Append(on).Append(" colliders switched on (").Append(triggers).Append(" triggers, ").Append(meshes).Append(" mesh)");
                sb.Append("; transforms synced automatically: ").Append(Physics.autoSyncTransforms ? "yes" : "no");
            }
            catch (Exception ex)
            {
                sb.Append(" (").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(')');
            }

            return sb.ToString();
        }

        // ---- trials: the same scene with one thing changed, for a few seconds each -----------------

        // A session's log had the same scene, untouched, at 64 fps in one trial and at 39 in another:
        // with a physics step of 13.9 ms the frame either holds one step (and a step is then the
        // cheaper, 7 ms against 10) or it needs nearly two, takes 25 ms for it, and so goes on
        // needing two. Which of the two it is in depends on what went before -- so every changed
        // trial stands between two unchanged ones, and one trial holds the frame to one step.
        private static int[] _bodyIterations = new int[0];
        private static int _defaultIterations;
        private static float _mostWas;
        private static bool _tried;

        private static void Iterations(float share)
        {
            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i] != null)
                {
                    _bodies[i].solverIterations = Mathf.Max(1, Mathf.RoundToInt(_bodyIterations[i] * share));
                }
            }

            Physics.defaultSolverIterations = Mathf.Max(1, Mathf.RoundToInt(_defaultIterations * share));
        }

        private static void TrialsBegin()
        {
            _bodyIterations = new int[_bodies.Length];

            for (int i = 0; i < _bodies.Length; i++)
            {
                _bodyIterations[i] = _bodies[i] != null ? _bodies[i].solverIterations : 0;
            }

            _defaultIterations = Physics.defaultSolverIterations;
            _mostWas = Time.maximumDeltaTime;
            _tried = true;
        }

        // Everything a trial changed, as it was. Safe to call twice.
        private static void TrialsEnd()
        {
            if (!_tried)
            {
                return;
            }

            _tried = false;

            try
            {
                MenuPointers.Trial = false;
                Iterations(1f);
                Physics.defaultSolverIterations = _defaultIterations;
                Time.maximumDeltaTime = _mostWas;
            }
            catch (Exception ex)
            {
                Say("a trial's change could not be put back (" + ex.GetType().Name + ": " + ex.Message + "): restart VaM to be sure of the physics");
            }
        }

        private static void Trial(int which)
        {
            Iterations(which == 3 ? 0.5f : 1f);
            MenuPointers.Trial = which == 5;

            // (what Unity may make up for in one frame: one step's worth, and the scene runs a little
            // slow where the frame is longer than a step, instead of taking a second step for it)
            Time.maximumDeltaTime = which == 1 ? Time.fixedDeltaTime : _mostWas;
        }

        private static readonly string[] TrialNames =
        {
            "as it is", "at most one physics step a frame", "as it is again", "solver iterations at a half", "as it is once more",
            "the menu pointers resting while not on a menu", "as it is, the last time"
        };

        private static void ClearWindow()
        {
            _window.Clear();

            foreach (Slot slot in _all)
            {
                slot.Ticks = 0;
                slot.Calls = 0;
            }

            foreach (Slot slot in _inside)
            {
                slot.Ticks = 0;
                slot.Calls = 0;
            }

            Array.Clear(_markerNs, 0, _markerNs.Length);
            Array.Clear(_markerCalls, 0, _markerCalls.Length);
        }

        // ---- the graphics card's side of the frame -------------------------------------------------
        //
        // Five spans that follow one another and together are the frame, as the card comes to them:
        // 0 from the frame's end to the scene camera's rendering (what the game sends before it:
        //   skinning, hair and cloth on the card, shadows of other cameras);
        // 1 the scene camera's own rendering, up to its image effects;
        // 2 the image effects that run before VaM DLSS (post-processing);
        // 3 VaM DLSS's work: DLSS, Neural Rendering, and this plugin's passes inside it;
        // 4 whatever comes after it, to the frame's end (the interface, other cameras, passthrough).
        // The card's waiting is in them too: a span is from one mark to the next on the card's clock.
        internal static bool Gpu;
        private static readonly string[] SpanNames =
        {
            "before the scene's camera (skinning, hair, cloth, what other cameras draw)", "the scene's camera", "image effects before VaM DLSS (post-processing)",
            "VaM DLSS's own work (DLSS and Neural Rendering)", "after VaM DLSS (interface, other cameras, passthrough)"
        };
        private static readonly ulong[] _spanMicros = new ulong[8];
        private static readonly uint[] _spanTimes = new uint[8];
        private static int _spanAt = -1; // which span the frame is in, as marked so far

        // The frame moves on to span `to`: the one it was in ends, that one begins.
        internal static void SpanTo(int to)
        {
            if (!Gpu || to == _spanAt)
            {
                return;
            }

            if (_spanAt >= 0)
            {
                Native.Span(_spanAt, false);
            }

            _spanAt = to;
            Native.Span(to, true);
        }

        private static void OnSceneBegin(Camera camera)
        {
            if (Gpu && (object)camera == HeadsetUi.SceneCamera)
            {
                SpanTo(1);
            }
        }

        private static void OnSceneEnd(Camera camera)
        {
            if (Gpu && (object)camera == HeadsetUi.SceneCamera && _spanAt == 1)
            {
                SpanTo(2);
            }
        }

        private static void SaySpans()
        {
            Native.Spans(_spanMicros, _spanTimes, true);
            StringBuilder sb = new StringBuilder("the graphics card's time, a frame, by its own clock (its waiting for the game is in it): ");
            double all = 0;
            bool any = false;

            for (int i = 0; i < SpanNames.Length; i++)
            {
                if (_spanTimes[i] == 0)
                {
                    continue;
                }

                double ms = _spanMicros[i] / (_spanTimes[i] * 1000.0);
                all += ms;
                sb.Append(any ? "; " : "").Append(SpanNames[i]).Append(' ').Append(ms.ToString("0.00")).Append(" ms");
                any = true;
            }

            Say(any ? sb.Append("; together ").Append(all.ToString("0.00")).Append(" ms (").Append(_spanTimes[1]).Append(" frames read)").ToString() : "the graphics card's time: nothing was read (VaM DLSS's camera was not seen)");
        }

        // ---- a run ------------------------------------------------------------------------------

        private static readonly string[] FixedNames = { "FixedUpdate" };
        private static readonly string[] UpdateNames = { "Update" };
        private static readonly string[] LateNames = { "LateUpdate" };
        private static readonly string[] RenderNames = { "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnRenderObject", "OnWillRenderObject", "OnGUI" };

        private static bool _running;
        private static float _startAt = -1f;

        internal static bool Running
        {
            get { return _running; }
        }

        // The panel's button, or the setting that starts a run by itself.
        internal static void Start()
        {
            if (_running || WorkScalePlugin.Instance == null)
            {
                return;
            }

            _running = true;
            WorkScalePlugin.Instance.StartCoroutine(Run());
        }

        internal static void Tick(float now)
        {
            if (_startAt < 0f)
            {
                _startAt = CfgStartAfter != null && CfgStartAfter.Value > 0 ? now + CfgStartAfter.Value : float.MaxValue;
            }

            if (now >= _startAt)
            {
                _startAt = float.MaxValue;
                Start();
            }

            // A run that has stopped without its end (its coroutine died) leaves nothing changed behind.
            if (_tried && !_running)
            {
                TrialsEnd();
            }
        }

        private static int PhaseOf(string name)
        {
            return name == "FixedUpdate" ? Fixed : (name == "Update" ? Update : (name == "LateUpdate" ? Late : (Array.IndexOf(RenderNames, name) >= 0 ? Render : -1)));
        }

        // Assemblies made in memory by Reflection.Emit are left alone altogether: VaM's script
        // compiler leaves them behind (the scripts themselves run from a copy loaded from bytes),
        // and this Mono dies -- the process with it -- when asked for the body of a method in one.
        private static bool Ours(Assembly assembly)
        {
            if (assembly is System.Reflection.Emit.AssemblyBuilder || assembly.ManifestModule is System.Reflection.Emit.ModuleBuilder)
            {
                return false;
            }

            string name = assembly.GetName().Name;

            foreach (string skip in new[] { "UnityEngine", "Unity.", "System", "mscorlib", "Mono.", "BepInEx", "0Harmony", "MonoMod", "HarmonyX", "netstandard", "Microsoft." })
            {
                if (name.StartsWith(skip, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        // Whether an assembly was loaded from bytes and not from a file: the scripts of scene and
        // session plugins, which VaM compiles as it loads them.
        private static bool FromMemory(Assembly assembly)
        {
            try
            {
                return string.IsNullOrEmpty(assembly.Location);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static int _left;
        private static readonly List<string> _leftNames = new List<string>();

        private static List<MethodInfo> Find(bool plugins)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            const MethodImplAttributes noBody = MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native | MethodImplAttributes.Unmanaged;
            _left = 0;
            _leftNames.Clear();
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                bool memory = false;

                try
                {
                    if (!Ours(assembly))
                    {
                        continue;
                    }

                    memory = FromMemory(assembly);
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    try
                    {
                        if (type == null || !type.IsClass || type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type))
                        {
                            continue;
                        }

                        foreach (MethodInfo method in type.GetMethods(declared))
                        {
                            if (PhaseOf(method.Name) < 0 || method.IsAbstract || method.IsGenericMethod || method.ReturnType != typeof(void) ||
                                (method.GetMethodImplementationFlags() & noBody) != 0 || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                            {
                                continue;
                            }

                            int parameters = method.GetParameters().Length;

                            if (parameters != 0 && !(method.Name == "OnRenderImage" && parameters == 2))
                            {
                                continue;
                            }

                            if (memory && !plugins)
                            {
                                _left++;

                                if (_leftNames.Count < 12 && !_leftNames.Contains(type.Name))
                                {
                                    _leftNames.Add(type.Name);
                                }

                                continue;
                            }

                            found.Add(method);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return found;
        }

        // One class taken apart: its methods whose names say they are a step of the frame's work
        // (VaM's SuperController.Update is a list of calls to its own Process..., Check..., Sync...
        // and Prep... methods, and in a headset it was 3.4 ms of the frame against 1.1 on the
        // monitor). Each is timed with what it calls in it, so the times overlap where one calls another.
        private static readonly string[] StepNames = { "Process", "Check", "Sync", "Prep", "Verify", "Handle", "Apply" };
        private static readonly List<Slot> _inside = new List<Slot>();

        private static Type InsideType(string name)
        {
            Type type = typeof(SuperController).Assembly.GetType(name, false);

            if (type == null && name.IndexOf('.') >= 0)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(name, false);

                    if (type != null)
                    {
                        break;
                    }
                }
            }

            return type;
        }

        // `names`: classes and single methods ('Class.Method'), with commas between.
        private static List<MethodInfo> FindInside(string names)
        {
            List<MethodInfo> found = new List<MethodInfo>();

            foreach (string entry in (names ?? "").Split(','))
            {
                if (entry.Trim().Length != 0)
                {
                    FindInside(entry.Trim(), found);
                }
            }

            return found;
        }

        private static void FindInside(string typeName, List<MethodInfo> found)
        {
            try
            {
                Type type = InsideType(typeName);
                string only = null; // the one method asked for by name: whatever it is called and returns

                if (type == null && typeName.LastIndexOf('.') > 0)
                {
                    only = typeName.Substring(typeName.LastIndexOf('.') + 1);
                    type = InsideType(typeName.Substring(0, typeName.LastIndexOf('.')));
                }

                if (type == null || type.ContainsGenericParameters)
                {
                    Say("there is no class or method '" + typeName + "' to look inside ([Profile] LookInside)");
                    return;
                }

                const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                const MethodImplAttributes noBody = MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native | MethodImplAttributes.Unmanaged;

                foreach (MethodInfo method in type.GetMethods(declared))
                {
                    if (method.IsAbstract || method.IsGenericMethod || (only == null && method.ReturnType != typeof(void)) || PhaseOf(method.Name) >= 0 ||
                        (method.GetMethodImplementationFlags() & noBody) != 0 || (method.Attributes & MethodAttributes.PinvokeImpl) != 0 || found.Contains(method))
                    {
                        continue;
                    }

                    if (only != null)
                    {
                        if (method.Name == only)
                        {
                            found.Add(method);
                        }

                        continue;
                    }

                    foreach (string step in StepNames)
                    {
                        if (method.Name.StartsWith(step, StringComparison.Ordinal))
                        {
                            found.Add(method);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Say("looking inside '" + typeName + "' failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SayInside(int frames, double ticksPerMs)
        {
            if (_inside.Count == 0 || frames <= 0)
            {
                return;
            }

            List<Slot> sorted = new List<Slot>(_inside);
            sorted.Sort(delegate(Slot a, Slot b) { return b.Ticks.CompareTo(a.Ticks); });
            Say("inside " + (CfgInside != null ? CfgInside.Value : "") + ", a frame (each with what it calls in it, so they overlap; in brackets the calls a frame):");

            for (int i = 0; i < sorted.Count && i < 24; i++)
            {
                Slot s = sorted[i];
                double ms = s.Ticks / (frames * ticksPerMs);

                if (ms < 0.02)
                {
                    break;
                }

                Say(ms.ToString("0.00").PadLeft(7) + " ms  " + s.Name + "  (" + (s.Calls / (double)frames).ToString("0.#") + ")");
            }
        }

        private static string Setting()
        {
            StringBuilder sb = new StringBuilder();

            try
            {
                sb.Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount).Append(" threads); ");
                sb.Append(UnityEngine.XR.XRSettings.enabled ? "headset (" + UnityEngine.XR.XRSettings.loadedDeviceName + ", " + UnityEngine.XR.XRDevice.refreshRate.ToString("0") + " Hz)" : "monitor");
                sb.Append("; physics step ").Append((Time.fixedDeltaTime * 1000f).ToString("0.00")).Append(" ms (").Append((1f / Time.fixedDeltaTime).ToString("0")).Append(" Hz), at most ")
                    .Append((Time.maximumDeltaTime * 1000f).ToString("0.0")).Append(" ms of them a frame; solver iterations ").Append(Physics.defaultSolverIterations).Append('/').Append(Physics.defaultSolverVelocityIterations);
                sb.Append("; vSync ").Append(QualitySettings.vSyncCount).Append(", frame rate asked ").Append(Application.targetFrameRate);

                UserPreferences prefs = UserPreferences.singleton;

                if (prefs != null)
                {
                    sb.Append("; VaM: physics rate ").Append(prefs.physicsRate).Append(", update cap ").Append(prefs.physicsUpdateCap).Append(", high quality physics ").Append(prefs.physicsHighQuality ? "on" : "off")
                        .Append(", soft body physics ").Append(prefs.softPhysics ? "on" : "off");
                }

                if (SuperController.singleton != null)
                {
                    int atoms = 0, people = 0;

                    foreach (Atom atom in SuperController.singleton.GetAtoms())
                    {
                        atoms++;
                        people += atom != null && atom.type == "Person" ? 1 : 0;
                    }

                    sb.Append("; scene: ").Append(atoms).Append(" atoms, ").Append(people).Append(people == 1 ? " person" : " people");
                }
            }
            catch (Exception ex)
            {
                sb.Append(" (").Append(ex.GetType().Name).Append(')');
            }

            return sb.ToString();
        }

        private static IEnumerator Run()
        {
            int seconds = Mathf.Clamp(CfgSeconds != null ? CfgSeconds.Value : 30, 5, 600);
            bool scripts = CfgScripts == null || CfgScripts.Value;
            bool trials = CfgTry == null || CfgTry.Value;
            Say("a run of " + seconds + " s" + (trials ? ", then " + TrialNames.Length + " trials of 5 s each" : "") + ". " + Setting());
            Say("the physics engine's load: " + Census());
            OpenMarkers();
            Line = "profiling: getting ready";
            yield return AfterFrame;

            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _harmony = new Harmony(WorkScalePlugin.Guid + ".profile");
            _slots.Clear();
            _all.Clear();
            Type me = typeof(Profile);
            const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;
            HarmonyMethod[] before =
            {
                new HarmonyMethod(me.GetMethod("BeforeFixed", pub)), new HarmonyMethod(me.GetMethod("BeforeUpdate", pub)),
                new HarmonyMethod(me.GetMethod("BeforeLate", pub)), new HarmonyMethod(me.GetMethod("BeforeRender", pub))
            };
            HarmonyMethod after = new HarmonyMethod(me.GetMethod("After", pub));

            // The stopwatches go on a few at a time: putting one on takes a millisecond or so, and
            // there are hundreds. Without the per-script ones, only our own plugin's are put on --
            // enough for the phases' marks if something else of the kind runs first, which the
            // marks say (see "not accounted for").
            List<MethodInfo> methods = Find(CfgPlugins == null || CfgPlugins.Value);
            int failed = 0;
            long spent = Stopwatch.GetTimestamp();

            foreach (MethodInfo method in methods)
            {
                if (!scripts && method.DeclaringType != typeof(MeshVR.PerfMonPre) && method.DeclaringType != typeof(MeshVR.PerfMon) && method.DeclaringType != typeof(WorkScalePlugin))
                {
                    continue;
                }

                try
                {
                    int phase = PhaseOf(method.Name);
                    Slot slot = new Slot();
                    slot.Phase = phase;
                    slot.Name = (method.DeclaringType.Assembly.GetName().Name == "Assembly-CSharp" ? "" : method.DeclaringType.Assembly.GetName().Name + ": ") + method.DeclaringType.FullName +
                        (phase == Render ? "." + method.Name : "");
                    _harmony.Patch(method, before[phase], after);
                    _slots[method] = slot;
                    _all.Add(slot);
                }
                catch (Exception)
                {
                    failed++;
                }

                if (Stopwatch.GetTimestamp() - spent > Stopwatch.Frequency / 40)
                {
                    Line = "profiling: getting ready (" + _all.Count + " of " + methods.Count + ")";
                    yield return null;
                    spent = Stopwatch.GetTimestamp();
                }
            }

            // ...and the steps inside the one class asked about.
            _inside.Clear();

            if (scripts)
            {
                foreach (MethodInfo method in FindInside(CfgInside != null ? CfgInside.Value : "SuperController"))
                {
                    try
                    {
                        Slot slot = new Slot();
                        slot.Phase = Inside;
                        slot.Name = method.DeclaringType.Name + "." + method.Name + (method.ReturnType != typeof(void) || method.DeclaringType.Assembly != typeof(SuperController).Assembly ? "/" + method.GetParameters().Length : "");
                        _harmony.Patch(method, before[Render], after);
                        _slots[method] = slot;
                        _inside.Add(slot);
                    }
                    catch (Exception)
                    {
                        failed++;
                    }

                    if (Stopwatch.GetTimestamp() - spent > Stopwatch.Frequency / 40)
                    {
                        Line = "profiling: getting ready (inside " + _inside.Count + ")";
                        yield return null;
                        spent = Stopwatch.GetTimestamp();
                    }
                }
            }

            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            string[] monitor = { "physicsTime", "scriptsTime", "renderTime", "totalTime" };

            for (int i = 0; i < 4; i++)
            {
                FieldInfo field = typeof(MeshVR.PerfMon).GetField(monitor[i], any);
                VamMonitor[i] = field != null && field.FieldType == typeof(float) ? field : null;
            }

            Say(_all.Count + " script callbacks are being timed" + (_inside.Count != 0 ? ", and " + _inside.Count + " steps inside " + (CfgInside != null ? CfgInside.Value : "SuperController") : "") + (failed != 0 ? " (" + failed + " could not be)" : "") + (scripts ? "" : " -- only the marks: [Profile] Scripts is off") +
                (_left != 0 ? "; " + _left + " of scene and session plugins are not ([Profile] ScenePlugins is off) and count as the engine's: " + string.Join(", ", _leftNames.ToArray()) : ""));

            _cyclesPerMs = CyclesPerMs();
            Say(MenuPointers.Scene());
            _window.Clear();
            _frameStart = 0;
            _collections = GC.CollectionCount(0);
            _measuring = true;
            Camera.onPreCull += OnCull;
            Camera.onPreRender += OnSceneBegin;
            Camera.onPostRender += OnSceneEnd;
            Native.Spans(_spanMicros, _spanTimes, true);
            _spanAt = -1;
            Gpu = true;
            MonoBehaviour host = WorkScalePlugin.Instance;
            host.StartCoroutine(Steps());
            host.StartCoroutine(Updates());
            host.StartCoroutine(Frames());

            float started = Time.unscaledTime, said = started;
            double ticksPerMs = Stopwatch.Frequency / 1000.0;

            while (Time.unscaledTime - started < seconds)
            {
                yield return null;
                float now = Time.unscaledTime;
                Line = "profiling the main thread: " + Mathf.CeilToInt(seconds - (now - started)) + " s left (to the log)";

                if (now - said >= 10f || now - started >= seconds)
                {
                    said = now;

                    foreach (string line in _window.Describe(ticksPerMs, _all, 30, _cyclesPerMs))
                    {
                        Say(line);
                    }

                    SayInside(_window.Frames, ticksPerMs);
                    SayMarkers(_window.Frames);
                    SaySpans();
                    ClearWindow();
                }
            }

            // The trials: one thing changed at a time, a second for the scene to settle into it,
            // four seconds measured, and everything put back at the end (and at once if a frame of
            // this fails: see Tick).
            if (trials && _bodies.Length != 0)
            {
                TrialsBegin();

                for (int which = 0; which < TrialNames.Length; which++)
                {
                    Trial(which);
                    float from = Time.unscaledTime;
                    bool settled = false;

                    while (Time.unscaledTime - from < 5f)
                    {
                        yield return null;
                        Line = "profiling: trying '" + TrialNames[which] + "' (" + (which + 1) + " of " + TrialNames.Length + ")";

                        if (!settled && Time.unscaledTime - from >= 1f)
                        {
                            settled = true;
                            ClearWindow();
                            MenuPointers.Counted();
                        }
                    }

                    Say("trial, " + TrialNames[which] + ": " + _window.Short(ticksPerMs, _cyclesPerMs));
                    string pointers = MenuPointers.Counted();

                    if (pointers.Length != 0)
                    {
                        Say(pointers);
                    }

                    SayInside(_window.Frames, ticksPerMs);
                    SayMarkers(_window.Frames);
                    SaySpans();
                }

                TrialsEnd();
                Say("the trials' changes are put back");
            }

            _measuring = false;
            CloseMarkers();
            Camera.onPreCull -= OnCull;
            Gpu = false;
            Camera.onPreRender -= OnSceneBegin;
            Camera.onPostRender -= OnSceneEnd;
            Line = "profiling: putting things back";
            yield return AfterFrame;

            try
            {
                _harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Say("the stopwatches could not all be taken off (" + ex.GetType().Name + ": " + ex.Message + "): they do nothing from here on, and are gone with a restart");
            }

            _slots.Clear();
            _all.Clear();
            _inside.Clear();
            _running = false;
            Line = "";
            Say("done");
        }

        private static void Say(string line)
        {
            if (Hooks.Info != null)
            {
                Hooks.Info("profile: " + line);
            }
        }
    }
}
