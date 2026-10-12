#!/usr/bin/env python3
"""
FirePit Tuya ball-valve shim.

Bridges the .NET app to the WiFi "Ratio Valve Controller", which speaks Tuya
protocol 3.5 (no .NET library supports it). Holds ONE persistent tinytuya
connection and exposes a tiny localhost HTTP API:

  GET  /status              -> {"online":bool,"position":int,"mode":str}
  POST /position  {"position": 0..100}  -> set DP 101 (and DP 102 OPEN/CLOSE) -> new status

DP map (verified on the device):
  DP 101 = position 0..100 (%), drives the valve directly.
  DP 102 = "OPEN"/"CLOSE"; kept aligned with position (OPEN when >0, CLOSE at 0).

Credentials come from the environment (never hardcode secrets):
  FIREPIT_VALVE_ID, FIREPIT_VALVE_IP, FIREPIT_VALVE_KEY, FIREPIT_VALVE_VERSION
Listen address: FIREPIT_VALVE_BIND (default 127.0.0.1:5006).
"""

import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import tinytuya

DEV_ID = os.environ["FIREPIT_VALVE_ID"]
DEV_IP = os.environ["FIREPIT_VALVE_IP"]
DEV_KEY = os.environ["FIREPIT_VALVE_KEY"]
DEV_VER = float(os.environ.get("FIREPIT_VALVE_VERSION", "3.5"))
BIND = os.environ.get("FIREPIT_VALVE_BIND", "127.0.0.1:5006")

DP_POSITION = "101"
DP_MODE = "102"

_lock = threading.Lock()
_dev = None


def log(msg: str) -> None:
    print(f"[firepit-valve] {msg}", flush=True)


def _device():
    global _dev
    if _dev is None:
        d = tinytuya.Device(DEV_ID, DEV_IP, DEV_KEY, version=DEV_VER)
        d.set_socketPersistent(True)
        d.set_socketTimeout(5)
        _dev = d
        log(f"connected to valve {DEV_IP} (protocol {DEV_VER})")
    return _dev


def _reset_device():
    global _dev
    _dev = None


def read_status() -> dict:
    with _lock:
        try:
            s = _device().status()
            dps = s.get("dps", {}) if isinstance(s, dict) else {}
            if not dps and isinstance(s, dict) and s.get("Error"):
                _reset_device()
                return {"online": False, "error": s.get("Error")}
            return {
                "online": True,
                "position": int(dps.get(DP_POSITION, 0)),
                "mode": dps.get(DP_MODE, "?"),
            }
        except Exception as ex:  # noqa: BLE001
            _reset_device()
            return {"online": False, "error": str(ex)}


SET_ATTEMPTS = 2  # initial try + one retry (worst case ~2x(set+readback) of socket timeout)


def _try_set(pct: int) -> str | None:
    """One set + read-back. Returns None if the valve confirms pct, else a reason string."""
    dev = _device()
    r = dev.set_multiple_values({DP_POSITION: pct, DP_MODE: "OPEN" if pct > 0 else "CLOSE"})
    if isinstance(r, dict) and r.get("Error"):
        return f"set rejected: {r.get('Error')}"
    s = dev.status()
    dps = s.get("dps", {}) if isinstance(s, dict) else {}
    if not dps:
        return f"read-back failed: {s.get('Error') if isinstance(s, dict) else s}"
    got = int(dps.get(DP_POSITION, -1))
    return None if got == pct else f"read-back {got}% != requested {pct}%"


def set_position(pct: int) -> dict:
    pct = max(0, min(100, int(pct)))
    why = "no attempt"
    with _lock:
        for attempt in range(1, SET_ATTEMPTS + 1):
            try:
                why = _try_set(pct)
            except Exception as ex:  # noqa: BLE001
                why = str(ex)
            if why is None:
                log(f"position set to {pct}% (confirmed, attempt {attempt})")
                break
            log(f"set {pct}% attempt {attempt}/{SET_ATTEMPTS} failed: {why}")
            _reset_device()  # fresh connection for the retry
    if why is not None:
        return {"online": False, "error": f"valve did not confirm {pct}%: {why}", "requested": pct}
    return read_status()


class Handler(BaseHTTPRequestHandler):
    def _send(self, code: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.rstrip("/") in ("/status", ""):
            self._send(200, read_status())
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path.rstrip("/") != "/position":
            self._send(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            data = json.loads(self.rfile.read(length) or b"{}")
            pct = int(data["position"])
        except Exception as ex:  # noqa: BLE001
            self._send(400, {"error": f"bad request: {ex}"})
            return
        self._send(200, set_position(pct))

    def log_message(self, *args):  # silence default stderr access log
        pass


def main():
    host, port = BIND.split(":")
    log(f"starting on {BIND}, valve {DEV_IP} id={DEV_ID[:6]}… proto {DEV_VER}")
    ThreadingHTTPServer((host, int(port)), Handler).serve_forever()


if __name__ == "__main__":
    main()
