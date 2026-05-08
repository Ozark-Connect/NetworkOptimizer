#!/bin/sh
# 19-sfp-sgmiiplus-eth5.sh: Force 1st SFP+ port (eth5 / Port 6) to SGMII+ 2.5G
#
# Loads a kernel module that switches uniphy2 from SGMII 1G to SGMII+ 2.5G
# by calling the QCA-SSDK's internal uniphy mode set function directly,
# bypassing SFP EEPROM validation that blocks the speed change.
#
# WARNING: This targets eth5 / Port 6 (the 1st SFP+ port) ONLY.
# For eth6 / Port 7, use 20-sfp-sgmiiplus.sh instead.
#
# The SSDK's MAC sync polling loop re-reads the SFP EEPROM every ~12s and
# would revert the 2.5G change. The module excludes eth5 from the
# polling loop's port bitmap and restarts it — the loop continues to run
# for all other ports, so eth6 link recovery is unaffected.
#
# Target: UCG-Fiber / UXG-Fiber (IPQ9574, kernel 5.4.213-ui-ipq9574)
# Requires: qca-ssdk.ko loaded, module pre-deployed to /data/sfp-sgmiiplus/

SCRIPT_NAME="sfp-sgmiiplus-eth5"
LOG_FILE="/var/log/${SCRIPT_NAME}.log"
MODULE_DIR="/data/sfp-sgmiiplus"
MODULE_NAME="force_uniphy2_sgmiiplus"
MODULE_FILE="${MODULE_DIR}/${MODULE_NAME}.ko"
CLOCK_PATH="/sys/kernel/debug/clk/uniphy2_gcc_tx_clk/clk_rate"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') - $1" >> "${LOG_FILE}"
}

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
        log "Verified: uniphy2 running at 312.5 MHz (SGMII+ 2.5G)"
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
