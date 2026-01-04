#!/bin/sh
# Patch OpenSpeedTest to send results to Network Optimizer API

# The save URL that browsers will POST results to
SAVE_DATA_URL="${OPENSPEEDTEST_SAVE_URL}"

if [ -z "$SAVE_DATA_URL" ]; then
    echo "Warning: OPENSPEEDTEST_SAVE_URL not set - results will not be saved"
    echo "Set OPENSPEEDTEST_SAVE_URL to your Network Optimizer API URL"
    echo "Example: OPENSPEEDTEST_SAVE_URL=http://192.168.1.10:8042/api/speedtest/result?"
fi

# Find and patch the main JavaScript file
JS_FILE=$(find /var/www/html -name "app-*.js" -o -name "app-*.min.js" 2>/dev/null | head -1)

if [ -n "$JS_FILE" ] && [ -n "$SAVE_DATA_URL" ]; then
    echo "Patching OpenSpeedTest to send results to: $SAVE_DATA_URL"

    # Enable saveData and set the URL
    sed -i 's/saveData\s*=\s*false/saveData = true/' "$JS_FILE"
    sed -i 's/saveData\s*=\s*!1/saveData = !0/' "$JS_FILE"  # Minified version

    # Set the save URL
    sed -i "s|saveDataURL\s*=\s*\"[^\"]*\"|saveDataURL = \"$SAVE_DATA_URL\"|" "$JS_FILE"

    echo "OpenSpeedTest patched successfully"
elif [ -z "$JS_FILE" ]; then
    echo "Warning: Could not find OpenSpeedTest JS file to patch"
fi

# Run the original entrypoint (OpenSpeedTest's nginx setup)
exec /entrypoint.sh
