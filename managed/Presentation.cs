// VaM DLSS - Model Resolution: how the game's window reaches the screen in monitor mode.
//
// The window itself is made flip-model before this plugin is loaded -- by the preloader patcher
// (Early.cs) and the native half (vws_flip.h). Here are its two settings, the say over whether the
// frames are paced (only while VaM DLSS's frame generation is on: without it there is nothing to
// spread, and the queue would only be delay), and the lines that tell what became of it.

using BepInEx.Configuration;
using UnityEngine;

namespace VamDlssNrWorkScale
{
    internal static class Presentation
    {
        internal static ConfigEntry<bool> CfgFlip, CfgPace;

        internal static string Status = "";

        private static float _statusAt;
        private static bool _pacing;
        private static int _multiplier;

        internal static void Tick(float now)
        {
            if (CfgFlip == null || !Native.Loaded || now < _statusAt)
            {
                return;
            }

            _statusAt = now + 0.5f;

            if (UnityEngine.XR.XRSettings.enabled)
            {
                Status = "";
                return;
            }

            int state;
            uint width, height, presents, tearing;
            Native.FlipStatus(out state, out width, out height, out presents, out tearing);

            ConfigEntry<bool> generation = ControlPanel.Mod<bool>("CfgFrameGen");
            ConfigEntry<int> multiplier = ControlPanel.Mod<int>("CfgFgMultiplier");
            ConfigEntry<bool> modPacing = ControlPanel.Mod<bool>("CfgFgPacing");
            bool pace = state == 2 && CfgPace != null && CfgPace.Value && generation != null && generation.Value;
            int times = multiplier != null ? Mathf.Clamp(multiplier.Value, 2, 4) : 2;

            if (pace != _pacing || (pace && times != _multiplier))
            {
                Native.FlipPace(pace, times);
                _pacing = pace;
                _multiplier = times;
            }

            string line =
                state == 2 ? "window: flip model, " + width + "x" + height + (tearing != 0 && !pace ? ", tearing allowed" : "") :
                state == 3 ? "window: old way -- VaM's own anti-aliasing is on" :
                state == 4 ? "window: flip model failed (see the log)" :
                state == 1 ? (now > 20f ? "window: old way -- armed too late" : "") :
                CfgFlip.Value ? "window: old way until VaM is restarted" : "";

            if (pace)
            {
                float rate, refresh, each, queue;
                uint dropped;
                Native.FlipPaceStatus(out rate, out refresh, out each, out queue, out dropped);

                if (rate > 0f)
                {
                    line += "\npacing: " + rate.ToString("F0") + "/s on " + refresh.ToString("F0") + " Hz, " + each.ToString("F2") + " refreshes each" +
                        (queue >= 0f ? ", " + queue.ToString("F0") + " waiting" : "") + (dropped != 0 ? ", " + dropped + " left out" : "");

                    if (rate > refresh * 1.03f)
                    {
                        line += "\nmore frames than the screen shows: lower the multiplier";
                    }
                    else if (modPacing != null && modPacing.Value)
                    {
                        line += "\nswitch \"even pacing\" off: the queue does it";
                    }
                }
            }

            Status = line;
        }
    }
}
