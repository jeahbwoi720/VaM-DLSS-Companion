// VaM DLSS - Model Resolution: the scene's interface atoms, kept out of what DLSS and Neural
// Rendering work on.
//
// VaM's menus are on the interface layer, which VaM DLSS (on the monitor) and HeadsetUi (in a
// headset) take off the scene and draw after the networks are done. The atoms a scene is built
// with -- UIButton, UISlider, UIToggle, UIText, UIImage, UIButtonImage -- are not: their canvases
// are on the scene's own layer, so their text is rendered small by a DLSS quality mode, upscaled,
// and reworked by Neural Rendering with everything else.
//
// Here their canvases are moved to a layer nothing else in VaM uses. Every camera of the game's
// draws that layer as it draws the scene's, so nothing changes by the move alone -- mirrors and
// screenshots included. While VaM DLSS is at work the layer is taken off the scene's camera and
// drawn afterwards onto the finished picture: in a headset by HeadsetUi, with the menu; on the
// monitor by a camera of our own, between VaM DLSS's picture and its interface.
//
// The finished picture's depth buffer knows nothing of the scene, and unlike the menu a button on
// a wall has to stay behind whoever stands in front of it. So the scene's depth is kept when the
// frame is composed and laid into the depth buffer before the atoms are drawn (native: DepthFill).

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VamDlssNrWorkScale
{
    internal static class SceneUi
    {
        internal static ConfigEntry<bool> CfgOn, CfgOcclude, CfgFlip;
        internal static ConfigEntry<string> CfgAtoms;
        internal static ConfigEntry<int> CfgLayer;
        internal static ConfigEntry<float> CfgSlack;

        internal static string Status = "";

        // ---- the atoms -------------------------------------------------------------------------

        private static readonly List<GameObject> _moved = new List<GameObject>();
        private static readonly List<Canvas> _canvases = new List<Canvas>();
        private static readonly HashSet<string> _types = new HashSet<string>();
        private static string _typesText;
        private static int _layer = -1;
        private static int _atomCount;
        private static string _refused = "";
        private static float _scanAt;

        // On, and with something moved to draw.
        internal static bool Wanted
        {
            get { return Able() && _refused.Length == 0 && _moved.Count != 0; }
        }

        private static bool Able()
        {
            return CfgOn != null && CfgOn.Value && Native.Loaded && HeadsetUi.Hooked && !HeadsetUi.Failed;
        }

        // The layer's bit, where this camera is to have it taken off; 0 where there is nothing to.
        internal static int Mask(Camera camera)
        {
            if (!Wanted || camera == null)
            {
                return 0;
            }

            int bit = 1 << _layer;

            if ((camera.cullingMask & bit) == 0)
            {
                // Something has taken that layer off the scene's camera: the atoms would be gone.
                _refused = "the scene's camera does not draw layer " + _layer + " (set [SceneUi] Layer to another free one)";
                GiveBack();
                return 0;
            }

            return bit;
        }

        internal static void Tick(float now)
        {
            if (CfgOn == null)
            {
                return;
            }

            if (!Able())
            {
                if (_moved.Count != 0)
                {
                    GiveBack();
                }

                MonitorOff();
                _refused = "";
                _layer = -1;
                Status = "";
                return;
            }

            // The monitor's camera, when VaM DLSS no longer comes by to have it set up.
            if (_own != null && _own.enabled && Time.frameCount - _monitorSeen > 2)
            {
                MonitorOff();
            }

            if (now < _scanAt)
            {
                return;
            }

            _scanAt = now + 1f;

            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                _refused = "looking for them failed (" + ex.GetType().Name + ": " + ex.Message + ")";
                GiveBack();
            }

            Say(now);
        }

        private static void Scan()
        {
            int layer = CfgLayer != null ? CfgLayer.Value : 19;
            string types = CfgAtoms != null ? CfgAtoms.Value : "";

            if (layer != _layer || types != _typesText)
            {
                GiveBack();
                _layer = layer;
                _typesText = types;
                _types.Clear();
                _refused = "";

                foreach (string type in types.Split(','))
                {
                    if (type.Trim().Length != 0)
                    {
                        _types.Add(type.Trim());
                    }
                }

                string taken = LayerMask.LayerToName(layer);

                if (layer < 1 || layer > 31 || taken.Length != 0)
                {
                    _refused = "layer " + layer + " is in use by VaM (\"" + taken + "\"): set [SceneUi] Layer to 3, 6, 7, 18 or 19";
                }
            }

            for (int i = _moved.Count - 1; i >= 0; i--)
            {
                if (_moved[i] == null)
                {
                    _moved.RemoveAt(i);
                }
            }

            SuperController sc = SuperController.singleton;

            if (_refused.Length != 0 || sc == null)
            {
                return;
            }

            int count = 0;

            foreach (Atom atom in sc.GetAtoms())
            {
                if (atom == null || !_types.Contains(atom.type))
                {
                    continue;
                }

                count++;
                atom.GetComponentsInChildren(true, _canvases);

                foreach (Canvas canvas in _canvases)
                {
                    // Only what is on the scene's own layer: an atom's settings panel is on the
                    // interface's, and is drawn with the menu already.
                    if (canvas != null && canvas.gameObject.layer == 0)
                    {
                        canvas.gameObject.layer = layer;
                        _moved.Add(canvas.gameObject);
                    }
                }

                _canvases.Clear();
            }

            _atomCount = count;
        }

        private static void GiveBack()
        {
            foreach (GameObject moved in _moved)
            {
                if (moved != null && moved.layer == _layer)
                {
                    moved.layer = 0;
                }
            }

            _moved.Clear();
        }

        internal static void Stop()
        {
            GiveBack();
            MonitorOff();

            if (_own != null)
            {
                UnityEngine.Object.Destroy(_own.gameObject);
            }

            _own = null;

            if (_kept != null)
            {
                _kept.Release();
                UnityEngine.Object.Destroy(_kept);
            }

            _kept = null;
            _keptPtr = IntPtr.Zero;
        }

        // ---- the scene's depth -----------------------------------------------------------------

        private static readonly int DepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
        private static RenderTexture _kept;
        private static IntPtr _keptPtr = IntPtr.Zero;
        private static int _keptFrame = -1;
        private static bool _keptStereo;

        // Called while VaM DLSS composes the scene camera's frame, when the atoms are off it: the
        // scene's depth texture is that camera's at this moment, and nobody else's yet.
        internal static void Composing(Camera camera, bool stereo, bool off)
        {
            if (!stereo)
            {
                _monitorComposed = Time.frameCount;
            }

            if (!off || camera == null)
            {
                return;
            }

            bool kept = CfgOcclude != null && CfgOcclude.Value && Keep(camera, stereo);

            // On the monitor the atoms' own camera renders after this one: its fill, for this frame.
            if (!stereo && kept && _own != null && _ownFill != null)
            {
                Fill(_ownFill, -1, true);
            }
        }

        // The card's depth copied as plain numbers, into a texture that is ours to keep until the
        // atoms are drawn (the game's own goes back to a pool when its camera is done).
        private static bool Keep(Camera camera, bool stereo)
        {
            // VaM DLSS asks for the depth texture itself; if it has not yet, it is there next frame.
            if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
                return false;
            }

            Texture depth = Shader.GetGlobalTexture(DepthTextureId);

            if (depth == null || depth.width < 16 || depth.height < 16)
            {
                return false;
            }

            if (_kept == null || _kept.width != depth.width || _kept.height != depth.height)
            {
                if (_kept != null)
                {
                    _kept.Release();
                    UnityEngine.Object.Destroy(_kept);
                }

                RenderTexture source = depth as RenderTexture;
                RenderTextureDescriptor d = source != null ? source.descriptor : new RenderTextureDescriptor(depth.width, depth.height);
                d.colorFormat = RenderTextureFormat.RFloat;
                d.depthBufferBits = 0;
                d.msaaSamples = 1;
                d.sRGB = false;
                d.useMipMap = false;
                d.autoGenerateMips = false;
                _kept = new RenderTexture(d);
                _kept.filterMode = FilterMode.Point;
                _kept.hideFlags = HideFlags.HideAndDontSave;
                _kept.Create();
            }

            RenderTexture active = RenderTexture.active;
            Graphics.Blit(depth, _kept);
            RenderTexture.active = active;

            _keptPtr = _kept.GetNativeTexturePtr();
            _keptFrame = Time.frameCount;
            _keptStereo = stereo;
            return _keptPtr != IntPtr.Zero;
        }

        // Makes `buffer` the one command that lays this frame's depth into the depth target of the
        // camera it is on: `eye` 0 or 1 in a headset, -1 for a picture that is not one of two.
        // `topDown`: the target's first row is the top of the picture (the kept depth, a render
        // texture, has it at the bottom). False, and the buffer empty, when there is no depth.
        internal static bool Fill(CommandBuffer buffer, int eye, bool topDown)
        {
            buffer.Clear();

            if (_keptFrame != Time.frameCount || _keptPtr == IntPtr.Zero || _kept == null || CfgOcclude == null || !CfgOcclude.Value)
            {
                return false;
            }

            // Single-pass stereo: both eyes side by side in one texture.
            bool wide = eye >= 0 && _keptStereo && _kept.width > _kept.height * 3 / 2;
            bool flip = topDown != (CfgFlip != null && CfgFlip.Value);
            float slack = CfgSlack != null ? Mathf.Clamp(CfgSlack.Value, 0f, 0.2f) : 0.01f;

            buffer.IssuePluginEvent(Native.EventFunc, Native.PushDepthFill(_keptPtr, wide ? eye * 0.5f : 0f, wide ? 0.5f : 1f, flip, slack, SystemInfo.usesReversedZBuffer));
            return true;
        }

        // ---- the monitor -----------------------------------------------------------------------

        private static Camera _own;
        private static CommandBuffer _ownFill, _ownClear;
        private static int _monitorSeen = -100, _monitorComposed = -100;

        // Before the scene's camera culls, on the monitor: the mask to take off it this frame, with
        // the atoms' own camera made ready to draw what is taken. Nothing is taken until that
        // camera has been on for a frame, nor when VaM DLSS has stopped composing frames.
        internal static int OnMonitor(Camera main, int mask)
        {
            _monitorSeen = Time.frameCount;

            if (mask == 0 || Time.frameCount - _monitorComposed > 2)
            {
                MonitorOff();
                return 0;
            }

            Camera own = Own(main);
            _ownFill.Clear();

            if (!own.enabled)
            {
                own.cullingMask = 0;
                own.enabled = true;
                return 0;
            }

            int off = main.cullingMask & mask;

            // After VaM DLSS's picture has been put on the screen (its camera for that is a
            // quarter above the scene's) and before its interface (a half above).
            own.depth = main.depth + 0.4f;
            own.cullingMask = off;
            own.fieldOfView = main.fieldOfView;
            own.nearClipPlane = main.nearClipPlane;
            own.farClipPlane = main.farClipPlane;
            own.allowMSAA = main.allowMSAA;
            own.ResetAspect();
            own.ResetProjectionMatrix();

            if (off != 0)
            {
                _drawnAt = Time.unscaledTime;
            }

            return off;
        }

        private static void MonitorOff()
        {
            if (_own != null && _own.enabled)
            {
                _own.enabled = false;
                _own.cullingMask = 0;
            }

            if (_ownFill != null)
            {
                _ownFill.Clear();
            }
        }

        private static Camera Own(Camera main)
        {
            if (_own != null)
            {
                if (_own.transform.parent != main.transform)
                {
                    _own.transform.SetParent(main.transform, false);
                }

                return _own;
            }

            GameObject holder = new GameObject("VamDlssNrWorkScale.SceneUi");
            holder.hideFlags = HideFlags.HideAndDontSave;
            holder.transform.SetParent(main.transform, false);

            Camera camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = null;
            camera.clearFlags = CameraClearFlags.Depth;
            camera.cullingMask = 0;
            camera.renderingPath = RenderingPath.Forward;
            camera.depthTextureMode = DepthTextureMode.None;
            camera.allowHDR = false;
            camera.useOcclusionCulling = false;

            if (_ownFill == null)
            {
                _ownFill = new CommandBuffer();
                _ownFill.name = "VamDlssNrWorkScale scene depth";

                // VaM DLSS's interface is drawn next into the same depth buffer, and is to find
                // nothing of the scene in it, as before.
                _ownClear = new CommandBuffer();
                _ownClear.name = "VamDlssNrWorkScale scene depth off";
                _ownClear.ClearRenderTarget(true, false, Color.clear);
            }

            camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _ownFill);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _ownClear);
            _own = camera;
            return camera;
        }

        // ---- what the panels say ---------------------------------------------------------------

        private static float _drawnAt = -100f;
        private static uint _fillsBad;
        private static bool _alwaysOnTop;

        // HeadsetUi has drawn them onto a headset frame.
        internal static void Drawn()
        {
            _drawnAt = Time.unscaledTime;
        }

        private static void Say(float now)
        {
            if (_refused.Length != 0)
            {
                Status = "scene UI atoms: in the scene -- " + _refused;
                return;
            }

            if (_atomCount == 0)
            {
                Status = "scene UI atoms: none in this scene";
                return;
            }

            if (now - _drawnAt > 1.5f)
            {
                Status = "scene UI atoms: " + _atomCount + ", in the scene (VaM DLSS is not at work)";
                return;
            }

            // Did the depth get under them?
            uint done, noTarget, failed;
            Native.DepthFillStatus(out done, out noTarget, out failed);
            bool occlude = CfgOcclude != null && CfgOcclude.Value;
            _alwaysOnTop = !occlude || noTarget + failed != _fillsBad || Time.frameCount - _keptFrame > 2;
            _fillsBad = noTarget + failed;

            Status = "scene UI atoms: " + _atomCount + " drawn after DLSS" + (_alwaysOnTop ? (occlude ? ", always on top (no scene depth)" : ", always on top") : "");
        }
    }
}
