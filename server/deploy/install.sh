#!/usr/bin/env bash
# FirePit installer — run ON THE PI as root:  sudo bash ~/firepit-deploy/install.sh
# Idempotent: safe to re-run for updates.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "Please run as root:  sudo bash $0" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_SRC="$SCRIPT_DIR/app"
WD_SRC="$SCRIPT_DIR/watchdog.py"
IGN_WD_SRC="$SCRIPT_DIR/igniter_watchdog.py"

# config.txt moved to /boot/firmware on recent Raspberry Pi OS.
CONFIG_TXT="/boot/firmware/config.txt"
[ -f "$CONFIG_TXT" ] || CONFIG_TXT="/boot/config.txt"

echo "== Stopping any running FirePit services =="
systemctl stop firepit.service firepit-watchdog.service firepit-valve.service firepit-igniter-watchdog.service 2>/dev/null || true
# Kill any stray manually-launched scaffold instance.
pkill -f 'dotnet .*FirepitController.dll' 2>/dev/null || true

echo "== Installing main app -> /opt/firepit =="
mkdir -p /opt/firepit
rsync -a --delete "$APP_SRC/" /opt/firepit/
chown -R pi:pi /opt/firepit

echo "== Installing watchdog -> /opt/firepit-watchdog =="
mkdir -p /opt/firepit-watchdog
install -m 0755 "$WD_SRC" /opt/firepit-watchdog/watchdog.py

echo "== Installing igniter watchdog -> /opt/firepit-igniter-watchdog =="
mkdir -p /opt/firepit-igniter-watchdog
install -m 0755 "$IGN_WD_SRC" /opt/firepit-igniter-watchdog/igniter_watchdog.py

echo "== Installing valve shim -> /opt/firepit-valve (venv + tinytuya) =="
mkdir -p /opt/firepit-valve
install -m 0755 "$SCRIPT_DIR/valve_shim.py" /opt/firepit-valve/valve_shim.py
if [ ! -x /opt/firepit-valve/venv/bin/python ]; then
  python3 -m venv /opt/firepit-valve/venv
  /opt/firepit-valve/venv/bin/pip install --quiet --upgrade pip >/dev/null 2>&1
fi
/opt/firepit-valve/venv/bin/pip install --quiet tinytuya
chown -R pi:pi /opt/firepit-valve

echo "== Installing valve credentials -> /etc/firepit/valve.env (root, 0600) =="
mkdir -p /etc/firepit
install -m 0600 -o root -g root "$SCRIPT_DIR/valve.env" /etc/firepit/valve.env

echo "== Installing systemd units =="
install -m 0644 "$SCRIPT_DIR/firepit.service"                  /etc/systemd/system/firepit.service
install -m 0644 "$SCRIPT_DIR/firepit-watchdog.service"         /etc/systemd/system/firepit-watchdog.service
install -m 0644 "$SCRIPT_DIR/firepit-valve.service"            /etc/systemd/system/firepit-valve.service
install -m 0644 "$SCRIPT_DIR/firepit-igniter-watchdog.service" /etc/systemd/system/firepit-igniter-watchdog.service
mkdir -p /etc/systemd/system.conf.d
install -m 0644 "$SCRIPT_DIR/99-firepit-watchdog.conf" /etc/systemd/system.conf.d/99-firepit-watchdog.conf

echo "== Boot hardening in $CONFIG_TXT =="
add_config_line() {
  local line="$1"
  if ! grep -qxF "$line" "$CONFIG_TXT"; then
    echo "$line" >> "$CONFIG_TXT"
    echo "  added: $line"
  else
    echo "  present: $line"
  fi
}
OLD_GPIO_LINE="gpio=20,21,26=op,dl"
if grep -qxF "$OLD_GPIO_LINE" "$CONFIG_TXT"; then
  sed -i "\\#^${OLD_GPIO_LINE}\$#d" "$CONFIG_TXT"
  echo "  superseded: $OLD_GPIO_LINE"
fi
add_config_line "# FirePit: drive gas + igniter relays LOW at boot (fail-safe, before OS starts)"
add_config_line "gpio=5,6,13,20,21,26=op,dl"
add_config_line "# FirePit: enable BCM hardware watchdog (/dev/watchdog)"
add_config_line "dtparam=watchdog=on"

echo "== Enabling services =="
systemctl daemon-reload
systemctl enable firepit.service firepit-watchdog.service firepit-valve.service firepit-igniter-watchdog.service
systemctl restart firepit-valve.service firepit.service firepit-watchdog.service firepit-igniter-watchdog.service

echo
echo "== DONE =="
echo "Services started. Verify:  systemctl status firepit firepit-watchdog"
echo "App URL:  http://$(hostname -I | awk '{print $1}'):5000"
echo
echo "IMPORTANT: reboot once to activate the early-boot GPIO safe state and the"
echo "hardware watchdog (they come from config.txt):   sudo reboot"
