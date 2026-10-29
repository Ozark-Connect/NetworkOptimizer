#!/bin/sh
# Dev server launcher for Network Optimizer (ASP.NET Core / Blazor Server).
# Forwards CLI host/port arguments to dotnet run so preview tooling can pick the port.
# Defaults: http://localhost:8042 (the app's canonical port).

PORT=8042
HOST=localhost

while [ $# -gt 0 ]; do
  case "$1" in
    --port)      PORT="$2"; shift 2 ;;
    --port=*)    PORT="${1#*=}"; shift ;;
    --host)      HOST="$2"; shift 2 ;;
    --host=*)    HOST="${1#*=}"; shift ;;
    *)           shift ;;
  esac
done

# npm may also surface flags as env vars; honor them if set
[ -n "$npm_config_port" ] && PORT="$npm_config_port"
[ -n "$npm_config_host" ] && HOST="$npm_config_host"

cd "$(dirname "$0")"
echo "Starting Network Optimizer on http://${HOST}:${PORT}"
exec dotnet run --no-launch-profile --urls "http://${HOST}:${PORT}"
