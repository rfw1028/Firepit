#!/usr/bin/env python3
"""
FirePit USB camera MJPEG streamer (separate from the gas controller app).

The camera (UVC, 32e4:9230) emits MJPEG natively, so ffmpeg just copies frames
(no re-encoding, near-zero CPU). ffmpeg only runs while at least one viewer is
connected, so the camera and CPU are idle otherwise.

  GET /            -> tiny viewer page
  GET /stream      -> multipart/x-mixed-replace MJPEG
  GET /snapshot.jpg-> latest single frame

LAN-trusted like the control page (no auth). Config via environment:
  FIREPIT_CAM_DEV (/dev/video0)  FIREPIT_CAM_SIZE (1280x720)
  FIREPIT_CAM_FPS (10)           FIREPIT_CAM_BIND (0.0.0.0:8081)
"""

import os
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DEV = os.environ.get("FIREPIT_CAM_DEV", "/dev/video0")
SIZE = os.environ.get("FIREPIT_CAM_SIZE", "1280x720")
FPS = os.environ.get("FIREPIT_CAM_FPS", "10")
BIND = os.environ.get("FIREPIT_CAM_BIND", "0.0.0.0:8081")
IDLE_STOP_S = 5.0
BOUNDARY = "frame"

_cond = threading.Condition()
_frame = None       # latest JPEG bytes
_seq = 0            # increments per frame
_viewers = 0
_proc = None
_last_viewer = time.monotonic()
_started_at = 0.0


def log(msg: str) -> None:
    print(f"[firepit-cam] {msg}", flush=True)


def _tune_camera():
    """Re-apply exposure tuning every time the stream starts: UVC controls reset to defaults when the
    camera is unplugged/replugged (e.g. when it is moved), and a one-time service start-up setting is lost."""
    try:
        subprocess.run(["v4l2-ctl", "-d", DEV, "-c",
                        os.environ.get("FIREPIT_CAM_CTRLS", "auto_exposure=3,backlight_compensation=2,brightness=-8")],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
    except Exception as e:
        log(f"camera tuning failed: {e}")


def _start_ffmpeg():
    _tune_camera()
    return subprocess.Popen(
        ["ffmpeg", "-hide_banner", "-loglevel", "error",
         "-f", "v4l2", "-input_format", "mjpeg",
         "-video_size", SIZE, "-framerate", FPS, "-i", DEV,
         "-c:v", "copy", "-f", "image2pipe", "-"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)


def _producer():
    """Run ffmpeg while viewers exist; split its output into JPEG frames."""
    global _frame, _seq, _proc, _started_at
    while True:
        with _cond:
            while _viewers == 0:
                _cond.wait()
        try:
            _proc = _start_ffmpeg()
            _started_at = time.monotonic()
            log(f"camera started ({DEV} {SIZE}@{FPS})")
            buf = b""
            while True:
                chunk = _proc.stdout.read(65536)
                if not chunk:
                    break
                buf += chunk
                while True:
                    soi = buf.find(b"\xff\xd8")
                    if soi < 0:
                        buf = b""
                        break
                    eoi = buf.find(b"\xff\xd9", soi + 2)
                    if eoi < 0:
                        buf = buf[soi:]
                        break
                    jpg, buf = buf[soi:eoi + 2], buf[eoi + 2:]
                    with _cond:
                        _frame, _seq = jpg, _seq + 1
                        _cond.notify_all()
                with _cond:
                    idle = _viewers == 0 and time.monotonic() - _last_viewer > IDLE_STOP_S
                if idle:
                    break
        except Exception as ex:  # noqa: BLE001
            log(f"camera error: {ex}")
        finally:
            if _proc:
                _proc.kill()
                _proc.wait()
                _proc = None
                log("camera stopped")
            with _cond:
                _frame = None
        time.sleep(1)


def _viewer_delta(d: int) -> None:
    global _viewers, _last_viewer
    with _cond:
        _viewers += d
        _last_viewer = time.monotonic()
        _cond.notify_all()


def _wait_frame(after_seq: int, timeout: float):
    with _cond:
        _cond.wait_for(lambda: _seq != after_seq and _frame is not None, timeout)
        return (_frame, _seq) if _frame is not None and _seq != after_seq else (None, after_seq)


PAGE = b"""<!doctype html><meta name=viewport content="width=device-width,initial-scale=1">
<title>Fire Pit Camera</title>
<body style="margin:0;background:#111;color:#ccc;font-family:sans-serif;text-align:center">
<img src="/stream" style="width:100%;max-width:1280px;display:block;margin:0 auto" alt="camera">
<p style="font-size:.85rem">Fire Pit camera</p></body>"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        path = self.path.split("?")[0].rstrip("/")
        if path == "":
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(PAGE)))
            self.end_headers()
            self.wfile.write(PAGE)
        elif path == "/snapshot.jpg":
            _viewer_delta(1)
            try:
                # Let auto-exposure settle: skip frames from the first ~1.5 s after start.
                deadline = time.monotonic() + 10
                jpg, seq = None, -1
                while time.monotonic() < deadline:
                    jpg, seq = _wait_frame(seq, 2)
                    if jpg is not None and time.monotonic() - _started_at > 1.5:
                        break
            finally:
                _viewer_delta(-1)
            if not jpg:
                self.send_error(503, "no frame from camera")
                return
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(jpg)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(jpg)
        elif path == "/stream":
            self.send_response(200)
            self.send_header("Content-Type", f"multipart/x-mixed-replace; boundary={BOUNDARY}")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            _viewer_delta(1)
            seq = -1
            # The camera only offers 60 fps at 720p and ignores lower rates, so thin the
            # stream to FPS here (keeps Wi-Fi use modest; frames are just dropped).
            min_gap, last_sent = 1.0 / float(FPS), 0.0
            try:
                while True:
                    jpg, seq = _wait_frame(seq, 10)
                    if jpg is None:
                        continue
                    now = time.monotonic()
                    if now - last_sent < min_gap:
                        continue
                    last_sent = now
                    self.wfile.write(
                        f"--{BOUNDARY}\r\nContent-Type: image/jpeg\r\nContent-Length: {len(jpg)}\r\n\r\n".encode()
                        + jpg + b"\r\n")
            except (BrokenPipeError, ConnectionResetError, TimeoutError):
                pass
            finally:
                _viewer_delta(-1)
        else:
            self.send_error(404)


def main():
    host, port = BIND.split(":")
    threading.Thread(target=_producer, daemon=True).start()
    log(f"listening on {BIND}")
    ThreadingHTTPServer((host, int(port)), Handler).serve_forever()


if __name__ == "__main__":
    main()
