#!/bin/sh
# 15-fan-control-tuning.sh: Tune the built-in PID fan controller setpoints
#
# UniFi Cloud Gateways ship with very conservative fan controller setpoints
# (e.g., CPU setpoint of 100C). The fan barely runs, and components sit at
# elevated temperatures unnecessarily.
#
# This script pushes lower setpoints via the Status Database (SDB) API so
# the PID controller keeps temperatures in a healthier range. It runs
# once at boot, applies the config, and exits - no background process, no
# continuous logging, zero eMMC wear.
#
# WHICH DAEMON OWNS THE FAN LOOP CHANGED IN UniFi OS 6.0:
#   5.1.x and earlier - uhwd runs the PID loop.
#   6.0.x and later   - fan control moved to a dedicated daemon, ufcd.
#                       uhwd no longer writes PWM at all.
# The SDB write alone never triggers a re-read, so the owning daemon must be
# restarted. Restarting the wrong one silently does nothing: the setpoints
# persist in SDB and read back correctly while the fan keeps running on the
# factory values it loaded at boot. This script detects the owner at runtime.
#
# IMPORTANT: The PID categories (cpu, hdd, rtl8372, rtl8261) vary by model.
# Run the monitoring command below to check YOUR gateway's config.fan before
# applying. If your model has different category names, adjust the script.
#
# Compatible with any UCG model that uses uhwd/ufcd + SDB for fan control.

SCRIPT_NAME="fan-control-tuning"
LOG_FILE="/var/log/${SCRIPT_NAME}.log"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') - $1" >> "${LOG_FILE}"
}

# ─── Tuned setpoints (index 0 in each PID array) ───
# Check your gateway's defaults first:
#   python3 -c "
#   import json, threading, time
#   from ustd.statusdb.sdb_client import SDBClient
#   c = SDBClient()
#   t = threading.Thread(target=c.run, daemon=True); t.start(); time.sleep(1)
#   print(json.dumps(c.get('config.fan'), indent=2))
#   "
#
# Typical defaults → Tuned:
#   cpu:     100 → 65   (fan engages much earlier)
#   hdd:      68 → 55   (protect NVMe/eMMC)
#   rtl8372: 109 → 85   (10G switch chip)
#   rtl8261: 103 → 90   (SFP+ PHY)
#   standby:  20        (stock; do not change — role not fully understood via RE)

CPU_SETPOINT=65
HDD_SETPOINT=55
RTL8372_SETPOINT=85
RTL8261_SETPOINT=90
STANDBY=20

# ─── Detect which daemon owns the fan PID loop ───
# ufcd exists only on UniFi OS 6.0 and later. Where it exists it owns the
# loop and uhwd is irrelevant to the fan; elsewhere uhwd still owns it.
if systemctl cat ufcd.service >/dev/null 2>&1; then
    FAN_SERVICE="ufcd.service"
else
    FAN_SERVICE="uhwd.service"
fi
log "Fan PID loop owned by ${FAN_SERVICE}"

# ─── Wait for the fan daemon ───
log "Waiting for ${FAN_SERVICE}..."
WAIT=0
MAX_WAIT=120
while [ $WAIT -lt $MAX_WAIT ]; do
    if systemctl is-active --quiet "${FAN_SERVICE}"; then
        break
    fi
    sleep 5
    WAIT=$((WAIT + 5))
done

if ! systemctl is-active --quiet "${FAN_SERVICE}"; then
    log "ERROR: ${FAN_SERVICE} not active after ${MAX_WAIT}s, aborting"
    exit 1
fi
log "${FAN_SERVICE} is active (waited ${WAIT}s)"

# Give the daemon a moment to initialize its default config
sleep 5

# ─── Apply tuned fan config via SDB ───
python3 >> "${LOG_FILE}" 2>&1 << PYEOF
import json, sys, threading, time

from ustd.statusdb.sdb_client import SDBClient

c = SDBClient()
t = threading.Thread(target=c.run, daemon=True)
t.start()
time.sleep(1)

fan = c.get("config.fan")
if fan is None:
    print("ERROR: config.fan is None")
    sys.exit(1)

# Log original setpoints
orig = {k: v[0] for k, v in fan.get("PID", {}).items()}
print(f"BEFORE: setpoints={json.dumps(orig)} standby={fan.get('standby')}")

# Apply tuned setpoints - only modify categories that exist on this model
pid = fan.get("PID", {})
if "cpu" in pid:
    pid["cpu"][0] = ${CPU_SETPOINT}
if "hdd" in pid:
    pid["hdd"][0] = ${HDD_SETPOINT}
if "rtl8372" in pid:
    pid["rtl8372"][0] = ${RTL8372_SETPOINT}
if "rtl8261" in pid:
    pid["rtl8261"][0] = ${RTL8261_SETPOINT}
fan["standby"] = ${STANDBY}

c.update("config.fan", fan)
time.sleep(1)

# Verify
fan2 = c.get("config.fan")
tuned = {k: v[0] for k, v in fan2.get("PID", {}).items()}
print(f"AFTER:  setpoints={json.dumps(tuned)} standby={fan2.get('standby')}")
PYEOF

if [ $? -eq 0 ]; then
    log "Fan config updated in SDB"
else
    log "ERROR: Failed to update fan config"
    exit 1
fi

# Restart the fan daemon so it picks up the new PID setpoints.
# The SDB update alone doesn't trigger the running PID loop to re-read config.
log "Restarting ${FAN_SERVICE} to apply new config..."
systemctl restart "${FAN_SERVICE}"
sleep 5

if systemctl is-active --quiet "${FAN_SERVICE}"; then
    log "${FAN_SERVICE} restarted successfully"
else
    log "ERROR: ${FAN_SERVICE} failed to restart"
    exit 1
fi

# Record live fan state for diagnostics. The fan correctly sits at its floor
# whenever every component is below its setpoint, so a low PWM here is not a
# failure - it is only a snapshot to correlate against temperatures later.
HWMON=$(ls -d /sys/class/hwmon/hwmon* 2>/dev/null | head -1)
if [ -n "${HWMON}" ] && [ -r "${HWMON}/pwm1" ]; then
    log "Fan state: pwm=$(cat "${HWMON}/pwm1" 2>/dev/null) rpm=$(cat "${HWMON}/fan1_input" 2>/dev/null)"
fi

log "Done"
