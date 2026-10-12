#!/usr/bin/env python3
"""Synthetic-scene self-test for flame_detector.FlameDetector (no camera needed).

Run:  python3 flame_selftest.py
It proves the LOGIC (colour fusion + motion + hysteresis); it does NOT replace testing with a
real flame in front of the real camera.
"""
import sys

import numpy as np

import flame_detector as fd

H, W = fd.H, fd.W
rng = np.random.default_rng(1)


def background():
    bg = np.zeros((H, W, 3), np.uint8)
    bg[..., 1] = 90      # dark-green lawn
    bg[..., 0] = 30
    bg[..., 2] = 40
    return bg


def noisy(bg):
    return np.clip(bg.astype(np.int16) + rng.integers(-6, 7, bg.shape), 0, 255).astype(np.uint8)


def ellipse(cx, cy, rx, ry):
    yy, xx = np.mgrid[0:H, 0:W]
    return ((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2 <= 1.0


def flame_frame(t, strength=1.0):
    """Flickering flame: orange/yellow tongues whose shape jitters every frame."""
    f = noisy(background())
    cx, cy = 90 + 4 * np.sin(t * 1.3), 120
    body = ellipse(cx, cy, 26, 38 + 6 * rng.random())
    tongues = ellipse(cx + rng.integers(-8, 9), cy - 30 - rng.integers(0, 14), 12, 22)
    m = (body | tongues) & (rng.random((H, W)) < 0.9 * strength)
    yellow = rng.random((H, W)) < 0.45
    f[m & ~yellow] = (255, 140, 25)
    f[m & yellow] = (255, 215, 70)
    return f


def run(name, frames, expect_on, expect_final=None):
    d = fd.FlameDetector()
    states = [d.process(fr)[0] for fr in frames]
    ever_on = any(states)
    ok = ever_on == expect_on and (expect_final is None or states[-1] == expect_final)
    first = states.index(True) / fd.FPS if ever_on else None
    print(f"{'PASS' if ok else 'FAIL'}  {name:<46} ever_on={ever_on} final={states[-1]} "
          f"first_on={first if first is None else round(first, 1)}s  last_stats={d.stats}")
    return ok


results = []

# 1. Empty scene
results.append(run("empty lawn, sensor noise", [noisy(background()) for _ in range(120)], False))

# 2. Static orange object (lava rock / sunlit brick) — colour matches but does not move
static = background()
static[ellipse(90, 120, 30, 30)] = (240, 130, 30)
results.append(run("static orange blob (rocks)", [noisy(static) for _ in range(120)], False))

# 3. Moving non-flame-coloured object
mov = []
for t in range(120):
    f = noisy(background())
    f[ellipse(40 + t, 100, 25, 25)] = (30, 60, 220)     # blue thing walking by
    mov.append(f)
results.append(run("moving blue object", mov, False))

# 4. Whole frame orange (sunset / camera washed out) with flicker — must be rejected
wash = []
for t in range(120):
    f = np.zeros((H, W, 3), np.uint8)
    f[...] = (245, 150, 40)
    f = np.clip(f.astype(np.int16) + rng.integers(-25, 26, f.shape), 0, 255).astype(np.uint8)
    wash.append(f)
results.append(run("orange-washed frame (>50% colour)", wash, False))

# 5. Tiny orange specks moving (a few leaves) — too small
leaves = []
for t in range(120):
    f = noisy(background())
    for k in range(4):
        cx, cy = (30 + 70 * k + 3 * t) % W, 40 + 30 * k
        f[ellipse(cx, cy, 3, 3)] = (230, 120, 30)
    leaves.append(f)
results.append(run("tiny moving orange specks (leaves)", leaves, False))

# 6. Real flicker flame, then removed: ON within a few seconds, OFF within ~3 s of removal
seq = [flame_frame(t) for t in range(80)] + [noisy(background()) for _ in range(100)]
results.append(run("flickering flame then extinguished", seq, True, expect_final=False))

# 7. Flame on from the start must stay on (no chatter) for 15 s
d = fd.FlameDetector()
st = [d.process(flame_frame(t))[0] for t in range(150)]
on_at = st.index(True) if True in st else None
chatter = on_at is not None and not all(st[on_at:])
ok7 = on_at is not None and not chatter
print(f"{'PASS' if ok7 else 'FAIL'}  {'sustained flame stays ON (no chatter)':<46} on_at={on_at} chatter={chatter}")
results.append(ok7)

# 8. ROI excludes the flame -> no detection
d = fd.FlameDetector(roi=(0.6, 0.0, 1.0, 1.0))
st = [d.process(flame_frame(t))[0] for t in range(100)]
ok8 = not any(st)
print(f"{'PASS' if ok8 else 'FAIL'}  {'flame outside ROI is ignored':<46} ever_on={any(st)}")
results.append(ok8)

print("ALL PASS" if all(results) else "SOME FAILED")
sys.exit(0 if all(results) else 1)
