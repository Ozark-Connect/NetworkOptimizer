#!/bin/sh
# Patch OpenSpeedTest to send results to Network Optimizer API

# Construct the save URL if not explicitly set
# Priority: OPENSPEEDTEST_SAVE_URL > REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
if [ -n "$OPENSPEEDTEST_SAVE_URL" ]; then
    SAVE_DATA_URL="$OPENSPEEDTEST_SAVE_URL"
elif [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - use https and no port (proxy handles it)
    SAVE_DATA_URL="https://${REVERSE_PROXIED_HOST_NAME}/api/speedtest/results?"
elif [ -n "$HOST_NAME" ]; then
    SAVE_DATA_URL="http://${HOST_NAME}:8042/api/speedtest/results?"
elif [ -n "$HOST_IP" ]; then
    SAVE_DATA_URL="http://${HOST_IP}:8042/api/speedtest/results?"
else
    echo "Warning: No host configured for result reporting"
    echo "Set HOST_IP, HOST_NAME, or REVERSE_PROXIED_HOST_NAME in .env"
    SAVE_DATA_URL=""
fi

# Patch index.html where saveData and saveDataURL are defined
HTML_FILE="/usr/share/nginx/html/index.html"

if [ -f "$HTML_FILE" ] && [ -n "$SAVE_DATA_URL" ]; then
    echo "Patching OpenSpeedTest to send results to: $SAVE_DATA_URL"

    # Enable saveData (change false to true)
    sed -i 's/var saveData = false;/var saveData = true;/' "$HTML_FILE"

    # Set the save URL
    sed -i "s|var saveDataURL = \"[^\"]*\";|var saveDataURL = \"$SAVE_DATA_URL\";|" "$HTML_FILE"

    # Verify the patch was applied
    if grep -q "saveData = true" "$HTML_FILE" && grep -q "$SAVE_DATA_URL" "$HTML_FILE"; then
        echo "OpenSpeedTest patched successfully"
    else
        echo "Warning: Patch may not have been applied correctly"
        grep "saveData" "$HTML_FILE"
    fi
elif [ ! -f "$HTML_FILE" ]; then
    echo "Warning: Could not find OpenSpeedTest index.html to patch"
elif [ -z "$SAVE_DATA_URL" ]; then
    echo "Warning: No save URL configured, results will not be reported"
fi

# Run the original entrypoint (OpenSpeedTest's nginx setup)
exec /entrypoint.sh
