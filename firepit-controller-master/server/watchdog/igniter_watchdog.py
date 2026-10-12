#!/usr/bin/env python3
"""
FirePit independent igniter watchdog.

Owns ONE relay — Relay B = BCM 6 — exclusively, in series with Relay A (owned by the
main app, BCM 5) on the Eaton continuous-ignition module's 12V control-enable input.
The main app refreshes a dedicated heartbeat file on every browser hold ping. This
watchdog:

  * holds Relay B armed (HIGH) only while the heartbeat is fresh,
  * on a STALE heartbeat: drives Relay B LOW immediately -> series broken -> igniter OFF.

Because both relays are in series, dropping Relay B alone cuts the igniter regardless
of whatever state the (possibly crashed/hung) main app left Relay A in.

Unlike the gas watchdog, this one never reboots the Pi: losing igniter control isn't a
system-health emergency the way losing gas-relay control is, so a stale igniter
heartbeat just means "stop sparking," not "something is badly wrong, recover by force."

Deliberately tiny and dependency-free (only stdlib + pinctrl), same as watchdog.py.
"""

import os
import subprocess
import sys
import time

HEARTBEAT_PATH = os.environ.get("FIREPIT_IGNITER_HEARTBEAT", "/run/firepit/igniter-heartbeat")
RELAY_PIN = os.environ.get("FIREPIT_IGNITER_WATCHDOG_PIN", "6")          # BCM 6 = Relay B
POLL_S = float(os.environ.get("POLL_S", "0.2"))
CUT_S = float(os.environ.get("FIREPIT_IGNITER_CUT_S", "1.5"))            # stale this long -> cut igniter


def log(msg: str) -> None:
    print(f"[firepit-igniter-watchdog] {msg}", flush=True)


def set_pin(high: bool) -> None:
    state = "dh" if high else "dl"
    try:
        subprocess.run(["pinctrl", "set", RELAY_PIN, "op", state],
                       check=True, capture_output=True)
    except Exception as ex:  # noqa: BLE001 - must never die here
        log(f"ERROR: failed to set Relay B (BCM{RELAY_PIN}) {'HIGH' if high else 'LOW'}: {ex}")


def heartbeat_age():
    """Seconds since the heartbeat file was last modified, or None if missing."""
    try:
        return time.time() - os.path.getmtime(HEARTBEAT_PATH)
    except OSError:
        return None


def main() -> int:
    log(f"starting. heartbeat={HEARTBEAT_PATH} pin=BCM{RELAY_PIN} cut={CUT_S}s poll={POLL_S}s")

    set_pin(False)          # start SAFE: Relay B low, igniter cannot be enabled
    armed = False

    while True:
        age = heartbeat_age()
        fresh = age is not None and age < CUT_S

        if fresh:
            if not armed:
                set_pin(True)
                armed = True
                log("heartbeat fresh — Relay B armed HIGH (igniter enabled).")
        else:
            if armed:
                set_pin(False)
                armed = False
                shown = "missing" if age is None else f"{age:.1f}s"
                log(f"heartbeat STALE ({shown}) — Relay B driven LOW, IGNITER OFF.")

        time.sleep(POLL_S)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        set_pin(False)
        sys.exit(0)
