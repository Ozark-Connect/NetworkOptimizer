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
# The boot-time mode set does not always commit: uniphy1 can stay at 125 MHz
# even though insmod succeeded, and early in boot a set that did commit can
# still be reverted up to ~35s later. Which boots are affected is timing-
# dependent, so a given firmware and SFP can boot cleanly and fail on the next
# try. The script retries up to five times, restoring stock SGMII 1G between
# attempts, and verifies that the 312.5 MHz clock holds for 45s before
# declaring success.
#
# The module bypasses the SSDK's SFP EEPROM validation by calling the uniphy
# mode set function directly. The SSDK's MAC sync polling loop re-reads the
# SFP EEPROM every ~12s and would revert the 2.5G change. The module (v3+)
# excludes eth6 from the polling loop's port bitmap and restarts it — the loop
# continues to run for all other ports, so eth5 link recovery is unaffected.
#
# WARNING: This targets eth6 / Port 7 (the 2nd SFP+ port) ONLY.
# For eth5 / Port 6, use 19-sfp-sgmiiplus-eth5.sh instead.
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
MAX_ATTEMPTS=5
HOLD_SECS=45

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

# ─── Load and verify module ───

BEFORE_CLOCK=""
if [ -f "${CLOCK_PATH}" ]; then
    BEFORE_CLOCK=$(cat "${CLOCK_PATH}")
    log "Clock rate before: ${BEFORE_CLOCK} Hz"
fi

attempt=1
verified=0

while [ $attempt -le $MAX_ATTEMPTS ]; do
    log "Loading ${MODULE_NAME} (attempt ${attempt}/${MAX_ATTEMPTS})..."

    insmod "${MODULE_FILE}" 2>> "${LOG_FILE}"
    RET=$?

    if [ ${RET} -ne 0 ]; then
        log "ERROR: insmod failed with exit code ${RET}"
        exit 1
    fi

    # Give the mode set sequence time to complete (~300ms PLL relock + calibration)
    sleep 1

    if [ ! -f "${CLOCK_PATH}" ]; then
        log "WARNING: ${CLOCK_PATH} not found, cannot verify clock rate"
        verified=1
        break
    fi

    AFTER_CLOCK=$(cat "${CLOCK_PATH}")
    log "Clock rate after attempt ${attempt}: ${AFTER_CLOCK} Hz"

    # Clock is the success criterion, not carrier, so no-link 2.5G SFPs work.
    if [ "${AFTER_CLOCK}" = "312500000" ]; then
        log "Clock reached 312500000 Hz; verifying it holds for ${HOLD_SECS}s"
        held=0
        while [ $held -lt $HOLD_SECS ]; do
            sleep 5
            held=$((held + 5))
            AFTER_CLOCK=$(cat "${CLOCK_PATH}" 2>/dev/null)
            if [ "${AFTER_CLOCK}" != "312500000" ]; then
                break
            fi
        done

        if [ "${AFTER_CLOCK}" = "312500000" ]; then
            log "Verified: uniphy1 running at 312.5 MHz (SGMII+ 2.5G) and held for ${HOLD_SECS}s"
            verified=1
            break
        fi

        log "WARNING: Attempt ${attempt} clock reverted to ${AFTER_CLOCK} Hz after ${held}s of holding"
    else
        log "WARNING: Attempt ${attempt} expected 312500000 Hz, got ${AFTER_CLOCK} Hz"
    fi

    log "Removing ${MODULE_NAME} to restore stock SGMII 1G before retry"
    rmmod "${MODULE_NAME}" 2>> "${LOG_FILE}"
    RET=$?

    if [ ${RET} -ne 0 ]; then
        log "ERROR: rmmod failed with exit code ${RET}"
        exit 1
    fi

    elapsed=0
    carrier=""
    while [ $elapsed -lt 30 ]; do
        carrier=$(cat /sys/class/net/${IFACE}/carrier 2>/dev/null)
        if [ "$carrier" = "1" ]; then
            log "${IFACE} stock 1G carrier recovered after ${elapsed}s"
            break
        fi
        sleep 2
        elapsed=$((elapsed + 2))
    done

    if [ "$carrier" != "1" ]; then
        log "WARNING: ${IFACE} stock 1G carrier did not recover after 30s; continuing anyway"
    fi

    if [ $attempt -lt $MAX_ATTEMPTS ]; then
        backoff=$((attempt * 5))
        log "Retrying after ${backoff}s backoff"
        sleep $backoff
    fi

    attempt=$((attempt + 1))
done

if [ $verified -ne 1 ]; then
    if lsmod | grep -q "${MODULE_NAME}"; then
        rmmod "${MODULE_NAME}" 2>> "${LOG_FILE}"
        RET=$?
        if [ ${RET} -ne 0 ]; then
            log "ERROR: final rmmod failed with exit code ${RET}"
            exit 1
        fi
    fi
    log "ERROR: 2.5G mode did not stick after ${MAX_ATTEMPTS} attempts; ${IFACE} was left in stock SGMII 1G state so the link still works at 1G"
    exit 1
fi

if lsmod | grep -q "${MODULE_NAME}"; then
    log "Module loaded successfully"
else
    log "ERROR: Module not present after insmod"
    exit 1
fi

log "Done"
