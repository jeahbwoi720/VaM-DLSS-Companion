// VaM DLSS - Model Resolution: the scene shaded finely only where the eyes look.
//
// An NVIDIA card can run the pixel shader once for a block of pixels instead of once a pixel, tile
// by tile of the picture (variable rate shading; the native half sets it, see "foveated shading"
// in vws.cpp). This file decides where and when: full rate around the point each eye looks at --
// SteamVR's eye tracking when the headset has it, the middle of the lens otherwise -- coarser in a
// ring around that, coarsest beyond.
//
// It is switched on only around the scene camera's own geometry: the camera is given command
// buffers that raise "on" before its opaque and its transparent pass and "off" after each. The
// shadow maps and depth drawn before, and the image effects, DLSS, Neural Rendering and the menu
// drawn after, are shaded as always. Edges and depth stay at full resolution throughout; only the
// shading inside surfaces gets coarser, which the eye does not see where it is not looking.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace VamDlssNrWorkScale
{
    internal static class Foveation
    {
        internal static ConfigEntry<bool> CfgOn, CfgGaze, CfgStrong, CfgShow, CfgTopDown, CfgMonitor;
        internal static ConfigEntry<float> CfgInner, CfgOuter;

        internal static string Status = "";

        // The native half's block of numbers (FovField in vws.cpp).
        private const int FOn = 0, FCentre = 1, FInner = 5, FOuter = 6, FStrong = 7, FShow = 8, FTopDown = 9;
        private static readonly float[] _values = new float[16];
        private static readonly float[] _gaze = new float[4];

        private sealed class Wired
        {
            internal Camera Camera;
            internal CommandBuffer On, Off;
        }

        private static readonly List<Wired> _wired = new List<Wired>();
        private static bool _listening, _failed, _wasOn;
        private static float _statusAt;
        private static bool _tracked;

        internal static void Begin()
        {
            if (_listening)
            {
                return;
            }

            Camera.onPreCull += PreCull;
            _listening = true;
        }

        // From the plugin, every frame: the status line, and the taking down when it is switched off.
        internal static void Tick(float now)
        {
            bool on = CfgOn != null && CfgOn.Value && Native.Loaded && !_failed;

            if (!on)
            {
                if (_wasOn)
                {
                    Unwire();
                    _values[FOn] = 0f;
                    Native.FoveaConfigure(_values, IntPtr.Zero);
                    // whatever was last set on the device goes with the next thing drawn
                    GL.IssuePluginEvent(Native.EventFunc, Native.FoveaEvent(false));
                    _wasOn = false;
                }

                if (!_failed)
                {
                    Status = "";
                }

                return;
            }

            _wasOn = true;

            if (now < _statusAt)
            {
                return;
            }

            _statusAt = now + 0.5f;
            int state;
            uint width, height, samples, coarse, ons;
            Native.FoveaStatus(out state, out width, out height, out samples, out coarse, out ons);

            Status = "foveation: " + (
                state == 1 ? width + "x" + height + (samples > 1 ? ", " + samples + "x anti-aliasing" : "") + ", " + coarse + "% shaded coarsely, " +
                    (_tracked ? "following the eyes" : (CfgGaze.Value ? "lens centre (no gaze: " + Gaze.Status + ")" : "lens centre")) :
                state == 2 ? "this graphics card has no variable rate shading (NVIDIA, GTX 16 / RTX 20 series or later)" :
                state == 3 ? "the driver refused it (see the log)" :
                "waiting for the scene camera");
        }

        // Is this the camera the scene is seen through? In a headset the stereo one; on the monitor
        // (if wanted) the main one.
        private static bool Wanted(Camera camera)
        {
            if (camera == null || camera.targetTexture != null && !camera.stereoEnabled)
            {
                return false;
            }

            if (camera.stereoEnabled)
            {
                return true;
            }

            return CfgMonitor.Value && !UnityEngine.XR.XRSettings.enabled && camera == Camera.main;
        }

        private static Wired Find(Camera camera)
        {
            for (int i = _wired.Count - 1; i >= 0; i--)
            {
                if (_wired[i].Camera == null)
                {
                    _wired.RemoveAt(i);
                }
                else if (_wired[i].Camera == camera)
                {
                    return _wired[i];
                }
            }

            return null;
        }

        private static void Wire(Camera camera)
        {
            Wired w = new Wired();
            w.Camera = camera;
            w.On = new CommandBuffer();
            w.On.name = "Vws foveation on";
            w.On.IssuePluginEvent(Native.EventFunc, Native.FoveaEvent(true));
            w.Off = new CommandBuffer();
            w.Off.name = "Vws foveation off";
            w.Off.IssuePluginEvent(Native.EventFunc, Native.FoveaEvent(false));

            // Around the geometry and nothing else. (Whichever way the camera renders: the deferred
            // events simply never come in forward, and the other way round.)
            camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, w.On);
            camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, w.Off);
            camera.AddCommandBuffer(CameraEvent.BeforeGBuffer, w.On);
            camera.AddCommandBuffer(CameraEvent.AfterGBuffer, w.Off);
            camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, w.On);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, w.Off);
            camera.AddCommandBuffer(CameraEvent.AfterEverything, w.Off);
            _wired.Add(w);

            if (Hooks.Info != null)
            {
                Hooks.Info("foveation: on camera '" + camera.name + "'" + (camera.stereoEnabled ? " (headset)" : " (monitor)"));
            }
        }

        private static void Unwire()
        {
            for (int i = 0; i < _wired.Count; i++)
            {
                Wired w = _wired[i];

                if (w.Camera != null)
                {
                    w.Camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, w.On);
                    w.Camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, w.Off);
                    w.Camera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, w.On);
                    w.Camera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, w.Off);
                    w.Camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, w.On);
                    w.Camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, w.Off);
                    w.Camera.RemoveCommandBuffer(CameraEvent.AfterEverything, w.Off);
                }

                w.On.Release();
                w.Off.Release();
            }

            _wired.Clear();
        }

        private static float AxisAt(float m)
        {
            float v = 0.5f - 0.5f * m;
            return float.IsNaN(v) || v < 0.2f || v > 0.8f ? 0.5f : v;
        }

        // Unity raises this for every camera about to render, on the main thread: the moment to say
        // where this camera's eyes are looking.
        private static void PreCull(Camera camera)
        {
            if (_failed || CfgOn == null || !CfgOn.Value || !Native.Loaded)
            {
                return;
            }

            try
            {
                if (!Wanted(camera))
                {
                    return;
                }

                if (Find(camera) == null)
                {
                    Wire(camera);
                }

                // Where each eye looks, in its own picture, v up from the bottom: the middle of the
                // lens (the view axis; not the middle of the picture, the frustum reaches further
                // out than in), or the gaze.
                _values[FCentre] = _values[FCentre + 1] = _values[FCentre + 2] = _values[FCentre + 3] = 0.5f;
                _tracked = false;

                if (camera.stereoEnabled)
                {
                    Matrix4x4 left = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                    Matrix4x4 right = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                    _values[FCentre] = AxisAt(left.m02);
                    _values[FCentre + 1] = AxisAt(left.m12);
                    _values[FCentre + 2] = AxisAt(right.m02);
                    _values[FCentre + 3] = AxisAt(right.m12);

                    if (CfgGaze.Value && Gaze.Read(_gaze, Time.frameCount, Time.unscaledTime))
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            _values[FCentre + i] = Mathf.Clamp01(_gaze[i]);
                        }

                        _tracked = true;
                    }
                }

                float inner = CfgInner.Value, outer = Mathf.Max(CfgOuter.Value, inner);
                _values[FOn] = 1f;
                _values[FInner] = inner;
                _values[FOuter] = outer;
                _values[FStrong] = CfgStrong.Value ? 1f : 0f;
                _values[FShow] = CfgShow.Value ? 1f : 0f;
                _values[FTopDown] = CfgTopDown.Value ? 1f : 0f;
                Native.FoveaConfigure(_values, Texture2D.whiteTexture.GetNativeTexturePtr());
            }
            catch (Exception ex)
            {
                _failed = true;
                Status = "foveation is off: " + ex.GetType().Name + ": " + ex.Message;

                if (Hooks.Warn != null)
                {
                    Hooks.Warn(Status + "\n" + ex.StackTrace);
                }

                try
                {
                    Unwire();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
