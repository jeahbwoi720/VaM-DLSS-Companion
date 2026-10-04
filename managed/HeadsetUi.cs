// VaM DLSS - Model Resolution: the interface at full size in a headset.
//
// On the monitor VamDlssNr takes the interface's layers off the scene camera and draws them
// afterwards with a camera of their own, so the menu is neither reconstructed by DLSS nor repainted
// by the network. In a headset it does not ("ui split: not available in VR"): the floating menu is
// rendered with the scene, at the reduced size when a quality mode is on, and goes through DLSS and
// Neural Rendering with everything else.
//
// This does the split for the headset. The layers come off the stereo camera for the length of its
// own render; when VamDlssNr is about to hand its finished frame on, a second camera draws them
// once per eye straight onto that frame -- the reconstructed eye textures that go to the
// compositor when DLSS is upscaling, the two halves of the finished frame otherwise. Nothing is
// composited: the interface's own shaders blend onto the picture, as they would have in the scene.
//
// What it costs: the interface is drawn with no scene depth behind it, so it is always on top --
// a hand or a person in front of the menu no longer covers it.
//
// Off unless turned on, and it only takes the layers off while VamDlssNr was seen to present a
// headset frame the frame before: if its pipeline stops (DLSS and NR both off, a debug view), the
// interface goes back into the scene on the next frame.
//
// The same moment -- the finished frame, about to be handed on -- is where the sharpening pass
// runs, on the monitor as in a headset and for stills too: first the picture is sharpened, then
// the interface is drawn over it, so the menu is never sharpened.

using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VamDlssNr;

namespace VamDlssNrWorkScale
{
    public static class HeadsetUi
    {
        internal static ConfigEntry<bool> CfgOn;
        internal static ConfigEntry<float> CfgSharpen;
        internal static bool Hooked;
        internal static string Problem = "";

        private static MethodInfo M_preCull;
        private static MethodInfo M_compose;
        private static MethodInfo M_renderImage;
        private static MethodInfo M_transfer;
        private static MethodInfo M_full;
        private static FieldInfo F_primary;
        private static FieldInfo F_eyeOut;
        private static FieldInfo F_srPresented;
        private static FieldInfo F_present;

        // Any capture's frame while its Compose runs: where it goes, and whether it has been
        // finished yet.
        private static NrCapture _composing;
        private static RenderTexture _frameDst;
        private static RenderTexture _framePresent;
        private static RenderTexture _frameSrc;
        private static bool _frameStereo;
        private static bool _finished;
        private static ConfigEntry<int> _modLayers;

        // The frame being composed: whose it is and where VamDlssNr will put it.
        private static NrCapture _capture;
        private static RenderTexture _dst;
        private static bool _presented;

        // True when the frame before was presented through the hooks below, so the layers can be
        // taken off this one in the knowledge that they will be drawn again.
        private static bool _live;

        private static Camera _stripped;
        private static int _removed;
        private static int _drawnFrame = -1;
        private static bool _perEye;
        private static bool _failed;

        // ---- hooking ---------------------------------------------------------------------------

        internal static void Resolve()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type capture = typeof(NrCapture);

            M_preCull = capture.GetMethod("OnPreCull", any, null, Type.EmptyTypes, null);
            M_compose = capture.GetMethod("Compose", any, null, new[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
            M_renderImage = capture.GetMethod("OnRenderImage", any, null, new[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
            M_transfer = capture.GetMethod("Transfer", any, null, new[] { typeof(Texture), typeof(RenderTexture), typeof(bool) }, null);
            M_full = capture.GetMethod("BlitFullViewport", any, null, new[] { typeof(Texture), typeof(RenderTexture), typeof(bool) }, null);
            F_primary = capture.GetField("Primary", any);
            F_eyeOut = capture.GetField("_eyeOut", any);
            F_srPresented = capture.GetField("_srPresented", any);
            F_present = capture.GetField("_presentTexture", any);

            if (F_present != null && F_present.FieldType != typeof(RenderTexture))
            {
                F_present = null;
            }

            FieldInfo layers = typeof(VamDlssNrPlugin).GetField("CfgUiLayerMask", any);
            _modLayers = layers != null ? layers.GetValue(null) as ConfigEntry<int> : null;

            string missing =
                M_preCull == null ? "NrCapture.OnPreCull" :
                M_compose == null ? "NrCapture.Compose" :
                M_renderImage == null ? "NrCapture.OnRenderImage" :
                M_transfer == null || !M_transfer.IsStatic ? "NrCapture.Transfer(Texture, RenderTexture, bool)" :
                M_full == null || M_full.IsStatic ? "NrCapture.BlitFullViewport(Texture, RenderTexture, bool)" :
                F_primary == null || F_primary.FieldType != typeof(bool) ? "NrCapture.Primary" :
                F_eyeOut == null || F_eyeOut.FieldType != typeof(RenderTexture[]) ? "NrCapture._eyeOut" :
                F_srPresented == null || F_srPresented.FieldType != typeof(bool) ? "NrCapture._srPresented" : null;

            if (missing != null)
            {
                Problem = "the headset menu cannot be drawn apart: this build of VamDlssNr has no " + missing;
            }
        }

        internal static void Apply(Harmony harmony)
        {
            if (Problem.Length != 0)
            {
                return;
            }

            try
            {
                Type me = typeof(HeadsetUi);
                const BindingFlags pub = BindingFlags.Static | BindingFlags.Public;

                harmony.Patch(M_preCull, new HarmonyMethod(me.GetMethod("PreCullPrefix", pub)));
                harmony.Patch(M_compose, new HarmonyMethod(me.GetMethod("ComposePrefix", pub)));
                harmony.Patch(M_transfer, new HarmonyMethod(me.GetMethod("TransferPrefix", pub)));
                harmony.Patch(M_full, new HarmonyMethod(me.GetMethod("FullPrefix", pub)));
                harmony.Patch(M_renderImage, null, new HarmonyMethod(me.GetMethod("RenderImagePostfix", pub)));
                Hooked = true;
            }
            catch (Exception ex)
            {
                Hooked = false;
                Problem = "the headset menu cannot be drawn apart (" + ex.GetType().Name + ": " + ex.Message + ")";

                // Half of these would take the layers off and never draw them again.
                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool Wanted()
        {
            return Hooked && !_failed && CfgOn != null && CfgOn.Value;
        }

        private static bool IsPrimary(NrCapture capture)
        {
            try
            {
                return (bool)F_primary.GetValue(capture);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int Layers()
        {
            return _modLayers != null ? _modLayers.Value : 32;
        }

        // ---- the frame -------------------------------------------------------------------------

        // Before the stereo camera culls: take the interface's layers off it, for this render only.
        public static void PreCullPrefix(NrCapture __instance)
        {
            if (!Hooked || !IsPrimary(__instance))
            {
                return;
            }

            // Whatever a frame that never reached its end left behind.
            Restore();

            if (!Wanted())
            {
                _live = false;
                return;
            }

            Camera camera = Hooks.CameraOf(__instance);

            if (camera == null || !camera.stereoEnabled)
            {
                _live = false;
                return;
            }

            if (!_live)
            {
                return;
            }

            int layers = Layers();
            int removed = camera.cullingMask & layers;

            if (removed == 0)
            {
                return;
            }

            camera.cullingMask &= ~layers;
            _stripped = camera;
            _removed = removed;
        }

        public static void ComposePrefix(NrCapture __instance, RenderTexture __0, RenderTexture __1)
        {
            if (!Hooked)
            {
                return;
            }

            Camera camera = Hooks.CameraOf(__instance);
            bool stereo = camera != null && camera.stereoEnabled;

            // On the monitor, while DLSS upscales, the finished frame goes to a texture of
            // VamDlssNr's own rather than to the camera's.
            _composing = __instance;
            _frameSrc = __0;
            _frameDst = __1;
            _framePresent = F_present != null ? F_present.GetValue(__instance) as RenderTexture : null;
            _frameStereo = stereo;
            _finished = false;

            if (!IsPrimary(__instance))
            {
                return;
            }

            _capture = null;
            _dst = null;
            _presented = false;

            if (!Wanted() || __1 == null || !stereo)
            {
                return;
            }

            _capture = __instance;
            _dst = __1;
        }

        // VamDlssNr's copies all go through Transfer; the one that matters is the one into the
        // frame's destination, which is the finished picture going out.
        public static void TransferPrefix(Texture __0, RenderTexture __1)
        {
            if ((object)_composing != null && IsFinal(__1))
            {
                Finish(__0, __1, false);
            }
        }

        // The same moment while DLSS is upscaling in a headset.
        public static void FullPrefix(Texture __0, RenderTexture __1)
        {
            if ((object)_composing != null && IsFinal(__1))
            {
                Finish(__0, __1, true);
            }
        }

        private static bool IsFinal(RenderTexture target)
        {
            return ReferenceEquals(target, _frameDst) || ((object)_framePresent != null && ReferenceEquals(target, _framePresent));
        }

        // The finished frame, once per Compose: sharpened first, the interface drawn over it after.
        private static void Finish(Texture source, RenderTexture target, bool upscaled)
        {
            RenderTexture frame = source as RenderTexture;

            if (_finished || frame == null)
            {
                return;
            }

            _finished = true;

            // While DLSS is upscaling in a headset the compositor is handed the reconstructed
            // eyes, not the frame: whatever is done to the picture has to be done to those.
            RenderTexture[] eyes = upscaled && _frameStereo ? PublishedEyes(_composing) : null;
            bool touched = false;

            try
            {
                touched = Sharpen(frame, eyes);
            }
            catch (Exception ex)
            {
                _sharpenFailed = true;

                if (Hooks.Warn != null)
                {
                    Hooks.Warn("sharpening is off: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // The room in place of the key colour, in a headset: after the sharpening, so the camera's
            // grain is not sharpened, and before the interface, which is drawn over it.
            if (_frameStereo && IsPrimary(_composing))
            {
                try
                {
                    // Will the interface be drawn over this frame when it is over?
                    bool menuLater = (object)_dst != null && ReferenceEquals(target, _dst) && (object)_stripped != null && _stripped != null && Method() != 2;
                    touched |= Passthrough.Run(frame, eyes, Hooks.NetIsTopDown(), Time.unscaledTime, menuLater);
                }
                catch (Exception ex)
                {
                    Passthrough.Fail(ex);
                }
            }

            if ((object)_dst != null && ReferenceEquals(target, _dst))
            {
                touched |= Present(frame, eyes);
            }

            // The frame itself is what the mirror on the monitor shows: give it the eyes as they
            // now are, the way VamDlssNr gathered them.
            if (touched && eyes != null && frame.width == eyes[0].width * 2 && frame.height == eyes[0].height && eyes[1].width == eyes[0].width && eyes[1].height == eyes[0].height)
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    Graphics.CopyTexture(eyes[eye], 0, 0, 0, 0, eyes[eye].width, eyes[eye].height, frame, 0, 0, eye * eyes[eye].width, 0);
                }
            }
        }

        private static RenderTexture[] PublishedEyes(NrCapture capture)
        {
            try
            {
                if (!(bool)F_srPresented.GetValue(capture))
                {
                    return null;
                }

                RenderTexture[] eyes = F_eyeOut.GetValue(capture) as RenderTexture[];

                if (eyes == null || eyes.Length < 2 || eyes[0] == null || eyes[1] == null || !eyes[0].IsCreated() || !eyes[1].IsCreated())
                {
                    return null;
                }

                return eyes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool _sharpenFailed;

        private static bool Sharpen(RenderTexture frame, RenderTexture[] eyes)
        {
            float strength = CfgSharpen != null ? CfgSharpen.Value : 0f;

            if (_sharpenFailed || strength <= 0f || !Native.Loaded)
            {
                return false;
            }

            if (eyes != null)
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    Native.IssuePass(Native.PushSharpen(eyes[eye].GetNativeTexturePtr(), strength, 1));
                }
            }
            else
            {
                Native.IssuePass(Native.PushSharpen(frame.GetNativeTexturePtr(), strength, _frameStereo ? 2 : 1));
            }

            return true;
        }

        public static void RenderImagePostfix(NrCapture __instance)
        {
            if (!Hooked)
            {
                return;
            }

            // With DLSS and Neural Rendering both off VamDlssNr hands the camera's picture straight
            // on and none of the hooks above fire. Passthrough still has a frame to work on: the
            // camera's own, which is then handed on again.
            if (!_finished && _frameStereo && (object)_composing != null && ReferenceEquals(__instance, _composing) && IsPrimary(__instance) &&
                _frameSrc != null && _frameDst != null)
            {
                try
                {
                    if (Passthrough.Run(_frameSrc, null, false, Time.unscaledTime, false))
                    {
                        Graphics.Blit(_frameSrc, _frameDst);
                    }
                }
                catch (Exception ex)
                {
                    Passthrough.Fail(ex);
                }
            }

            _composing = null;
            _frameSrc = null;
            _frameDst = null;
            _framePresent = null;

            if (!IsPrimary(__instance))
            {
                return;
            }

            Restore();
            _live = (object)_capture != null && _presented && Wanted();
            _capture = null;
            _dst = null;
        }

        private static void Restore()
        {
            if ((object)_stripped != null)
            {
                if (_stripped != null)
                {
                    _stripped.cullingMask |= _removed;
                }

                _stripped = null;
                _removed = 0;
            }
        }

        // How and when the two eyes are drawn.
        //   0  at the end of the frame, with the single-pass stereo shader keyword off (the way
        //      that works: see Draw)
        //   1  at the end of the frame, keyword left as it is
        //   2  at once, inside VamDlssNr's frame, keyword off -- kept for comparison only
        internal static ConfigEntry<int> CfgMethod;
        internal static bool CaptureWanted;

        private const string StereoKeyword = "UNITY_SINGLE_PASS_STEREO";
        private static Camera _camera;
        private static RenderTexture _depth;
        private static int _reportedMethod = -1;

        // What the end of the frame is to draw, noted while VamDlssNr composed it.
        private static int _pendingFrame = -1;
        private static Camera _pendingMain;
        private static int _pendingLayers;
        private static RenderTexture _pendingDst;
        private static readonly RenderTexture[] _pendingEyes = new RenderTexture[2];
        private static bool _pendingPerEye;

        private static int Method()
        {
            return CfgMethod != null ? CfgMethod.Value : 0;
        }

        private static bool Present(RenderTexture frame, RenderTexture[] eyes)
        {
            if (_drawnFrame == Time.frameCount)
            {
                return false;
            }

            // Nothing was taken off this frame: it has its interface in it already.
            if ((object)_stripped == null || _stripped == null)
            {
                _presented = true;
                return false;
            }

            _drawnFrame = Time.frameCount;
            _presented = true;

            if (Method() == 2)
            {
                return Guarded(delegate { Draw(_stripped, _removed, eyes, frame, Hooks.NetIsTopDown(), true); });
            }

            // Not now: while a single-pass stereo camera is rendering -- and its image effects are
            // part of that -- the device draws everything twice, once into each half of whatever
            // viewport is set, and a camera rendered from inside it inherits that. The interface
            // is drawn when the frame is over and the device is back to drawing things once.
            _pendingFrame = Time.frameCount;
            _pendingMain = _stripped;
            _pendingLayers = _removed;
            _pendingDst = _dst;
            _pendingPerEye = eyes != null;
            _pendingEyes[0] = eyes != null ? eyes[0] : null;
            _pendingEyes[1] = eyes != null ? eyes[1] : null;
            return false;
        }

        // After every camera has rendered and before the frame is handed to the headset.
        internal static void EndOfFrame()
        {
            if (_pendingFrame != Time.frameCount)
            {
                return;
            }

            _pendingFrame = -1;

            Camera main = _pendingMain;
            RenderTexture dst = _pendingDst;
            _pendingMain = null;
            _pendingDst = null;

            if (main == null || _failed)
            {
                return;
            }

            bool keywordOff = Method() != 1;

            if (_pendingPerEye)
            {
                // The reconstructed eyes, which lie the way VamDlssNr's FlipY says.
                if (_pendingEyes[0] != null && _pendingEyes[1] != null && _pendingEyes[0].IsCreated() && _pendingEyes[1].IsCreated())
                {
                    Guarded(delegate { Draw(main, _pendingLayers, _pendingEyes, null, Hooks.NetIsTopDown(), keywordOff); });
                    MatteAfterMenu(null, _pendingEyes, Hooks.NetIsTopDown());
                }
            }
            else if (dst != null && dst.IsCreated())
            {
                // The camera's own target, which VamDlssNr has filled by now: a render texture
                // like any other, first row at the bottom.
                Guarded(delegate { Draw(main, _pendingLayers, null, dst, false, keywordOff); });
                MatteAfterMenu(dst, null, false);
            }

            _pendingEyes[0] = _pendingEyes[1] = null;
        }

        // The passthrough overlay's matte, now that the interface is in the frame.
        private static void MatteAfterMenu(RenderTexture wide, RenderTexture[] eyes, bool topDown)
        {
            try
            {
                Passthrough.MatteLate(wide, eyes, topDown);
            }
            catch (Exception ex)
            {
                Passthrough.Fail(ex);
            }
        }

        private delegate void Work();

        private static bool Guarded(Work work)
        {
            try
            {
                work();
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Problem = "the headset menu is back in the scene: drawing it apart failed (" + ex.GetType().Name + ": " + ex.Message + ")";

                if (Hooks.Warn != null)
                {
                    Hooks.Warn(Problem + "\n" + ex.StackTrace);
                }

                return false;
            }
        }

        // Draws the layers once per eye: onto `eyes` when given, else onto the two halves of
        // `wide`. `topDown` says the target's first row is the top of the picture: a camera drawing
        // into a render texture lays it down first row at the bottom, so there the projection is
        // turned over, and the winding with it.
        private static void Draw(Camera main, int layers, RenderTexture[] eyes, RenderTexture wide, bool topDown, bool keywordOff)
        {
            int method = Method();
            _perEye = eyes != null;

            RenderTexture active = RenderTexture.active;
            bool report = method != _reportedMethod || CaptureWanted;
            bool keyword = keywordOff && Shader.IsKeywordEnabled(StereoKeyword);

            if (report)
            {
                _reportedMethod = method;
                RenderTexture t = eyes != null ? eyes[0] : wide;
                Say("method " + method + ", layers 0x" + layers.ToString("X") + ", onto " + (eyes != null ? "separate eyes " : "one wide target ") + t.width + "x" + t.height + " " + t.format +
                    " depth " + t.depth + " msaa " + t.antiAliasing + ", first row at the " + (topDown ? "top" : "bottom") + ", stereo keyword " + (Shader.IsKeywordEnabled(StereoKeyword) ? "on" : "off") +
                    (keyword ? " (turned off for the draw)" : ""));
            }

            if (keyword)
            {
                Shader.DisableKeyword(StereoKeyword);
            }

            Camera ui = EnsureCamera();

            try
            {
                ui.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
                ui.cullingMask = layers;
                ui.nearClipPlane = main.nearClipPlane;
                ui.farClipPlane = main.farClipPlane;

                for (int eye = 0; eye < 2; eye++)
                {
                    Camera.StereoscopicEye which = eye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                    RenderTexture target = eyes != null ? eyes[eye] : wide;

                    // VamDlssNr has taken its jitter off again by now, so this is the device's own.
                    Matrix4x4 device = main.GetStereoProjectionMatrix(which);

                    ui.worldToCameraMatrix = main.GetStereoViewMatrix(which);
                    ui.projectionMatrix = topDown ? Matrix4x4.Scale(new Vector3(1f, -1f, 1f)) * device : device;
                    ui.allowMSAA = target.antiAliasing > 1;

                    // The interface's masks are cut with the stencil: a target with no depth and
                    // stencil of its own is lent one.
                    if (target.depth >= 24)
                    {
                        ui.targetTexture = target;
                    }
                    else
                    {
                        ui.targetTexture = null;
                        ui.SetTargetBuffers(target.colorBuffer, Depth(target).depthBuffer);
                    }

                    ui.rect = eyes != null ? new Rect(0f, 0f, 1f, 1f) : new Rect(eye * 0.5f, 0f, 0.5f, 1f);

                    GL.invertCulling = topDown;
                    ui.Render();
                    GL.invertCulling = false;
                    ui.targetTexture = null;
                }

                if (CaptureWanted)
                {
                    CaptureWanted = false;
                    Capture(wide, eyes);
                }
            }
            finally
            {
                GL.invertCulling = false;
                ui.targetTexture = null;

                if (keyword)
                {
                    Shader.EnableKeyword(StereoKeyword);
                }

                RenderTexture.active = active;
            }
        }

        private static RenderTexture Depth(RenderTexture like)
        {
            int aa = like.antiAliasing > 1 ? like.antiAliasing : 1;

            if (_depth != null && _depth.width == like.width && _depth.height == like.height && _depth.antiAliasing == aa && _depth.IsCreated())
            {
                return _depth;
            }

            if (_depth != null)
            {
                _depth.Release();
                UnityEngine.Object.Destroy(_depth);
            }

            _depth = new RenderTexture(like.width, like.height, 24, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
            _depth.antiAliasing = aa;
            _depth.hideFlags = HideFlags.HideAndDontSave;
            _depth.Create();
            return _depth;
        }

        private static void Say(string line)
        {
            if (Hooks.Info != null)
            {
                Hooks.Info("headset menu: " + line);
            }
        }

        // What the eyes were given, as a picture beside the plugin: left eye on the left, at
        // reduced size. For finding out what a headset shows without being in it.
        private static void Capture(RenderTexture wide, RenderTexture[] eyes)
        {
            const int w = 768, h = 768;
            RenderTexture small = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D picture = new Texture2D(w * 2, h, TextureFormat.RGB24, false);

            try
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    if (eyes != null)
                    {
                        Graphics.Blit(eyes[eye], small);
                    }
                    else
                    {
                        Graphics.Blit(wide, small, new Vector2(0.5f, 1f), new Vector2(eye * 0.5f, 0f));
                    }

                    RenderTexture.active = small;
                    picture.ReadPixels(new Rect(0, 0, w, h), eye * w, 0, false);
                }

                picture.Apply(false);

                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "headset-menu-capture.png");
                System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(picture));
                Say("the eyes as drawn are in " + path);
            }
            catch (Exception ex)
            {
                Say("the capture failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            finally
            {
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(small);
                UnityEngine.Object.Destroy(picture);
            }
        }

        // A camera of our own, never enabled: it renders only when told to, so nothing that walks
        // the scene's cameras meets it.
        private static Camera EnsureCamera()
        {
            if (_camera != null)
            {
                return _camera;
            }

            GameObject holder = new GameObject("VamDlssNrWorkScale.HeadsetUi");
            holder.hideFlags = HideFlags.HideAndDontSave;

            Camera camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.clearFlags = CameraClearFlags.Depth;
            camera.renderingPath = RenderingPath.Forward;
            camera.depthTextureMode = DepthTextureMode.None;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            _camera = camera;
            return camera;
        }

        // ---- what the panels say ---------------------------------------------------------------

        internal static string Line()
        {
            if (Problem.Length != 0)
            {
                return CfgOn != null && CfgOn.Value ? Problem : "";
            }

            if (CfgOn == null || !CfgOn.Value)
            {
                return "";
            }

            if (!_live)
            {
                return "headset menu: in the scene (no headset frame from VaM DLSS yet)";
            }

            return _perEye ? "headset menu: drawn at full size, after DLSS" : "headset menu: drawn after DLSS and Neural Rendering";
        }
    }
}
