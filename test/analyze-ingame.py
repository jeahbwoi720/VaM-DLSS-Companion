"""Turns the in-game self-test's screenshots into the comparisons that say whether it worked.

    python test/analyze-ingame.py [test/out/ingame]

The scene is frozen for the whole run, so two screenshots differ only by what Neural Rendering and
the resolve did. What should hold:

  * frame-only (the resolve handing the frame back with the edit left out) is the NR-off picture;
  * 100% twice in a row differ only by the network's own frame-to-frame variation -- that is the
    noise floor every other number is read against;
  * a reduced model resolution lands close to 100% and far from NR-off: the edit survived;
  * a reduced model resolution keeps the frame's own sharpness, where "classic" (the network's small
    picture simply enlarged) loses it.
"""
import glob
import os
import sys

import numpy as np
from PIL import Image


def load(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float32)


def luma(img):
    return img @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)


def sharpness(img):
    """Variance of a 4-neighbour Laplacian of luma: falls when fine detail is lost."""
    y = luma(img)
    lap = 4 * y[1:-1, 1:-1] - y[:-2, 1:-1] - y[2:, 1:-1] - y[1:-1, :-2] - y[1:-1, 2:]
    return float(lap.var())


def diff(a, b):
    d = np.abs(a - b)
    return float(d.mean()), float(np.percentile(d, 99)), float(d.max())


def corr(a, b):
    x, y = luma(a).ravel(), luma(b).ravel()
    x, y = x - x.mean(), y - y.mean()
    den = float(np.sqrt((x * x).sum() * (y * y).sum()))
    return float((x * y).sum() / den) if den > 0 else 1.0


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "out", "ingame")
    shots = {}

    for path in sorted(glob.glob(os.path.join(root, "step-*.png"))):
        label = os.path.basename(path)[len("step-00-"):-len(".png")]
        shots[label] = load(path)

    if not shots:
        print("no screenshots in", root)
        return 2

    first = next(iter(shots.values()))
    print(f"{len(shots)} screenshots, {first.shape[1]}x{first.shape[0]}")
    print()
    print(f"{'shot':22} {'mean':>7} {'sharpness':>10}")

    for label, img in shots.items():
        print(f"{label:22} {img.mean():7.2f} {sharpness(img):10.2f}")

    def compare(a, b, what):
        if a not in shots or b not in shots:
            print(f"{a} vs {b}: missing")
            return None

        if shots[a].shape != shots[b].shape:
            print(f"{a} vs {b}: sizes differ {shots[a].shape} / {shots[b].shape}")
            return None

        mean, p99, mx = diff(shots[a], shots[b])
        print(f"{a:18} vs {b:18} mean {mean:6.3f}  p99 {p99:6.1f}  max {mx:5.0f}  corr {corr(shots[a], shots[b]):.5f}   {what}")
        return mean

    print()
    print("differences, in 8-bit steps of the encoded picture:")
    floor = compare("nr-100", "nr-100-again", "noise floor: the network's own variation")
    compare("nr-off", "nr-off-again", "the scene really is frozen")
    effect = compare("nr-off", "nr-100", "the size of the network's edit")
    compare("nr-off", "nr-050-frame-only", "the resolve with the edit left out is the frame")
    half = compare("nr-100", "nr-050", "50% against 100%")
    compare("nr-100", "nr-075", "75% against 100%")
    compare("nr-100", "nr-025", "25% against 100%")
    compare("nr-off", "nr-050", "50% against NR off: the edit is there")
    compare("nr-050", "nr-050-classic", "matched residual against classic")

    verdicts = []

    if "nr-off" in shots and "nr-050-frame-only" in shots:
        mean, p99, _ = diff(shots["nr-off"], shots["nr-050-frame-only"])
        verdicts.append((mean < 1.0 and p99 <= 3.0, f"frame-only reproduces the NR-off frame (mean {mean:.3f}, p99 {p99:.1f})"))

    if effect is not None and half is not None:
        verdicts.append((effect > 0.2, f"the network changes the picture at 100% (mean {effect:.3f})"))
        verdicts.append((half < effect, f"50% is closer to 100% ({half:.3f}) than NR-off is ({effect:.3f})"))

    if "nr-off" in shots and "nr-050" in shots:
        verdicts.append((corr(shots["nr-off"], shots["nr-050"]) > 0.97, "50% is the same picture, the right way up (correlation with NR off)"))

    if all(k in shots for k in ("nr-100", "nr-050", "nr-050-classic")):
        s100, s050, scl = sharpness(shots["nr-100"]), sharpness(shots["nr-050"]), sharpness(shots["nr-050-classic"])
        verdicts.append((s050 > scl, f"matched residual keeps more detail than classic (sharpness {s050:.1f} vs {scl:.1f}; 100% is {s100:.1f})"))

    print()

    for ok, text in verdicts:
        print(("PASS  " if ok else "FAIL  ") + text)

    # Side-by-side crops at 1:1, for looking at.
    order = [k for k in ("nr-off", "nr-100", "nr-075", "nr-050", "nr-025", "nr-050-classic") if k in shots]
    h, w = first.shape[:2]

    for name, (cx, cy, size) in {"centre": (w // 2, h // 2, 420), "upper": (w // 2, h // 3, 420)}.items():
        x0, y0 = max(0, cx - size // 2), max(0, cy - size // 2)
        tiles = [shots[k][y0:y0 + size, x0:x0 + size] for k in order]
        strip = np.concatenate(tiles, axis=1).astype(np.uint8)
        out = os.path.join(root, f"compare-{name}.png")
        Image.fromarray(strip).save(out)
        print(f"wrote {out}  ({' | '.join(order)})")

    return 0 if all(ok for ok, _ in verdicts) else 1


if __name__ == "__main__":
    sys.exit(main())
