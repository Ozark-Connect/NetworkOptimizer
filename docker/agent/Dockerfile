# Network Optimizer on-site agent.
# Built per-architecture on native runners (see .github/workflows/release.yml),
# so TARGETARCH matches the build host and no QEMU emulation is needed.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac && \
    dotnet publish src/NetworkOptimizer.Agent/NetworkOptimizer.Agent.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -o /app/publish

# uwnspeedtest Go binary for WAN speed tests run site-local at the agent
# (uwnspeedtest imports the cfspeedtest module). Built for the target arch.
FROM golang:1.22-alpine AS uwnspeedtest-build
ARG TARGETARCH
WORKDIR /src
COPY src/cfspeedtest/ cfspeedtest/
COPY src/uwnspeedtest/ uwnspeedtest/
WORKDIR /src/uwnspeedtest
RUN CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} go build -trimpath \
    -ldflags "-s -w" -o /uwnspeedtest .

# runtime-deps carries the native dependencies a self-contained .NET app needs.
# nginx serves the OpenSpeedTest page + the throughput-critical transfer legs
# (sendfile, so it saturates 10 GbE on modest hardware); iperf3 (LAN speed test)
# and iputils-ping (per-WAN source-bound probes) round it out. The .NET agent
# runs the tunnel plus the loopback results relay.
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends nginx iperf3 iputils-ping ca-certificates \
    && rm -rf /var/lib/apt/lists/* /etc/nginx/sites-enabled/default
WORKDIR /app
COPY --from=build /app/publish/NetworkOptimizer.Agent /app/NetworkOptimizer.Agent
# Site-local WAN speed test binary, resolved at AppContext.BaseDirectory/uwnspeedtest.
COPY --from=uwnspeedtest-build /uwnspeedtest /app/uwnspeedtest

# OpenSpeedTest, served by nginx. config.js is overridden so results relay
# same-origin through the agent (see docker/agent/nginx.conf + config.js).
COPY src/OpenSpeedTest/ /usr/share/nginx/html/
COPY docker/agent/config.js /usr/share/nginx/html/assets/js/config.js
# Run our OWN nginx master from an explicit standalone config (the same
# nginx-standalone.conf the bare-metal installer uses), wrapping the shared server
# block - rather than leaning on the base image's top-level nginx.conf. The webroot
# stays /usr/share/nginx/html (set in the server block).
COPY docker/agent/nginx.conf /etc/nginx/netopt-speedtest-server.conf
COPY docker/agent/nginx-standalone.conf /etc/nginx/netopt-speedtest.conf
RUN sed -i \
        -e 's#__PIDFILE__#/run/nginx-speedtest.pid#' \
        -e 's#__ERRORLOG__#/dev/stderr#' \
        -e 's#__SERVERCONF__#/etc/nginx/netopt-speedtest-server.conf#' \
        /etc/nginx/netopt-speedtest.conf
COPY docker/agent/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# The agent persists its key/slug back into the config, so it must live on a
# mounted volume - otherwise the agent re-enrolls on every container recreate.
ENV NO_AGENT_CONFIG=/data/agent.json
VOLUME /data

ENTRYPOINT ["/entrypoint.sh"]
