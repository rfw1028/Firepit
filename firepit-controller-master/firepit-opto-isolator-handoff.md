# Fire pit controller — 24 V opto isolator sense inputs

Handoff brief for Claude Code. Target: Raspberry Pi 4B fire pit controller, accessed over SSH.

\---

## 1\. What this task is

Two 24 VDC sense taps have been wired into a 4-channel optocoupler isolator board so the Pi can
verify the state of a series relay permissive chain. The wiring is complete. What remains is
**bench verification of the isolator outputs and the GPIO read path** — no gas, no flame, no
production logic yet.

\---

## 2\. Safety constraints — read before running anything

This controller operates a natural gas fire pit. Observe these without exception:

* **Do not command any relay** unless the operator has explicitly confirmed in this session that
the gas supply is isolated at the manual shutoff. The test script defaults to read-only and
requires an explicit flag to actuate.
* **Do not modify, restart, or disable** the production ASP.NET Core service or its systemd unit
as a side effect of testing. If a port or GPIO conflict appears, report it rather than
resolving it unilaterally.
* **Do not write flame-safety logic.** Flame proving and the shutoff interlock are hardware, in
series with valve power. Software here is monitoring and diagnostics only.
* If a test result is ambiguous (a reading between valid logic levels, an intermittent edge),
stop and report. Do not tune thresholds or add filtering to make a marginal signal pass.

\---

## 3\. Hardware inventory

|Item|Notes|
|-|-|
|Raspberry Pi 4B|Runs ASP.NET Core on Kestrel under systemd; VNC/remote desktop configured|
|Treedix UPS|Stacked. May claim I2C and possibly a power-fail GPIO — **verify**|
|Electronics-Salon D-1226 relay HAT|Three relays. Control pins are jumper-selectable; Electronics-Salon defaults are commonly GPIO 4 / 17 / 27 (pins 7 / 11 / 13) — **verify against the board's jumpers, do not assume**|
|NOYITO-family 4-channel opto isolator|"PNP NPN to PNP, 24V to 3.3V". Amazon ASIN B07TCXX2QL|

### Isolator board terminal convention

Input side is per-channel isolated differential pairs. Output side is a single supply plus four
outputs.

```
Input side (24 V field):   1+  1-   2+  2-   3+  3-   4+  4-
Output side (3.3 V logic): VCC  GND  O1  O2  O3  O4
```

**Critical:** `VCC` on this board is the *output-side* supply and must be 3.3 V. On this board
family the DC supply positive equals the signal output voltage. 24 V never touches `VCC`.

Output is PNP (active-high, non-inverting): 24 V on `n+` relative to `n-` drives `On` high.
Conversion frequency limit is 20–80 kHz depending on the variant — irrelevant at these speeds.

\---

## 4\. Wiring as built

The relay chain is **three relays in series** (relay 4 does not exist — an earlier draft of this
plan assumed four). The channel 4 tap is branched off relay 3's output.

```
+24 V ──●── \[Relay 1] ── \[Relay 2] ── \[Relay 3] ──●── Load (valve coil)
        │                                          │
        └── 1+                                     └── 4+
```

|Board terminal|Connected to|
|-|-|
|`1+`|Relay 1 COM — incoming 24 V node|
|`1-`|24 V supply 0 V|
|`4+`|Relay 3 output, load side (branched)|
|`4-`|24 V supply 0 V|
|`VCC`|Pi 3.3 V — header pin 1 or 17|
|`GND`|Pi GND — header pin 34 or 39|
|`O1`|GPIO 16 — header pin 36|
|`O4`|GPIO 26 — header pin 37|

Channels 2 and 3 are unused.

**The 24 V 0 V rail is not bonded to Pi GND anywhere.** Preserving that isolation is the point of
the board. Do not suggest or introduce a common ground.

### Why GPIO 16 and 26

* On the BCM2711, GPIO 0–8 power up with internal pull-ups; GPIO 9–27 power up with pull-downs.
The isolator's PNP output is a weak high-side source, so a pull-up default would let the pin
float high between power-on and service start — the Pi would read "24 V present" before it knew
anything. GPIO 16 and 26 default low, which is the safe state.
* Neither has a default alt function under any standard overlay.
* GPIO 2/3 were rejected: hard 1.8 kΩ I2C pull-ups fight the board's output pull-down and can read
a false high. GPIO 14/15 rejected: UART0, needed for MODBUS RTU to the DPS3005. GPIO 0/1 are the
HAT EEPROM ID pins. GPIO 4 gets claimed by the 1-Wire overlay if enabled.
* Both are at the far end of the header on the opposite row from the relay HAT's likely control
pins, so no jumper collision.

**Never configure these pins with pull-up.** An internal pull-up fighting the board's output
pull-down leaves roughly 0.5 V — technically a valid low, but with no margin. Use pull-down or no
bias.

\---

## 5\. Current verified state

* 24 VDC confirmed present at relay 1 input.
* Channel 1 status LED on the isolator board is **lit**.

That LED is on the field side, driven by the same current as the optocoupler emitter. It confirms
`1+` / `1-` polarity is correct and the tap is live. It says nothing about the output transistor,
since the output side is independently powered.

**Nothing on the output side has been verified yet.** That is the first job.

\---

## 6\. Test plan

### Test 1 — Isolator output levels (do this before landing wires on GPIO)

Power `VCC` / `GND` from 3.3 V. Meter `O1` referenced to output-side `GND`.

|24 V at relay 1 input|Expected at `O1`|
|-|-|
|Present (LED lit)|\~3.0–3.3 V|
|Removed (LED dark)|< 0.3 V|

* Inverted result → this is the NPN-output variant despite the listing. Flip the logic in
software, do not rewire.
* \~1.5 V or floating → wrong `VCC` or bad output-side ground. Stop and diagnose.

This is a multimeter step for the operator. Claude Code's role is to confirm it was done and
record the result before proceeding.

### Test 2 — GPIO bias and idle state

Confirm both pins come up with pull-down and read low with 24 V removed:

```
pinctrl get 16      # Bookworm and later
pinctrl get 26
raspi-gpio get 16   # older images
```

Expect `ip pd` and level `0`. Anything reading `ip pu` means something else configured the pin —
find what before continuing.

### Test 3 — Static read, both states

With the script in monitor mode, have the operator apply and remove 24 V at each tap. Confirm the
GPIO state tracks. Log the transition latency.

### Test 4 — Channel 4 dark-state leakage

With relay 3 open, the channel 4 LED must be **completely dark**. A faint glow means leakage
reaching the tap — a contact snubber, a MOV, or capacitive coupling on a long run. The opto input's
\~5 mA draw usually bleeds that away, but if it partially lights the LED it may also push `O4` into
an indeterminate region and produce intermittent false "chain closed" reads. Report any glow.

### Test 5 — Commanded vs sensed (requires gas isolated)

Only with explicit operator confirmation that gas is shut off at the manual valve. Command the
chain open and closed; compare against the sense bits. Allow \~50 ms settling before evaluating —
the optocoupler settles in well under a millisecond but relay pull-in and dropout are 5–15 ms.

\---

## 7\. Diagnostic truth table

With taps at both ends of a three-relay chain:

|Ch1|Ch4|Commanded|Interpretation|
|-|-|-|-|
|Low|—|any|24 V feed absent. Supply fault. Everything downstream is meaningless.|
|High|Low|Closed|One of relays 1–3 did not pull in, or a contact is open.|
|High|High|Closed|Chain healthy.|
|High|Low|Open|Normal.|
|High|High|Open|**Welded contact or bypassed path. Latching lockout, no auto-retry.**|

Endpoint-only sensing cannot isolate *which* relay failed. That is accepted — the question that
matters for a gas permissive is whether the chain as a whole did what it was told.

\---

## 8\. Outstanding hardware items

These are not blockers for testing but must be resolved before the system sees gas:

* **Flyback diode across the load coil**, at the coil. 1N4007, cathode to the +24 V side. Relay 3
is the switching point; that node flies negative on dropout.
* **Anti-parallel diode across `4+` / `4-`**, 1N4148, anode on `4-`, cathode on `4+`. Clamps
reverse excursion at the board terminals to \~0.7 V regardless of what gets past the coil diode.
Photodiode LEDs in this family break down around 5–6 V reverse; a valve coil will exceed that.
* Confirm the Treedix UPS's GPIO claims beyond I2C.
* Confirm the D-1226 relay control pin assignment from its jumpers.

Useful side effect worth noting: the opto input draws roughly 5 mA at 24 V, which provides wetting
current for the relay 3 contact. Helpful if the load itself is light.

\---

## 9\. Environment and conventions

* Production stack on the Pi is ASP.NET Core under Kestrel, managed by systemd.
* Operator's C# conventions: raw ADO.NET (no Entity Framework), one class per file, lowercase
namespaces.
* The Python test harness in `test\_opto\_sense.py` is **bench tooling only**. Production sense
reads belong in the C# service. Do not let the Python script become the permanent read path.
* EMI context: a ZVS flyback ignition module is in this system. Optical isolation blocks conducted
paths but not common-mode coupling into the field wiring. If intermittent false edges appear,
suspect coupling before suspecting the board.

\---

## 10\. Deliverables expected back

1. Recorded pass/fail for tests 1–4 with actual measured values, not just pass/fail.
2. `pinctrl get` output for both pins, captured verbatim.
3. Confirmed D-1226 relay control GPIO assignment.
4. Confirmed Treedix UPS GPIO claims.
5. Any observed leakage, glow, or intermittent edge, with conditions.

