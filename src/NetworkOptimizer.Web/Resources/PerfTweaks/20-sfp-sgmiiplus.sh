#!/bin/sh
# 20-sfp-sgmiiplus.sh: Force 2nd SFP+ port (eth6 / Port 7) to SGMII+ 2.5G
#
# Waits for the SFP to establish a 1G link, then loads a kernel module that
# switches uniphy1 from SGMII 1G to SGMII+ 2.5G. The wait avoids a boot-order
# race with SFPs that need time to configure their SerDes (e.g., Zyxel PMG3000
# takes ~15s after boot to fire its 2.5G override). If no 1G link appears
# within the timeout, the module loads anyway — this handles SFPs that are
# hard-locked at 2.5G and can't establish a 1G link without the host matching.
#
# The module bypasses the SSDK's SFP EEPROM validation by calling the uniphy
# mode set function directly. The SSDK's MAC sync polling loop re-reads the
# SFP EEPROM every ~12s and would revert the 2.5G change. The module (v3+)
# excludes eth6 from the polling loop's port bitmap and restarts it — the loop
# continues to run for all other ports, so eth5 link recovery is unaffected.
#
# WARNING: This targets eth6 / Port 7 (the 2nd SFP+ port) ONLY.
#
# Target: UCG-Fiber / UXG-Fiber (IPQ9574, kernel 5.4.213-ui-ipq9574)
# Requires: qca-ssdk.ko loaded, module pre-deployed to /data/sfp-sgmiiplus/

SCRIPT_NAME="sfp-sgmiiplus"
LOG_FILE="/var/log/${SCRIPT_NAME}.log"
MODULE_DIR="/data/sfp-sgmiiplus"
MODULE_NAME="force_uniphy1_sgmiiplus"
MODULE_FILE="${MODULE_DIR}/${MODULE_NAME}.ko"
CLOCK_PATH="/sys/kernel/debug/clk/uniphy1_gcc_tx_clk/clk_rate"
IFACE="eth6"
CARRIER_TIMEOUT=90

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') - $1" >> "${LOG_FILE}"
}

# Re-exec in background so on_boot.d doesn't block waiting for carrier
if [ "$1" != "--bg" ]; then
    nohup "$0" --bg >/dev/null 2>&1 &
    exit 0
fi

# ─── Sanity checks ───

if [ ! -f "${MODULE_FILE}" ]; then
    log "ERROR: ${MODULE_FILE} not found. Deploy the module first — see docs/sfp-sgmiiplus.md"
    exit 1
fi

if lsmod | grep -q "${MODULE_NAME}"; then
    log "Module ${MODULE_NAME} already loaded. Nothing to do."
    exit 0
fi

if ! lsmod | grep -q "qca_ssdk"; then
    log "ERROR: qca-ssdk.ko not loaded. Cannot proceed."
    exit 1
fi

# ─── Wait for 1G carrier or timeout ───

elapsed=0
while [ $elapsed -lt $CARRIER_TIMEOUT ]; do
    carrier=$(cat /sys/class/net/${IFACE}/carrier 2>/dev/null)
    if [ "$carrier" = "1" ]; then
        log "${IFACE} has carrier after ${elapsed}s — loading module"
        break
    fi
    sleep 2
    elapsed=$((elapsed + 2))
done

if [ "$carrier" != "1" ]; then
    log "${IFACE} no carrier after ${CARRIER_TIMEOUT}s — loading module anyway (SFP may be hard-locked at 2.5G)"
fi

# ─── Load module ───

log "Loading ${MODULE_NAME}..."

BEFORE_CLOCK=""
if [ -f "${CLOCK_PATH}" ]; then
    BEFORE_CLOCK=$(cat "${CLOCK_PATH}")
    log "Clock rate before: ${BEFORE_CLOCK} Hz"
fi

insmod "${MODULE_FILE}" 2>> "${LOG_FILE}"
RET=$?

if [ ${RET} -ne 0 ]; then
    log "ERROR: insmod failed with exit code ${RET}"
    exit 1
fi

# Give the mode set sequence time to complete (~300ms PLL relock + calibration)
sleep 1

# ─── Verify ───

if [ -f "${CLOCK_PATH}" ]; then
    AFTER_CLOCK=$(cat "${CLOCK_PATH}")
    log "Clock rate after: ${AFTER_CLOCK} Hz"
    if [ "${AFTER_CLOCK}" = "312500000" ]; then
        log "Verified: uniphy1 running at 312.5 MHz (SGMII+ 2.5G)"
    else
        log "WARNING: Expected 312500000 Hz, got ${AFTER_CLOCK} Hz"
    fi
else
    log "WARNING: ${CLOCK_PATH} not found, cannot verify clock rate"
fi

if lsmod | grep -q "${MODULE_NAME}"; then
    log "Module loaded successfully"
else
    log "ERROR: Module not present after insmod"
    exit 1
fi

log "Done"
