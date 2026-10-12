#!/usr/bin/env python3
"""
FirePit flame detector (separate service; ADVISORY ONLY — not a certified flame sensor).

Reads frames from the camera streamer (camera_stream.py, :8081) via ffmpeg, downscaled to
320x176, and decides "is a physical flame present?" by fusing:

  1. COLOR FUSION  — three independent flame-color classifiers (RGB rule, HSV rule, YCbCr
                     rule) vote per pixel; a pixel is flame-colored if >= 2 of 3 agree.
  2. MOTION        — flame-colored pixels only count if they also move: frame-to-frame
                     difference OR temporal flicker (colour on/off toggling over the last
                     ~0.6 s). Static orange things (lava rock, sunlit bricks) are rejected.
  3. SPATIAL       — the moving flame-colored pixels must fill >= MIN_BLOCKS 16x16 blocks.
  4. TEMPORAL      — hysteresis: ON needs sustained hits (>= ON_HITS of the last 2 s);
                     OFF comes quickly (<= OFF_HITS of the last 3 s). Falsely reporting a
                     flame is the dangerous error, so the detector is slow to say "yes"
                     and fast to say "no".

OUTPUT (checked by the firepit controller every ~2 s):
  * file  /run/firepit-flame/flame.json   (written atomically every 0.5 s)
  * HTTP  127.0.0.1:5007/flame            (same JSON + "age_s");  /debug.png for tuning

  {"flame": bool, "confidence": 0..1, "camera_ok": bool, "ts": <unix seconds>, ...}

CONSUMER RULE (fail-safe): treat the flag as "NO FLAME" unless  flame==true AND
camera_ok==true AND (now - ts) < 5 s.  A stale file, missing file or dead camera = no flame.

Config via environment: FIREPIT_FLAME_URL, FIREPIT_FLAME_OUT, FIREPIT_FLAME_BIND,
FIREPIT_FLAME_ROI ("x0,y0,x1,y1" as fractions of the frame; only this region is analysed).
"""

import json
import os
import struct
import subprocess
import threading
import time
import zlib
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np

W, H = 320, 176            # analysis size (H divisible by BLOCK)
FPS = 10                   # camera streamer delivers ~10 fps
BLOCK = 8

URL = os.environ.get("FIREPIT_FLAME_URL", "http://127.0.0.1:8081/stream")
OUT = os.environ.get("FIREPIT_FLAME_OUT", "/run/firepit-flame/flame.json")
BIND = os.environ.get("FIREPIT_FLAME_BIND", "127.0.0.1:5007")
ROI = tuple(float(v) for v in os.environ.get("FIREPIT_FLAME_ROI", "0,0,1,1").split(","))

# ---- tunables ----
DIFF_THR = 14              # gray-level change that counts as "moving"
FLICKER_WIN = 6            # frames of colour history for the flicker test
FLICKER_MIN_TOGGLES = 2
BLOCK_FRAC = 0.045         # fraction of a block that must be moving flame-colour
RECUR_WIN, RECUR_MIN = 10, 4  # a block counts only if active in >= 4 of the last 10 frames
MIN_BLOCKS = 2             # blocks needed for a per-frame "hit" (small ground-level flames)
MONO_CHROMA = 3.0          # mean R-G + G-B below this => grayscale (IR night mode) frame
MONO_BRIGHT = 200          # gray level that counts as flame in a grayscale frame
MAX_COLOR_FRAC = 0.50      # >50% of ROI flame-coloured = washed out / orange scene, not a flame
ON_HITS, ON_WINDOW = 6, 20     # ON  : >= 6 hits in last 20 frames (2 s)
OFF_HITS, OFF_WINDOW = 2, 80   # OFF : <= 2 hits in last 80 frames (8 s); small flames flicker in and out of detection
STALE_S = 3.0              # no frame for this long => camera_ok False, flame False


def log(msg):
    print(f"[firepit-flame] {msg}", flush=True)


class FlameDetector:
    def __init__(self, roi=(0.0, 0.0, 1.0, 1.0)):
        x0, y0, x1, y1 = roi
        self.roi = np.zeros((H, W), bool)
        self.roi[int(y0 * H):max(int(y1 * H), 1), int(x0 * W):max(int(x1 * W), 1)] = True
        self.roi_n = int(self.roi.sum())
        self.prev_gray = None
        self.color_hist = deque(maxlen=FLICKER_WIN)
        self.blk_hist = deque(maxlen=RECUR_WIN)
        self.hits = deque(maxlen=max(ON_WINDOW, OFF_WINDOW))
        self.flame = False
        self.mono = False
        self.stats = {}
        self.last = None    # (rgb, color, active) for the debug image

    # --- stage 1: colour fusion (3 classifiers, 2-of-3 vote) ---
    @staticmethod
    def color_mask(rgb):
        r = rgb[..., 0].astype(np.int16)
        g = rgb[..., 1].astype(np.int16)
        b = rgb[..., 2].astype(np.int16)
        mx = np.maximum(np.maximum(r, g), b)
        mn = np.minimum(np.minimum(r, g), b)
        span = mx - mn

        # (a) RGB rule (Chen et al.): R bright, R >= G > B, saturation rises as R falls.
        sat = 255.0 * span / np.maximum(mx, 1)
        rgb_rule = (r > 170) & (r >= g) & (g > b) & (sat >= (255 - r) * 60.0 / 170.0)

        # (b) HSV rule: hue 0..50 deg (red-orange-yellow), saturated, bright.
        hue_ok = (r == mx) & (g >= b) & ((g - b) * 60 <= 50 * span)
        hsv_rule = hue_ok & (sat >= 90) & (mx >= 150)

        # (c) YCbCr rule (Celik & Demirel): flame is high-Cr, low-Cb and bright.
        y = 0.299 * r + 0.587 * g + 0.114 * b
        cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b
        cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b
        ycc_rule = (y >= 120) & (cr > cb + 45) & (cr >= 140)

        votes = rgb_rule.astype(np.uint8) + hsv_rule + ycc_rule
        return votes >= 2, y

    def process(self, rgb):
        """rgb: (H, W, 3) uint8. Returns the per-frame result dict and updates state."""
        color, gray = self.color_mask(rgb)
        # Night / infrared mode: the camera delivers a grayscale picture, so flame colour does not
        # exist. Fall back to "very bright" (the flame is the brightest thing in an IR scene); the
        # motion/flicker, block and persistence stages below still apply, so static glare is ignored.
        self.mono = float(np.abs(rgb[..., 0].astype(np.int16) - rgb[..., 1]).mean()
                          + np.abs(rgb[..., 1].astype(np.int16) - rgb[..., 2]).mean()) < MONO_CHROMA
        if self.mono:
            color = gray >= MONO_BRIGHT
        color &= self.roi

        # --- stage 2: motion — frame difference OR temporal flicker ---
        if self.prev_gray is None:
            moving = np.zeros_like(color)
        else:
            moving = np.abs(gray - self.prev_gray) > DIFF_THR
        self.prev_gray = gray
        self.color_hist.append(color)
        if len(self.color_hist) >= 3:
            h = np.stack(self.color_hist)
            toggles = (h[1:] != h[:-1]).sum(axis=0)
            flicker = toggles >= FLICKER_MIN_TOGGLES
        else:
            flicker = np.zeros_like(color)
        active = color & (moving | flicker)

        # --- stage 3: spatial — enough 16x16 blocks of moving flame colour ---
        frac = active.reshape(H // BLOCK, BLOCK, W // BLOCK, BLOCK).mean(axis=(1, 3))
        blk = frac >= BLOCK_FRAC
        # A real flame keeps burning in the same blocks; drifting specks (leaves, insects) do not.
        self.blk_hist.append(blk)
        if len(self.blk_hist) >= RECUR_WIN:
            recur = np.stack(self.blk_hist).sum(axis=0) >= RECUR_MIN
            blocks = int((blk & recur).sum())
        else:
            blocks = 0   # history not yet full: no hits
        color_frac = float(color.sum()) / max(self.roi_n, 1)
        hit = blocks >= MIN_BLOCKS and color_frac <= MAX_COLOR_FRAC

        # --- stage 4: temporal hysteresis (slow ON, fast OFF) ---
        self.hits.append(hit)
        h = list(self.hits)
        on_n = sum(h[-ON_WINDOW:])
        off_n = sum(h[-OFF_WINDOW:])
        if not self.flame and len(h) >= ON_WINDOW and on_n >= ON_HITS:
            self.flame = True
            log(f"FLAME detected (hits {on_n}/{ON_WINDOW}, blocks {blocks}, color {color_frac:.3f})")
        elif self.flame and off_n <= OFF_HITS:
            self.flame = False
            log(f"flame gone (hits {off_n}/{OFF_WINDOW})")

        self.stats = {
            "hits_2s": on_n, "blocks": blocks,
            "color_frac": round(color_frac, 4),
            "motion_color_px": int(active.sum()),
        }
        self.last = (rgb, color, active)
        conf = min(1.0, on_n / (2.0 * ON_HITS)) if self.flame else 0.0
        return self.flame, round(conf, 2)

    def reset(self):
        self.prev_gray = None
        self.color_hist.clear()
        self.blk_hist.clear()
        self.hits.clear()
        self.flame = False


# ---------------------------------------------------------------- service plumbing
_lock = threading.Lock()
_det = FlameDetector(ROI)
_state = {"flame": False, "confidence": 0.0, "camera_ok": False}
_last_frame_t = 0.0
_proc = None


def _png(rgb):
    h, w, _ = rgb.shape
    raw = b"".join(b"\x00" + rgb[y].tobytes() for y in range(h))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 3)) + chunk(b"IEND", b""))


def _debug_png():
    with _lock:
        last = _det.last
        roi = _det.roi.copy()
    if last is None:
        return None
    rgb, color, active = last
    right = (rgb * 0.35).astype(np.uint8)
    right[color] = (255, 170, 0)       # amber  = flame-coloured
    right[active] = (255, 0, 0)        # red    = flame-coloured AND moving
    edge = roi ^ np.roll(roi, 1, 0) | roi ^ np.roll(roi, 1, 1)
    right[edge & roi] = (0, 200, 255)  # cyan   = ROI outline
    return _png(np.concatenate([rgb, right], axis=1))


def _snapshot():
    with _lock:
        s = dict(_state)
        s.update(_det.stats)
    now = time.time()
    s["ts"] = round(now, 3)
    return s


def _writer():
    """Publish the flag every 0.5 s; force 'no flame' if frames stop arriving."""
    global _proc
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    while True:
        with _lock:
            if time.monotonic() - _last_frame_t > STALE_S:
                if _state["camera_ok"] or _state["flame"]:
                    log("no frames — publishing flame=false, camera_ok=false")
                _state.update(flame=False, confidence=0.0, camera_ok=False)
                _det.reset()
                if _proc is not None:
                    try:
                        _proc.kill()
                    except Exception:  # noqa: BLE001
                        pass
        try:
            tmp = OUT + ".tmp"
            with open(tmp, "w") as f:
                json.dump(_snapshot(), f)
            os.replace(tmp, OUT)
        except Exception as ex:  # noqa: BLE001
            log(f"write failed: {ex}")
        time.sleep(0.5)


def _reader():
    global _proc, _last_frame_t
    nbytes = W * H * 3
    while True:
        try:
            # The MJPEG stream carries no timestamps (ffmpeg would assume 25 fps and drop
            # frames), so stamp by wall clock and pass every frame through; the streamer
            # already delivers ~FPS frames/s.
            _proc = subprocess.Popen(
                ["ffmpeg", "-hide_banner", "-loglevel", "error",
                 "-use_wallclock_as_timestamps", "1", "-i", URL,
                 "-vf", f"scale={W}:{H}:flags=area", "-fps_mode", "passthrough",
                 "-pix_fmt", "rgb24", "-f", "rawvideo", "-"],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
            with _lock:
                _last_frame_t = time.monotonic()   # grace period for ffmpeg start-up
            log("reading frames from camera stream")
            while True:
                buf = b""
                while len(buf) < nbytes:
                    chunk = _proc.stdout.read(nbytes - len(buf))
                    if not chunk:
                        raise EOFError("ffmpeg ended")
                    buf += chunk
                rgb = np.frombuffer(buf, np.uint8).reshape(H, W, 3)
                with _lock:
                    flame, conf = _det.process(rgb)
                    _state.update(flame=flame, confidence=conf, camera_ok=True)
                    _last_frame_t = time.monotonic()
        except Exception as ex:  # noqa: BLE001
            log(f"reader: {ex}")
        finally:
            if _proc is not None:
                try:
                    _proc.kill()
                    _proc.wait()
                except Exception:  # noqa: BLE001
                    pass
            with _lock:
                _state.update(flame=False, confidence=0.0, camera_ok=False)
                _det.reset()
        time.sleep(2)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, ctype, body):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        p = self.path.split("?")[0].rstrip("/")
        if p in ("", "/flame"):
            s = _snapshot()
            s["age_s"] = 0.0
            self._send(200, "application/json", json.dumps(s).encode())
        elif p == "/debug.png":
            png = _debug_png()
            if png is None:
                self._send(503, "text/plain", b"no frames yet")
            else:
                self._send(200, "image/png", png)
        else:
            self._send(404, "text/plain", b"not found")


def main():
    host, port = BIND.split(":")
    threading.Thread(target=_reader, daemon=True).start()
    threading.Thread(target=_writer, daemon=True).start()
    log(f"flag file {OUT}; http {BIND}; ROI {ROI}")
    ThreadingHTTPServer((host, int(port)), Handler).serve_forever()


if __name__ == "__main__":
    main()
