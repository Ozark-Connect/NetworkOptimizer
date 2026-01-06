#!/bin/sh
# Ozark Connect Speed Test - Entrypoint
# Injects runtime configuration into config.js

# Construct the save URL if not explicitly set
# Priority: OPENSPEEDTEST_SAVE_URL > REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
if [ -n "$OPENSPEEDTEST_SAVE_URL" ]; then
    SAVE_DATA_URL="$OPENSPEEDTEST_SAVE_URL"
elif [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - use https and no port (proxy handles it)
    SAVE_DATA_URL="https://${REVERSE_PROXIED_HOST_NAME}/api/public/speedtest/results?"
elif [ -n "$HOST_NAME" ]; then
    SAVE_DATA_URL="http://${HOST_NAME}:8042/api/public/speedtest/results?"
elif [ -n "$HOST_IP" ]; then
    SAVE_DATA_URL="http://${HOST_IP}:8042/api/public/speedtest/results?"
else
    echo "Warning: No host configured for result reporting"
    echo "Set HOST_IP, HOST_NAME, or REVERSE_PROXIED_HOST_NAME in .env"
    SAVE_DATA_URL=""
fi

# Inject configuration into config.js
CONFIG_FILE="/usr/share/nginx/html/assets/js/config.js"

if [ -f "$CONFIG_FILE" ]; then
    echo "Configuring speed test..."

    # Determine if saveData should be enabled
    if [ -n "$SAVE_DATA_URL" ]; then
        SAVE_DATA_VALUE="true"
        echo "Results will be sent to: $SAVE_DATA_URL"
    else
        SAVE_DATA_VALUE="false"
        echo "Result reporting disabled (no host configured)"
    fi

    # Replace placeholders with actual values
    sed -i "s|__SAVE_DATA__|$SAVE_DATA_VALUE|g" "$CONFIG_FILE"
    sed -i "s|__SAVE_DATA_URL__|$SAVE_DATA_URL|g" "$CONFIG_FILE"

    echo "Configuration complete"
else
    echo "Warning: config.js not found at $CONFIG_FILE"
fi

# Start nginx
exec "$@"
