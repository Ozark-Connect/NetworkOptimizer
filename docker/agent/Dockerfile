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

# runtime-deps carries the native dependencies a self-contained .NET app needs.
# iperf3 (LAN speed test) and iputils-ping (per-WAN source-bound probes) are the
# only extras; the OpenSpeedTest page is embedded in the binary itself.
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends iperf3 iputils-ping ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish/NetworkOptimizer.Agent /app/NetworkOptimizer.Agent

# The agent persists its key/slug back into the config, so it must live on a
# mounted volume - otherwise the agent re-enrolls on every container recreate.
ENV NO_AGENT_CONFIG=/data/agent.json
VOLUME /data

ENTRYPOINT ["/app/NetworkOptimizer.Agent"]
