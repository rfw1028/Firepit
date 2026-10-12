# FirePit deploy & safety architecture

## Fail-safe layers (defense in depth)
1. **Series relays, all-must-close** — 3 relay NO contacts in series (CH1/CH2/CH3 = BCM 26/20/21). Any one open = gas off. Any one welded still leaves two to break the circuit.
2. **Split software authority (software dead-man)** — the main app owns CH1/CH2; the independent `firepit-watchdog` owns CH3. The app writes `/run/firepit/heartbeat` continuously; if it goes stale the watchdog drives CH3 LOW (gas off) within ~1 s, then reboots if it stays stale. *Verified: an app killed with `-9` leaves CH1/CH2 stuck HIGH, but the watchdog still cuts gas via CH3.*
3. **systemd `WatchdogSec`** — a hung app (still alive but not pinging) is killed & restarted; startup force-safe re-establishes gas-off.
4. **BCM hardware watchdog** (`RuntimeWatchdogSec`) — a fully frozen Pi is reset by the SoC; boot returns GPIOs to safe.
5. **Early-boot GPIO** (`gpio=20,21,26=op,dl` in config.txt) — pins driven low by firmware before the OS starts.
6. **Thermocouple pilot supervision** (hardware, independent of the Pi) — no lit pilot → the 24 VDC valve passes no gas, regardless of everything above.
7. **Igniter dead-man (Eaton continuous-ignition module)** — separate relay pair (Relay A/BCM 5, Relay B/BCM 6) in series on the module's 12V enable input, deliberately independent of the gas relays. Relay A is app-owned, energized only while the browser is actively holding the on-screen button (pinged ~every 500 ms); Relay B is owned by the independent `firepit-igniter-watchdog` service. Either relay dropping cuts the igniter. No flame-proving — ignition is manually supervised by the operator holding the button, not by software logic. Unlike the gas watchdog, a stale igniter heartbeat never reboots the Pi — it only cuts power.

## Install (on the Pi)
```
sudo bash ~/firepit-deploy/install.sh
sudo reboot        # activates the config.txt items (boot GPIO + hardware watchdog)
```

## Verify
```
systemctl status firepit firepit-watchdog firepit-igniter-watchdog
journalctl -u firepit-watchdog -f          # watch gas arm/cut/reboot decisions
journalctl -u firepit-igniter-watchdog -f  # watch igniter arm/cut decisions
curl -s localhost:5000/api/gas/status
curl -s localhost:5000/api/igniter/status
```

## Maintenance note (IMPORTANT)
The watchdog reboots the Pi if the heartbeat goes stale. To stop the **app** for
maintenance without triggering a reboot, stop the **watchdog first**:
```
sudo systemctl stop firepit-watchdog
sudo systemctl stop firepit
# ... work ...
sudo systemctl start firepit
sudo systemctl start firepit-watchdog
```
A normal `sudo reboot` / shutdown stops both units together, so it does NOT mis-trigger.

## Watchdog tuning (env in firepit-watchdog.service)
- `FIREPIT_GAS_CUT_S` (1.0) — stale this long → CH3 LOW (gas off).
- `FIREPIT_REBOOT_S` (5.0) — stale this long while armed → reboot.
- `FIREPIT_ALLOW_REBOOT` (1) — set 0 to cut gas but never reboot (debugging).

## Igniter watchdog tuning (env in firepit-igniter-watchdog.service)
- `FIREPIT_IGNITER_CUT_S` (1.5) — stale this long → Relay B LOW (igniter off). No reboot tier.
