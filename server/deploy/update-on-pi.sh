#!/usr/bin/env bash
# Applies a freshly-copied app build (in ~/firepit-update) to /opt/firepit.
# Run as root on the Pi:  sudo bash ~/update-on-pi.sh
# Stops the watchdog first so the app restart can't trip a reboot.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "run as root: sudo bash $0" >&2; exit 1; }
SRC="/home/pi/firepit-update"
[ -f "$SRC/FirepitController.dll" ] || { echo "no build found in $SRC" >&2; exit 1; }

echo "== stopping watchdog (so restart can't trigger a reboot) =="
systemctl stop firepit-watchdog

echo "== syncing new build -> /opt/firepit =="
rsync -a --delete "$SRC/" /opt/firepit/
chown -R pi:pi /opt/firepit

echo "== restarting app =="
systemctl restart firepit
sleep 4

if curl -sf -m 5 http://127.0.0.1/api/gas/status >/dev/null; then
  echo "== app responding on :80 OK =="
else
  echo "!! app not responding — check: journalctl -u firepit -n 40" >&2
fi

echo "== re-arming watchdog =="
systemctl start firepit-watchdog

for s in firepit-valve firepit firepit-watchdog; do printf "  %-18s " "$s:"; systemctl is-active "$s"; done
echo "== update complete =="
