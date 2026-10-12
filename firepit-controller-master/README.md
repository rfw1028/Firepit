# FirePit Controller

> **Safety and security notice.** This project controls a gas valve and an igniter. It is
> provided as-is, with no warranty, and is **not** a certified or safety-rated appliance
> controller. Install and test gas equipment in accordance with local codes and with a
> qualified professional. The web UI and API have **no authentication**: run them only on a
> trusted private network and never expose them to the internet.

A Raspberry Pi–based controller for a natural gas fire pit: an ASP.NET Core app
(`server/src`) that owns two series gas-shutoff relays and a WiFi ball valve, plus a
manually-supervised igniter relay for an Eaton continuous-ignition module — all backed
by independent, dependency-free Python watchdog processes that provide fail-safe
cutoffs even if the main app hangs or crashes.

## Repo layout

- `server/src/` — the ASP.NET Core control app (Razor Pages UI + minimal API)
- `server/watchdog/` — independent Python dead-man watchdogs (`watchdog.py` for gas
  CH3, `igniter_watchdog.py` for the igniter's Relay B)
- `server/valve/` — Python shim bridging the WiFi ball valve (Tuya protocol 3.5)
- `server/deploy/` — systemd units, `install.sh`, and the deploy `README.md` with the
  fail-safe layer enumeration and operational runbook
- `server/GpioProbe/` — a small standalone diagnostic tool for probing GPIO state
- `server/update.ps1` — build + deploy script (`dotnet publish` → scp → remote install)
- `design-notes.md`, `firepit-opto-isolator-handoff.md` — working notes from prior
  deployment/hardware sessions

Secrets (`valve.env`, `devices.json`, `tinytuya.json`, `tuya-raw.json`, etc.) are
excluded via `.gitignore` and never committed — see `server/deploy/valve.env` on the
Pi itself (`/etc/firepit/valve.env`, root-only) for the real credentials.

---

# Design: manual dead-man igniter relay for the Eaton continuous-ignition module

The section below is the full design writeup produced when adding igniter control to
the existing gas fire pit system, preserved as project documentation.

## Context

A second relay HAT (identical model to the one already on the Firepit Controller Pi)
was added to switch the 12VDC/2A control-enable input of an Eaton oil burner
continuous-ignition module. There's no flame-proving logic — ignition is manually
supervised: the operator holds a button on the web UI, the browser pings the backend
every 500ms while held, and power to the igniter must drop within ~1.5s of the pings
stopping (tab closed, network dropped, browser crashed — not just an explicit release).

The production app (`server/src`) already solved exactly this class of problem for the
gas relays: CH1/CH2 are owned by the main ASP.NET Core app, CH3 is owned by a fully
independent, root-owned, dependency-free Python watchdog (`watchdog.py`) that polls a
heartbeat file and force-drops its relay if the app hangs — because GPIO pins retain
their last driven state through a process crash (confirmed in `GpioRelayArray.cs`'s doc
comment and the deploy README's verified test: "an app killed with `-9` leaves CH1/CH2
stuck HIGH, but the watchdog still cuts gas via CH3"). The igniter needed the same
defense-in-depth, so this design reuses that pattern rather than inventing a new one.

Confirmed during investigation:
- The new HAT is the same model as the existing one: **Electronics-Salon D-1226**,
  3-channel, each relay's control line selected by a removable jumper on a jack —
  factory default bridges relay 1→GPIO26, relay 2→GPIO20, relay 3→GPIO21 (matching
  the existing board exactly). To use a different pin, pull that relay's jumper and
  wire a jumper wire from the desired pin on the header pass-through side to the
  jack's relay-side pin.
- It **can** be physically stacked on the 40-pin header (both boards' pass-through
  pins tie together, which is fine) as long as none of its three jumpers are left on
  their factory default — those would tie straight into the existing board's CH1/CH2/CH3
  signals once stacked. All three get rewired, including the unused third channel
  (left floating would let its relay driver's input pick up noise — wire it to a real
  pin and boot-default it LOW instead).
- Live free GPIO on the header (checked via `pinctrl`): everything except BCM 20/21/26
  (gas) is unclaimed — no I2C/SPI/UART overlays are enabled.
- `firepit-opto-isolator-handoff.md` describes a separate, unrelated sensing project
  that would have landed on GPIO 16/26 — confirmed **not** wired to this production Pi,
  so it doesn't constrain pin choice here.
- The 12VDC/2A rating is the module's control/enable input, not the raw igniter coil —
  no flyback diode needed.
- Chosen pins, all currently unclaimed with no default alt-function: **BCM 5** (Relay A,
  main-app-owned, header pin 29), **BCM 6** (Relay B, independent igniter-watchdog-owned,
  pin 31), **BCM 13** (spare third relay, unused but wired + boot-safe LOW, pin 33).

## Approach

Mirror the CH1/CH2 + CH3 series pattern exactly, scaled down (no reboot escalation —
losing the igniter isn't a system-health emergency the way losing gas-relay control is,
so the igniter watchdog only cuts power, it never reboots the Pi):

- **Relay A + Relay B in series** on the Eaton module's 12V control-input line. Both
  must be HIGH for the module to be enabled; either dropping cuts it.
- **Relay A** — driven by the main app, energized only while it's received a hold ping
  within `HoldTimeoutMs` (1500ms). Enforced two ways in-app: immediately on each
  `/api/igniter/hold` call, and by a background poller (mirrors `HeartbeatService`'s
  `PeriodicTimer` idiom) that force-drops it if pings stop arriving.
- **Relay B** — driven by a new, separate, root-owned daemon `igniter_watchdog.py`
  (near-identical structure to `watchdog.py`, stripped of the reboot tier), watching a
  dedicated heartbeat file `/run/firepit/igniter-heartbeat` that the main app refreshes
  on every hold ping. Independent of the main app process, so an app hang still cuts
  the igniter via Relay B — same guarantee CH3 provides for gas.

### Physical wiring (new D-1226 board)

Stack the board on the 40-pin header, then pull all three factory jumpers and rewire:

| Relay | Factory jumper (pull it) | Wire jumper wire to | Header pin | Role |
|---|---|---|---|---|
| 1 | GPIO26 | BCM 5 | 29 | Relay A — app-controlled |
| 2 | GPIO20 | BCM 6 | 31 | Relay B — igniter-watchdog-controlled |
| 3 | GPIO21 | BCM 13 | 33 | spare, unused, boot-safe LOW |

Relay A and Relay B in series on the Eaton module's 12V control-input line.

### Backend (`server/src`)

- **`Hardware/IgniterOptions.cs`** — `BcmPins` (default `[5]`), `ForceSimulation`.
  Same shape as `RelayOptions.cs`.
- **`Hardware/IgniterHeartbeatOptions.cs`** — `Path` (`/run/firepit/igniter-heartbeat`),
  `HoldTimeoutMs` (1500). Same shape as `HeartbeatOptions.cs`.
- **`Hardware/IgniterController.cs`** — mirrors `GasValveController.cs`. Owns a
  keyed `IRelayArray` (Relay A only — Relay B belongs to the external watchdog, same
  as CH3 is excluded from `Relays.BcmPins`). Methods: `HoldAsync()` (drives Relay A HIGH,
  stamps `LastHoldUtc`, touches the heartbeat file), `Release()`/`ForceSafe()` (drives
  Relay A LOW). `GetStatus()` returns `{ active, channelStates, simulated, holdAgeMs }`.
  Reuses the existing `IRelayArray`/`GpioRelayArray`/`SimulatedRelayArray` classes
  unmodified — they already take an arbitrary pin array.
- **`Hardware/IgniterHoldTimeoutService.cs`** — `BackgroundService`, same
  `PeriodicTimer` idiom as `HeartbeatService.cs`, polls every 200ms, releases Relay A
  if `LastHoldUtc` is older than `HoldTimeoutMs`.
- **`Program.cs`** — registers `IgniterOptions`/`IgniterHeartbeatOptions`, a keyed
  singleton `IRelayArray` (key `"igniter"`, same factory-lambda pattern as the existing
  gas `IRelayArray` registration, just reading `IgniterOptions.BcmPins`), `IgniterController`,
  and `IgniterHoldTimeoutService`. Calls `igniter.ForceSafe()` alongside the existing
  `gas.ForceSafe()` at startup and in the `ApplicationStopping` handler. Minimal-API
  endpoints: `GET /api/igniter/status`, `POST /api/igniter/hold`, `POST /api/igniter/release`.
- **`appsettings.json`** — `Igniter` (`BcmPins: [5]`) and `IgniterHeartbeat`
  (`Path`, `HoldTimeoutMs: 1500`) sections, following the existing `Relays`/`Heartbeat`
  comment style.

### Independent watchdog

- **`server/watchdog/igniter_watchdog.py`** — copy of `watchdog.py` trimmed to just the
  arm/cut logic (no `REBOOT_S`/`reboot()` — a stale igniter heartbeat only has to cut
  Relay B, never reboot). Env vars: `FIREPIT_IGNITER_HEARTBEAT`
  (`/run/firepit/igniter-heartbeat`), `FIREPIT_IGNITER_WATCHDOG_PIN` (`6`),
  `FIREPIT_IGNITER_CUT_S` (`1.5`), `POLL_S` (`0.2`).
- **`server/deploy/firepit-igniter-watchdog.service`** — same shape as
  `firepit-watchdog.service` (root, `Restart=always`), but `After=multi-user.target`
  only (no reboot capability, so no ordering concerns with the main app).

### Deploy scripts (`server/deploy`)

- **`install.sh`** — installs `igniter_watchdog.py` to `/opt/firepit-igniter-watchdog/`,
  installs the new unit, appends `5,6,13` to the boot-safe `gpio=` line
  (`gpio=5,6,13,20,21,26=op,dl`, superseding the old `gpio=20,21,26=op,dl` line), enables
  + starts the new service alongside the existing three.
- **`README.md`** — includes a fail-safe-layers entry for the igniter dead-man,
  matching the existing enumeration style, plus igniter watchdog tuning env vars.
- `update-on-pi.sh` needs no change — it only touches the main app + `firepit-watchdog`
  (which reboots), and the igniter watchdog never reboots, so there's no restart-ordering
  hazard to guard against there.

### Frontend (`Pages/Index.cshtml`)

- `fp-card` section "Igniter" with a single hold button, styled like the existing
  Gas/Valve cards (reuses `fp-badge`, `fp-btn`, `fp-note` classes from `site.css`).
- JS: on `pointerdown` starts a 500ms `setInterval` POSTing `/api/igniter/hold`; on
  `pointerup`/`pointercancel`/`pointerleave`, window `blur`, and `visibilitychange`
  (tab hidden), clears the interval and POSTs `/api/igniter/release` immediately for a
  snappy UI turn-off — the real safety cutoff is the staleness timeout server-side, this
  is just UX. Periodic `/api/igniter/status` refresh (every 3000ms) mirrors `refreshGas`.

## Verification

1. `dotnet build` in `server/src` — confirms it compiles (runs fine on a dev machine via
   `SimulatedRelayArray`, no GPIO needed).
2. Run locally, exercise `/api/igniter/hold` / `/status` / `/release` with curl to
   confirm the state machine and timeout behave before touching the Pi.
3. Deploy via `.\update.ps1` for the app half, then `sudo bash install.sh` on the Pi
   once (picks up the new watchdog service + config.txt line) — **reboot required**
   to activate the boot-safe GPIO defaults for pins 5/6/13.
4. On the Pi: `pinctrl get 5,6,13,20,21,26` after reboot — expect all low.
5. Hold the button, confirm Relay A goes HIGH (`pinctrl get 5`) and stays high only
   while pinging; release and confirm it drops within ~1.5s.
6. Repeat the existing kill-test methodology from the deploy README: kill the exact
   `firepit` `MainPID` (not a `pkill -f` pattern match — that can accidentally match the
   invoking shell's own command line and kill the SSH session instead) while holding.
   Confirmed on real hardware: Relay A stayed stuck HIGH (expected — GPIO retains
   state), while Relay B (`pinctrl get 6`) independently dropped LOW within
   `FIREPIT_IGNITER_CUT_S` once the heartbeat went stale, breaking the series circuit
   and cutting the igniter regardless of the dead app's stuck relay. The app then
   recovered via `systemd Restart=always` and cleared Relay A on its next startup
   `ForceSafe()` call.
7. Only after 1–6 pass, and with the Eaton module physically connected, do a real
   hold/release cycle and confirm ignition control matches expectations.

**Status: deployed and verified on hardware.** Steps 1–6 above have been executed
against the real Pi and real relay hardware, including the `kill -9`
independent-watchdog test. Step 7 (physical Eaton module connection and live ignition
test) is pending manual electrical verification at the module's terminals.
