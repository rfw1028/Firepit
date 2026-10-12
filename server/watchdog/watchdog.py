#!/usr/bin/env python3
"""
FirePit independent safety watchdog.

Owns ONE series gas relay — CH3 = BCM 21 — exclusively. The main control app owns
CH1/CH2 (BCM 26, 20) and continuously refreshes a heartbeat file. This watchdog:

  * holds CH3 armed (HIGH) only while the heartbeat is fresh,
  * on a STALE heartbeat: drives CH3 LOW immediately  -> series broken -> GAS OFF,
  * if it STAYS stale past the reboot threshold: reboots the Pi.

Because all three relays are in series, dropping CH3 alone shuts gas regardless of
whatever state the (possibly crashed/hung) main app left CH1/CH2 in.

Deliberately tiny and dependency-free (only stdlib + pinctrl) so the safety monitor
is far simpler — and thus more trustworthy — than the app it guards.

Boot-loop safe: it will NOT reboot until it has seen at least one fresh heartbeat
(i.e. the app came up healthy at least once). If the app never starts, CH3 just
stays LOW (gas off) and the watchdog waits.
"""

import os
import subprocess
import sys
import time

HEARTBEAT_PATH = os.environ.get("FIREPIT_HEARTBEAT", "/run/firepit/heartbeat")
RELAY_PIN = os.environ.get("FIREPIT_WATCHDOG_PIN", "21")        # BCM 21 = CH3
POLL_S = float(os.environ.get("FIREPIT_POLL_S", "0.2"))
GAS_CUT_S = float(os.environ.get("FIREPIT_GAS_CUT_S", "1.0"))   # stale this long -> cut gas
REBOOT_S = float(os.environ.get("FIREPIT_REBOOT_S", "5.0"))     # stale this long (armed) -> reboot
ALLOW_REBOOT = os.environ.get("FIREPIT_ALLOW_REBOOT", "1") != "0"


def log(msg: str) -> None:
    print(f"[firepit-watchdog] {msg}", flush=True)


def set_pin(high: bool) -> None:
    state = "dh" if high else "dl"
    try:
        subprocess.run(["pinctrl", "set", RELAY_PIN, "op", state],
                       check=True, capture_output=True)
    except Exception as ex:  # noqa: BLE001 - must never die here
        log(f"ERROR: failed to set CH3 (BCM{RELAY_PIN}) {'HIGH' if high else 'LOW'}: {ex}")


def heartbeat_age():
    """Seconds since the heartbeat file was last modified, or None if missing."""
    try:
        return time.time() - os.path.getmtime(HEARTBEAT_PATH)
    except OSError:
        return None


def reboot() -> None:
    log("REBOOTING Pi now (heartbeat stale past reboot threshold).")
    if not ALLOW_REBOOT:
        log("reboot suppressed (FIREPIT_ALLOW_REBOOT=0).")
        return
    try:
        subprocess.run(["systemctl", "reboot"], check=False)
    except Exception as ex:  # noqa: BLE001
        log(f"ERROR: reboot failed: {ex}")
        subprocess.run(["/sbin/reboot"], check=False)


def main() -> int:
    log(f"starting. heartbeat={HEARTBEAT_PATH} pin=BCM{RELAY_PIN} "
        f"gas_cut={GAS_CUT_S}s reboot={REBOOT_S}s poll={POLL_S}s allow_reboot={ALLOW_REBOOT}")

    set_pin(False)          # start SAFE: CH3 low, gas cannot flow
    armed = False           # have we ever seen a healthy app?
    gas_enabled = False     # is CH3 currently HIGH
    stale_since = None      # when the current stale episode began

    while True:
        now = time.time()
        age = heartbeat_age()
        fresh = age is not None and age < GAS_CUT_S

        if fresh:
            if not armed:
                armed = True
                log("first fresh heartbeat seen — watchdog ARMED.")
            if not gas_enabled:
                set_pin(True)
                gas_enabled = True
                log("heartbeat fresh — CH3 armed HIGH (gas enabled).")
            stale_since = None
        else:
            if gas_enabled:
                set_pin(False)
                gas_enabled = False
                shown = "missing" if age is None else f"{age:.1f}s"
                log(f"heartbeat STALE ({shown}) — CH3 driven LOW, GAS OFF.")
            if armed:
                if stale_since is None:
                    stale_since = now
                elif (now - stale_since) >= REBOOT_S:
                    reboot()
                    return 0  # stop enforcing; reboot is in progress
            # not armed yet -> app never came up healthy; hold safe, do NOT reboot.

        time.sleep(POLL_S)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        set_pin(False)
        sys.exit(0)
