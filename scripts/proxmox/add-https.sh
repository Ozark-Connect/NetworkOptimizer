#!/usr/bin/env bash
# Network Optimizer - add HTTPS (Traefik + Let's Encrypt) to an EXISTING Proxmox install.
#
# The interactive installer offers HTTPS via Traefik at install time; this supplement
# retrofits the same setup onto a container installed before that option existed (or
# where it was declined). It deploys the same Traefik proxy the installer uses
# (Ozark-Connect/NetworkOptimizer-Proxy, Let's Encrypt DNS-01 via Cloudflare) and
# updates the app's .env to match, without touching application data.
#
# Run on the Proxmox HOST (same place the installer ran):
#   bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/add-https.sh)"
#
# Prerequisites:
#   - DNS A/AAAA records for your hostnames pointing at the container (or your router,
#     with 443 forwarded to the container)
#   - A Cloudflare API token with Zone:DNS:Edit on the zone (DNS-01 challenge)

set -euo pipefail

RD='\033[01;31m'; GN='\033[1;92m'; YW='\033[33m'; DIM='\033[2m'; CL='\033[m'; WH='\033[1;97m'
msg_info() { echo -e " ${YW}-${CL} $1"; }
msg_ok()   { echo -e " ${GN}+${CL} $1"; }
msg_err()  { echo -e " ${RD}x${CL} $1"; }

APP_DIR="/opt/network-optimizer"
PROXY_DIR="/opt/network-optimizer-proxy"
PROXY_REPO="Ozark-Connect/NetworkOptimizer-Proxy"
PROXY_BRANCH="main"

echo -e "\n${WH}Network Optimizer - add HTTPS to an existing Proxmox install${CL}\n"

if ! command -v pct >/dev/null 2>&1; then
    msg_err "pct not found - run this on the Proxmox host, not inside the container."
    exit 1
fi

read -rp "Container ID of the Network Optimizer LXC: " CT_ID
if ! pct status "$CT_ID" >/dev/null 2>&1; then
    msg_err "Container $CT_ID not found."
    exit 1
fi
if [[ "$(pct status "$CT_ID")" != *running* ]]; then
    msg_err "Container $CT_ID is not running - start it first (pct start $CT_ID)."
    exit 1
fi
if ! pct exec "$CT_ID" -- test -d "$APP_DIR"; then
    msg_err "$APP_DIR not found in container $CT_ID - is this the Network Optimizer LXC?"
    exit 1
fi
msg_ok "Found Network Optimizer in container $CT_ID"

# The compose the installer generates passes the reverse-proxy variables through from
# .env. A very old install may predate that; .env edits would then silently do nothing.
if ! pct exec "$CT_ID" -- grep -q "REVERSE_PROXIED_HOST_NAME" "$APP_DIR/docker-compose.yml"; then
    msg_err "This container's docker-compose.yml predates reverse-proxy support."
    echo -e "${DIM}Back up $APP_DIR/data, re-run the current installer to regenerate the${CL}"
    echo -e "${DIM}compose file, then run this script again.${CL}"
    exit 1
fi

echo
echo -e "${DIM}Hostname the app will be served at (must resolve to this container/site).${CL}"
read -rp "App HTTPS hostname (e.g., optimizer.example.com): " OPTIMIZER_HOST
if [[ -z "$OPTIMIZER_HOST" ]]; then
    msg_err "A hostname is required."
    exit 1
fi

echo
echo -e "${DIM}Optional: serve the browser speed test over HTTPS too (required for GPS-tagged${CL}"
echo -e "${DIM}results, and Chrome/Edge block speed test timing APIs on plain HTTP).${CL}"
read -rp "Speed test HTTPS hostname (e.g., speedtest.example.com, empty to skip): " SPEEDTEST_HOST

echo
read -rp "Let's Encrypt account email: " ACME_EMAIL
echo -e "${DIM}Cloudflare API token with Zone:DNS:Edit on the zone (DNS-01 challenge).${CL}"
echo -e "${DIM}Create one at: https://dash.cloudflare.com/profile/api-tokens${CL}"
read -rsp "Cloudflare DNS API token: " CF_TOKEN
echo
if [[ -z "$ACME_EMAIL" || -z "$CF_TOKEN" ]]; then
    msg_err "Email and Cloudflare token are both required."
    exit 1
fi

# The speed test's published port, for the Traefik route backend.
SPEEDTEST_PORT=$(pct exec "$CT_ID" -- bash -c "grep -oP '^OPENSPEEDTEST_PORT=\K.*' $APP_DIR/.env 2>/dev/null" || true)
SPEEDTEST_PORT=${SPEEDTEST_PORT:-3005}

if pct exec "$CT_ID" -- test -d "$PROXY_DIR"; then
    echo
    msg_info "$PROXY_DIR already exists - its config will be regenerated (certificates are kept)."
    read -rp "Continue? [y/N]: " cont
    [[ "${cont,,}" =~ ^(y|yes)$ ]] || exit 0
fi

echo
msg_info "Deploying Traefik proxy..."
pct exec "$CT_ID" -- mkdir -p "$PROXY_DIR/dynamic" "$PROXY_DIR/acme"
pct exec "$CT_ID" -- curl -fsSL \
    "https://raw.githubusercontent.com/${PROXY_REPO}/${PROXY_BRANCH}/docker-compose.yml" \
    -o "$PROXY_DIR/docker-compose.yml"
pct exec "$CT_ID" -- curl -fsSL \
    "https://raw.githubusercontent.com/${PROXY_REPO}/${PROXY_BRANCH}/config.example.yml" \
    -o "$PROXY_DIR/config.example.yml"
pct exec "$CT_ID" -- curl -fsSL \
    "https://raw.githubusercontent.com/${PROXY_REPO}/${PROXY_BRANCH}/.env.example" \
    -o "$PROXY_DIR/.env.example"
msg_ok "Traefik configuration downloaded"

# Route hostnames + speed test backend port into the dynamic config, exactly as the
# installer does. An omitted speed test hostname keeps the example host, which never
# resolves - the route is inert.
pct exec "$CT_ID" -- bash -c "
    sed -e 's/optimizer\\.example\\.com/${OPTIMIZER_HOST}/g' \
        -e 's/speedtest\\.example\\.com/${SPEEDTEST_HOST:-speedtest.example.com}/g' \
        -e 's|http://localhost:3005|http://localhost:${SPEEDTEST_PORT}|g' \
        '$PROXY_DIR/config.example.yml' > '$PROXY_DIR/dynamic/config.yml'
"
msg_ok "Routes configured"

PROXY_ENV="# Traefik Proxy - generated by add-https.sh
ACME_EMAIL=${ACME_EMAIL}
CF_DNS_API_TOKEN=${CF_TOKEN}"
ENCODED=$(echo "$PROXY_ENV" | base64 -w 0)
pct exec "$CT_ID" -- bash -c "echo '$ENCODED' | base64 -d > $PROXY_DIR/.env"
pct exec "$CT_ID" -- bash -c "touch $PROXY_DIR/acme/acme.json && chmod 600 $PROXY_DIR/acme/acme.json"
msg_ok "Proxy environment written"

msg_info "Starting Traefik..."
pct exec "$CT_ID" -- bash -c "cd $PROXY_DIR && docker compose pull -q && docker compose up -d"
msg_ok "Traefik running"

msg_info "Updating app configuration..."
UPSERT_SCRIPT="
set -e
upsert() {
    if grep -q \"^\$1=\" '$APP_DIR/.env'; then
        sed -i \"s|^\$1=.*|\$1=\$2|\" '$APP_DIR/.env'
    else
        printf '%s=%s\n' \"\$1\" \"\$2\" >> '$APP_DIR/.env'
    fi
}
upsert REVERSE_PROXIED_HOST_NAME '$OPTIMIZER_HOST'
# Traefik serves on 443, so any manually-set non-standard front-end port no longer applies
sed -i '/^REVERSE_PROXIED_PORT=/d' '$APP_DIR/.env'
upsert BIND_LOCALHOST_ONLY true
"
if [[ -n "$SPEEDTEST_HOST" ]]; then
    UPSERT_SCRIPT="$UPSERT_SCRIPT
upsert OPENSPEEDTEST_HTTPS true
upsert OPENSPEEDTEST_HOST '$SPEEDTEST_HOST'
"
fi
ENCODED=$(echo "$UPSERT_SCRIPT" | base64 -w 0)
pct exec "$CT_ID" -- bash -c "echo '$ENCODED' | base64 -d | bash"
msg_ok "App .env updated"

msg_info "Restarting Network Optimizer..."
pct exec "$CT_ID" -- bash -c "cd $APP_DIR && docker compose up -d"
for i in $(seq 1 24); do
    if pct exec "$CT_ID" -- curl -sf http://localhost:8042/api/health >/dev/null 2>&1; then
        break
    fi
    sleep 5
done
if pct exec "$CT_ID" -- curl -sf http://localhost:8042/api/health >/dev/null 2>&1; then
    msg_ok "Application healthy"
else
    msg_err "Application did not report healthy in time - check: pct exec $CT_ID -- docker logs network-optimizer"
fi

echo
echo -e "${WH}Done.${CL} Point DNS (and any router port-forward for 443) at this container, then:"
echo -e "  App:        ${GN}https://${OPTIMIZER_HOST}${CL}"
if [[ -n "$SPEEDTEST_HOST" ]]; then
    echo -e "  Speed test: ${GN}https://${SPEEDTEST_HOST}${CL}"
fi
echo -e "${DIM}First certificate issuance can take a minute; watch: pct exec $CT_ID -- docker logs -f traefik-proxy${CL}"
