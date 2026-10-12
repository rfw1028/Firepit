# FirePit camera + flame detector (separate from the gas controller)

| Service | File | Port / output |
|---|---|---|
| `firepit-camera` | `camera_stream.py` | MJPEG viewer `http://<pi-address>:8081/` (`/stream`, `/snapshot.jpg`) |
| `firepit-flame` | `flame_detector.py` | flag file `/run/firepit-flame/flame.json`, `http://127.0.0.1:5007/flame`, `/debug.png` (localhost only) |

`firepit-flame` reads the camera stream (so the camera runs whenever it runs). Both on the Pi under
`/opt/firepit-camera/`, units in `/etc/systemd/system/`. Self-test (synthetic scenes): `python3 flame_selftest.py`.

## Flag file

```json
{"flame": false, "confidence": 0.0, "camera_ok": true, "hits_2s": 0, "blocks": 0,
 "color_frac": 0.0, "motion_color_px": 0, "ts": 1791485925.228}
```

Rewritten atomically every 0.5 s. **Consumer rule (fail-safe)** — treat as *flame present* only if
`flame == true && camera_ok == true && (now - ts) < 5 s`. Missing/stale file, dead camera, parse error = no flame.

## Detection (advisory only — NOT a certified flame sensor; the thermocouple pilot remains the real interlock)

1. Colour fusion: RGB, HSV and YCbCr flame-colour rules; pixel counts if >= 2 of 3 agree.
2. Motion: colour pixels must also change frame-to-frame or flicker on/off over ~0.6 s.
3. Spatial: >= 2 blocks (16x16 @ 320x176) with >= 8 % moving flame colour; rejects > 50 % colour (washed out).
4. Temporal hysteresis: ON = >= 6 hits in 2 s; OFF = <= 2 hits in 3 s (slow to say yes, fast to say no).

Tunables are constants at the top of `flame_detector.py`. Region of interest: `FIREPIT_FLAME_ROI` in
`firepit-flame.service` (fractions `x0,y0,x1,y1`) — update it if the camera is moved.
Look at what the detector sees: `curl localhost:5007/debug.png` on the Pi (left = frame, right = amber flame-colour,
red = flame-colour AND moving, cyan = ROI outline).
