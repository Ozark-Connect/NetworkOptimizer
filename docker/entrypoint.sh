#!/bin/bash
set -e

# Set container timezone from TZ env var so .NET TimeZoneInfo.Local is correct
if [ -n "$TZ" ] && [ -f "/usr/share/zoneinfo/$TZ" ]; then
    ln -sf "/usr/share/zoneinfo/$TZ" /etc/localtime
    echo "$TZ" > /etc/timezone
fi

# Fix ownership of mounted volumes (they may be created as root by Docker)
# This runs as root before dropping to the app user
chown -R app:app /app/data /app/logs /app/ssh-keys 2>/dev/null || true

# Set bind address based on BIND_LOCALHOST_ONLY
# Default: false (bind to all interfaces for direct network access)
# Set to true when behind a reverse proxy on the same host
if [ "${BIND_LOCALHOST_ONLY,,}" = "true" ]; then
    export ASPNETCORE_URLS="http://127.0.0.1:8042"
    echo "Binding to localhost only (127.0.0.1:8042)"
else
    export ASPNETCORE_URLS="http://0.0.0.0:8042"
    echo "Binding to all interfaces (0.0.0.0:8042)"
fi

# Drop to app user and run the application
exec gosu app dotnet NetworkOptimizer.Web.dll "$@"
