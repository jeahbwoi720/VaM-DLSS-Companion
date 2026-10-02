using System;
using UnityEngine;

namespace Jeahbwoi720
{
    // In-headset controls for VaM DLSS (UncleBurrito's DLSS / Neural Rendering mod).
    //
    // Add this as a Session Plugin and open its custom UI: the same settings as VaM DLSS's F10
    // panel, as VaM sliders, toggles and popups you can work with the controllers' pointer.
    //
    // This script is only the panel's frame. VaM compiles scripts in a sandbox with no reflection
    // and no file access, so a script cannot reach a BepInEx plugin's settings. The controls are
    // put into this panel by the BepInEx plugin "VaM DLSS - Model Resolution"
    // (BepInEx\plugins\VamDlssNrWorkScale), which finds it by the constant below and binds every
    // control to the real setting, both ways. Without that plugin this shows a note and nothing else.
    //
    // Nothing is stored with the plugin or in a preset: the settings live in VaM DLSS's own .cfg.
    public class VaMVrNrControl : MVRScript
    {
        // What the BepInEx side looks for. Changes only if the contract between the two does.
        public const int VamDlssControlHost = 1;

        // Both written by the BepInEx side: the text of the status box, and that it has attached.
        public JSONStorableString hostStatus;
        public bool hostAttached;

        private float _warnAt;

        public override void Init()
        {
            hostStatus = new JSONStorableString("Status", "VaM DLSS controls: starting...");
            UIDynamicTextField box = CreateTextField(hostStatus, true);

            if (box != null)
            {
                box.height = 380f;
            }

            _warnAt = Time.unscaledTime + 6f;
        }

        private void Update()
        {
            if (hostAttached || hostStatus == null || _warnAt < 0f || Time.unscaledTime < _warnAt)
            {
                return;
            }

            _warnAt = -1f;
            hostStatus.val = "Nothing to control yet.\n\n" +
                "This panel is filled in by the BepInEx plugin \"VaM DLSS - Model Resolution\" " +
                "(BepInEx\\plugins\\VamDlssNrWorkScale), which needs UncleBurrito's VaM DLSS. " +
                "One of the two is not running; BepInEx\\LogOutput.log says which.";
        }
    }
}
