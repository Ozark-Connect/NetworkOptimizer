#!/bin/sh
# Patch OpenSpeedTest to send results to Network Optimizer API

# Construct the save URL if not explicitly set
# Priority: OPENSPEEDTEST_SAVE_URL > REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
if [ -n "$OPENSPEEDTEST_SAVE_URL" ]; then
    SAVE_DATA_URL="$OPENSPEEDTEST_SAVE_URL"
elif [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - use https and no port (proxy handles it)
    SAVE_DATA_URL="https://${REVERSE_PROXIED_HOST_NAME}/api/speedtest/result?"
elif [ -n "$HOST_NAME" ]; then
    SAVE_DATA_URL="http://${HOST_NAME}:8042/api/speedtest/result?"
elif [ -n "$HOST_IP" ]; then
    SAVE_DATA_URL="http://${HOST_IP}:8042/api/speedtest/result?"
else
    echo "Warning: No host configured for result reporting"
    echo "Set HOST_IP, HOST_NAME, or REVERSE_PROXIED_HOST_NAME in .env"
    SAVE_DATA_URL=""
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
elif [ -z "$SAVE_DATA_URL" ]; then
    echo "Warning: No save URL configured, results will not be reported"
fi

# Run the original entrypoint (OpenSpeedTest's nginx setup)
exec /entrypoint.sh
