#!/bin/sh
# The agent container runs two processes: nginx (serves OpenSpeedTest + the
# throughput-critical transfer legs on port 3000) and the .NET agent (the tunnel
# plus the loopback results relay on 3001). nginx runs in the background; the
# agent is the main process, so `docker stop` (SIGTERM to it) tears the container
# down and nginx goes with it.
set -e
nginx -g 'daemon off;' &
exec /app/NetworkOptimizer.Agent
