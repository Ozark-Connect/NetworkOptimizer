# Network Optimizer on-site agent.
# Built per-architecture on native runners (see .github/workflows/release.yml),
# so TARGETARCH matches the build host and no QEMU emulation is needed.

ARG DOTNET_VERSION=10.0
# Passed from CI on tag push. MinVer reads git tags and .dockerignore keeps .git out of the
# build context, so without this every image - released ones included - calls itself
# 0.0.0-alpha.0.
ARG VERSION=0.0.0-alpha.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION
WORKDIR /src
COPY . .
# Grpc.Tools' bundled linux_arm64 protoc segfaults (exit 139) under dotnet on native
# arm64 runners (grpc/grpc#26032, #38538); use the distro's protoc there. amd64 unchanged.
RUN if [ "$TARGETARCH" = "arm64" ]; then \
        apt-get update && apt-get install -y --no-install-recommends protobuf-compiler && rm -rf /var/lib/apt/lists/*; \
    fi
RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64; export PROTOBUF_PROTOC=/usr/bin/protoc ;; \
        *) echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1 ;; \
    esac && \
    dotnet publish src/NetworkOptimizer.Agent/NetworkOptimizer.Agent.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -p:MinVerVersionOverride=${VERSION} \
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
    && apt-get install -y --no-install-recommends nginx iperf3 iputils-ping ca-certificates openssl \
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
COPY docker/agent/nginx.conf /etc/nginx/netopt-speedtest-server.conf.template
# The entrypoint rewrites the listener and (optionally) strips TLS into the live file each
# start. It works from this pristine copy rather than editing in place, because a container
# restart keeps the filesystem - an in-place edit would be permanent and the next boot could
# not put it back.
COPY docker/agent/nginx-standalone.conf /etc/nginx/netopt-speedtest.conf
# The self-signed cert lives in the persisted /data volume so it survives restarts
# (entrypoint.sh generates it once); wire the server block's cert paths to it.
RUN sed -i \
        -e 's#__PIDFILE__#/run/nginx-speedtest.pid#' \
        -e 's#__ERRORLOG__#/dev/stderr#' \
        -e 's#__SERVERCONF__#/etc/nginx/netopt-speedtest-server.conf#' \
        /etc/nginx/netopt-speedtest.conf \
    && sed -i \
        -e 's#__CERTFILE__#/data/speedtest-tls/cert.pem#' \
        -e 's#__KEYFILE__#/data/speedtest-tls/key.pem#' \
        /etc/nginx/netopt-speedtest-server.conf.template
COPY docker/agent/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# The agent persists its key/slug back into the config, so it must live on a
# mounted volume - otherwise the agent re-enrolls on every container recreate.
ENV NO_AGENT_CONFIG=/data/agent.json
VOLUME /data

ENTRYPOINT ["/entrypoint.sh"]
