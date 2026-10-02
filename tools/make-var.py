"""Packs the in-headset control panel's session script as a VaM package.

    python tools/make-var.py <output dir> [version]

Writes jeahbwoi720.VaMVrNrControl.<version>.var: meta.json plus the Custom/ tree under vam/.

A .var is a zip that VaM reads with SharpZipLib, which is strict about the local and central headers
agreeing -- so every entry is written whole, from a fresh ZipInfo, with no data descriptor.
"""
import json
import os
import sys
import zipfile

CREATOR = "jeahbwoi720"
PACKAGE = "VaMVrNrControl"
DEFAULT_VERSION = 2

DESCRIPTION = (
    "In-headset controls for VaM DLSS (UncleBurrito's DLSS Super Resolution / Neural Rendering mod).\n\n"
    "VaM DLSS's own panel (F10) is a desktop overlay. This puts the same settings into a session "
    "plugin's UI, where the controllers' pointer can reach them in VR: Neural Rendering on/off, "
    "model resolution, intensity, local tone and structure, style, passes, the per-region mask, "
    "DLSS on/off, quality and model, and frame generation - with a live status box. Either panel "
    "moves the other.\n\n"
    "REQUIRES, installed separately: VaM DLSS 1.0.3 by UncleBurrito, and the BepInEx plugin "
    "'VaM DLSS - Model Resolution' 1.1.0 or newer "
    "(https://github.com/jeahbwoi720/VaM-DLSS-Model-Resolution). The script in this package is "
    "only the panel's frame: VaM's script sandbox cannot reach a BepInEx plugin's settings, so that "
    "plugin fills the panel in. Without it the panel shows a note saying so."
)

INSTRUCTIONS = (
    "Main UI > Session Plugins > Add Plugin > Select File, pick VaMVrNrControl.cslist from this "
    "package, then Open Custom UI. To have it every session, save it into your session plugin "
    "defaults (Session Plugin Presets > Change User Defaults > Set Current As User Defaults)."
)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "dist", "AddonPackages")
    version = int(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_VERSION
    source = os.path.join(root, "vam")

    files = []

    for folder, _, names in os.walk(os.path.join(source, "Custom")):
        for name in sorted(names):
            full = os.path.join(folder, name)
            files.append((os.path.relpath(full, source).replace("\\", "/"), full))

    files.sort()

    if not files:
        print("nothing under", source)
        return 1

    meta = {
        "licenseType": "CC BY-SA",
        "creatorName": CREATOR,
        "packageName": PACKAGE,
        "packageVersion": str(version),
        "standardReferenceVersionOption": "Latest",
        "scriptReferenceVersionOption": "Exact",
        "description": DESCRIPTION,
        "credits": "jeahbwoi720. VaM DLSS is by UncleBurrito.",
        "instructions": INSTRUCTIONS,
        "promotionalLink": "https://github.com/jeahbwoi720/VaM-DLSS-Model-Resolution",
        "contentList": [name for name, _ in files],
        "dependencies": {},
        "customOptions": {"preloadMorphs": "false"},
        "hadReferenceIssues": "false",
        "referenceIssues": [],
    }

    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, "%s.%s.%d.var" % (CREATOR, PACKAGE, version))

    if os.path.exists(out):
        os.remove(out)

    stamp = (2026, 1, 1, 0, 0, 0)  # fixed, so the same sources give the same package

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        info = zipfile.ZipInfo("meta.json", date_time=stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, json.dumps(meta, indent=3).encode("utf-8"))

        for name, full in files:
            with open(full, "rb") as f:
                data = f.read()

            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)

    # Read it back the way a strict reader would: every entry whole, sizes agreeing, no descriptors.
    with zipfile.ZipFile(out) as z:
        bad = z.testzip()

        if bad is not None:
            print("corrupt entry:", bad)
            return 1

        for info in z.infolist():
            if info.flag_bits & 0x08:
                print("entry uses a data descriptor:", info.filename)
                return 1

        json.loads(z.read("meta.json").decode("utf-8"))
        print(out)

        for info in z.infolist():
            print("   %-68s %6d" % (info.filename, info.file_size))

    return 0


if __name__ == "__main__":
    sys.exit(main())
