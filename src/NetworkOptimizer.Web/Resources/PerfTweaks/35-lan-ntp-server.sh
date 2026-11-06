#!/bin/bash
# 35-lan-ntp-server.sh: Let the gateway answer NTP requests from LAN clients
#
# UniFi OS keeps the gateway's clock with systemd-timesyncd, which is an NTP client
# only, so nothing on the gateway answers on UDP 123. UniFi Network can hand out an
# NTP server with DHCP option 42 per network, but the gateway itself cannot be one.
#
# This runs chronyd from Debian's chrony package as a serving-only NTP server. It
# is started with -x: it never sets the gateway's clock, timesyncd stays the
# gateway's own client and the UniFi NTP setting keeps working. chronyd polls the
# same servers that UniFi Network configures for the gateway, tracks how far the
# system clock is off from them and answers LAN clients with the corrected time.
# The package is not installed with apt (that would remove timesyncd): the pinned
# .deb is downloaded from Debian, checked against its SHA-256 and only chronyd and
# chronyc are extracted to /data. chrony is GPLv2; its copyright file is kept next
# to the binaries.
#
# chronyd answers only addresses on the LAN bridges (brN) and rate limits each
# client. Guest and DMZ networks reach it only once a firewall policy allows UDP
# 123 to the gateway; WAN requests stay dropped by UniFi's firewall. Point DHCP
# option 42 at the gateway in UniFi Network yourself.
#
# Files: /data/lan-ntp-server/bin/{chronyd,chronyc}, /data/lan-ntp-server/helper.sh,
#        /etc/systemd/system/lan-ntp-server{,-refresh}.service and
#        lan-ntp-server-refresh.timer (recreated at boot),
#        /run/lan-ntp-server/{chrony.conf,chronyd.sock} (RAM only).

LOG_TAG="lan-ntp-server"
DIR="/data/lan-ntp-server"
BIN="$DIR/bin"
HELPER="$DIR/helper.sh"
UNIT="/etc/systemd/system/lan-ntp-server.service"
REFRESH_UNIT="/etc/systemd/system/lan-ntp-server-refresh.service"
REFRESH_TIMER="/etc/systemd/system/lan-ntp-server-refresh.timer"

# Debian 13 (trixie) chrony, matching the UniFi OS 6 base. A security update is a bump
# of these values: the .deb hash comes from trixie's signed Packages index.
CHRONY_DEB="chrony_4.6.1-3+deb13u2_arm64.deb"
CHRONY_DEB_SHA256="efae2e04c60f675126b0251c391f7df23e87e8c776445516a282f5b7bba4769a"
CHRONY_URLS="https://deb.debian.org/debian/pool/main/c/chrony/chrony_4.6.1-3+deb13u2_arm64.deb
https://snapshot.debian.org/archive/debian/20260704T211317Z/pool/main/c/chrony/chrony_4.6.1-3%2Bdeb13u2_arm64.deb"
CHRONYD_SHA256="8b49e06c6a13954ac437ca3859a1c923206dfd6b8dc4279d5a207111c3ee71e2"
CHRONYC_SHA256="943a851395b3f2012bbecff0fd7606c6681b175a6e70aa95eac7c4ef2807eb83"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
}

if [ "$(dpkg --print-architecture 2>/dev/null)" != "arm64" ]; then
    log "Not an arm64 gateway, not installing."
    exit 1
fi
for tool in dpkg-deb sha256sum curl ip; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        log "$tool not found, not installing."
        exit 1
    fi
done

# chronyd runs as chrony's own user where the firmware has one, else as nobody
if getent passwd _chrony >/dev/null 2>&1; then
    RUN_USER="_chrony"
else
    RUN_USER="nobody"
fi
RUN_GROUP=$(id -gn "$RUN_USER" 2>/dev/null) || { log "No group for $RUN_USER."; exit 1; }

mkdir -p "$BIN" || { log "Creating $BIN failed."; exit 1; }
chmod 755 "$DIR" "$BIN"

binaries_ok() {
    [ -x "$BIN/chronyd" ] && [ -x "$BIN/chronyc" ] \
        && printf '%s  %s\n%s  %s\n' "$CHRONYD_SHA256" "$BIN/chronyd" "$CHRONYC_SHA256" "$BIN/chronyc" \
            | sha256sum -c --strict --status
}

# Downloads the pinned .deb and extracts chronyd and chronyc. Only on first deploy, after a
# version bump or when a binary on /data no longer matches its hash.
fetch_chrony() {
    local work deb url
    work=$(mktemp -d /run/lan-ntp-server-fetch.XXXXXX) || return 1
    deb="$work/$CHRONY_DEB"
    for url in $CHRONY_URLS; do
        if curl -fsSL --max-time 60 --max-filesize 4000000 -o "$deb" "$url" \
            && echo "$CHRONY_DEB_SHA256  $deb" | sha256sum -c --strict --status; then
            break
        fi
        log "Download from $url failed or did not match the pinned SHA-256."
        rm -f "$deb"
    done
    if [ ! -f "$deb" ]; then
        rm -rf "$work"
        return 1
    fi
    if ! dpkg-deb -x "$deb" "$work/x" \
        || ! install -m 755 "$work/x/usr/sbin/chronyd" "$BIN/chronyd.new" \
        || ! install -m 755 "$work/x/usr/bin/chronyc" "$BIN/chronyc.new" \
        || ! install -m 644 "$work/x/usr/share/doc/chrony/copyright" "$DIR/chrony-copyright"; then
        rm -rf "$work" "$BIN/chronyd.new" "$BIN/chronyc.new"
        return 1
    fi
    mv -f "$BIN/chronyd.new" "$BIN/chronyd" && mv -f "$BIN/chronyc.new" "$BIN/chronyc"
    local rc=$?
    rm -rf "$work"
    return $rc
}

BIN_CHANGED=""
if ! binaries_ok; then
    log "Fetching $CHRONY_DEB from Debian..."
    if ! fetch_chrony || ! binaries_ok; then
        log "Installing chrony failed."
        exit 1
    fi
    log "chrony extracted to $BIN."
    BIN_CHANGED="changed"
fi

# A firmware update can drop a library chronyd links against
if ! VERSION_OUT=$("$BIN/chronyd" -v 2>&1); then
    log "chronyd does not run on this firmware: $VERSION_OUT"
    exit 1
fi

# The prototype's Python daemon, if an earlier build left it behind
rm -f "$DIR/lan-ntp-server.py"

# Writes stdin to $1 (mode $2) only when the content differs, so a reboot writes nothing to
# the eMMC: the comparison copy lives on tmpfs. Prints "changed" when it wrote; fails without
# touching $1 when any step fails.
install_if_changed() {
    local target="$1" mode="$2" tmp
    tmp=$(mktemp /run/lan-ntp-server-install.XXXXXX) || return 1
    if ! cat > "$tmp"; then
        rm -f "$tmp"
        return 1
    fi
    if [ -f "$target" ] && cmp -s "$tmp" "$target"; then
        rm -f "$tmp"
        chmod "$mode" "$target"
        return
    fi
    if install -m "$mode" "$tmp" "$target.new" && mv -f "$target.new" "$target"; then
        rm -f "$tmp"
        echo "changed"
        return 0
    fi
    rm -f "$tmp" "$target.new"
    return 1
}

if ! HELPER_CHANGED=$(install_if_changed "$HELPER" 755 <<'HELPEREOF'
#!/bin/sh
# Helper for lan-ntp-server.service, installed by /data/on_boot.d/35-lan-ntp-server.sh.
#   wait-sync   waits up to two minutes for timesyncd's first sync, so chronyd does not
#               start its estimate on a clock that timesyncd is about to step
#   print-conf  prints chrony.conf: the gateway's own NTP servers and the LAN bridges
#   check-sockets  prints "ok" when chronyd holds UDP 123 for every address family it allows;
#               chronyd only warns when a port is taken and then serves the other family alone
#   refresh     restarts the service when print-conf no longer matches the running config or
#               a socket is missing
CONF=/run/lan-ntp-server/chrony.conf
UDAPI_CONF=/run/systemd/timesyncd.conf.d/ubios-udapi-server.conf

print_conf() {
    echo "# Generated by /data/lan-ntp-server/helper.sh at service start"
    local_addrs=$(ip -o addr show 2>/dev/null | awk '{ sub(/\/.*/, "", $4); print $4 }')
    # systemd semantics: the last Servers= line wins, an empty one resets the list
    servers=$(sed -n 's/^Servers=//p' "$UDAPI_CONF" 2>/dev/null | tail -n 1)
    [ -n "$servers" ] || servers="0.ubnt.pool.ntp.org 1.ubnt.pool.ntp.org"
    for s in $servers; do
        case "$s" in *[!A-Za-z0-9.:-]*) continue ;; esac
        # Never poll ourselves, should the gateway's NTP setting point at the gateway
        if printf '%s\n' "$local_addrs" | grep -qxF "$s"; then
            continue
        fi
        # Sources on private addresses (a GPS box, the Starlink dish) are the user's own:
        # prefer them, public servers only take over when none of them is usable
        case "$s" in
            10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*|169.254.*|[fF][cCdD]*:*|[fF][eE]80:*)
                echo "server $s iburst prefer" ;;
            *)
                echo "server $s iburst" ;;
        esac
    done
    ip -o addr show 2>/dev/null | awk '
        $2 ~ /^br[0-9]+$/ && $3 == "inet" { print "allow " $4 }
        $2 ~ /^br[0-9]+$/ && $3 == "inet6" && $4 !~ /^fe80:/ { print "allow " $4 }'
    cat <<'EOF'
makestep 1 -1
ratelimit interval 1 burst 16
cmdport 0
bindcmdaddress /run/lan-ntp-server/chronyd.sock
pidfile /run/lan-ntp-server/chronyd.pid
EOF
}

check_sockets() {
    # Without an allow line chronyd answers nobody, however many sockets it holds
    if ! grep -q '^allow ' "$CONF" 2>/dev/null; then
        echo "no LAN bridge address to serve, chronyd answers no client"
        return 1
    fi
    socks=$(ss -Hlunp 'sport = :123' 2>/dev/null)
    ours=$(printf '%s\n' "$socks" | grep '"chronyd"')
    missing=""
    if grep -q '^allow [0-9]*\.' "$CONF" 2>/dev/null && ! printf '%s\n' "$ours" | grep -q '0\.0\.0\.0:123 '; then
        missing="IPv4"
    fi
    if grep -q '^allow [0-9a-fA-F]*:' "$CONF" 2>/dev/null && ! printf '%s\n' "$ours" | grep -qE '(\[::\]|\*):123 '; then
        missing="${missing:+$missing and }IPv6"
    fi
    if [ -z "$missing" ]; then
        echo "ok"
        return 0
    fi
    echo "chronyd is not listening on UDP 123 for $missing: $(printf '%s\n' "$socks" | grep -v '"chronyd"' | head -n 1)"
    return 1
}

case "$1" in
    check-sockets)
        check_sockets
        ;;
    wait-sync)
        i=0
        while [ ! -e /run/systemd/timesync/synchronized ] && [ "$i" -lt 120 ]; do
            sleep 1
            i=$((i + 1))
        done
        exit 0
        ;;
    print-conf)
        print_conf
        ;;
    write-conf)
        print_conf > "$CONF.new" && mv -f "$CONF.new" "$CONF"
        ;;
    refresh)
        systemctl is-active --quiet lan-ntp-server || exit 0
        new=$(print_conf) || exit 1
        if [ "$new" != "$(cat "$CONF" 2>/dev/null)" ]; then
            echo "Networks or NTP servers changed, restarting lan-ntp-server"
            systemctl restart --no-block lan-ntp-server
        elif ! sockets=$(check_sockets); then
            echo "$sockets; restarting lan-ntp-server to open UDP 123 again"
            systemctl restart --no-block lan-ntp-server
        fi
        ;;
    *)
        echo "usage: $0 wait-sync|print-conf|write-conf|refresh" >&2
        exit 2
        ;;
esac
HELPEREOF
); then
    log "Writing $HELPER failed."
    exit 1
fi

if ! UNIT_CHANGED=$(install_if_changed "$UNIT" 644 <<EOF
[Unit]
Description=Answer NTP requests from LAN clients (chronyd, serving only)
After=network-online.target systemd-timesyncd.service
Wants=network-online.target
StartLimitIntervalSec=0

[Service]
User=$RUN_USER
Group=$RUN_GROUP
ExecStartPre=$HELPER wait-sync
ExecStartPre=$HELPER write-conf
# -x: never touch the system clock, timesyncd keeps it. -U: start without root.
ExecStart=$BIN/chronyd -x -U -d -u $RUN_USER -f /run/lan-ntp-server/chrony.conf
TimeoutStartSec=180
Restart=always
RestartSec=30
RuntimeDirectory=lan-ntp-server
RuntimeDirectoryMode=0750
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectClock=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
LockPersonality=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
RestrictNamespaces=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
MemoryMax=64M

[Install]
WantedBy=multi-user.target
EOF
); then
    log "Writing $UNIT failed."
    exit 1
fi

# Picks up new networks, a changed IPv6 prefix or a changed NTP setting in UniFi Network
if ! REFRESH_CHANGED=$(install_if_changed "$REFRESH_UNIT" 644 <<EOF
[Unit]
Description=Restart lan-ntp-server when the LAN networks or NTP servers change

[Service]
Type=oneshot
ExecStart=$HELPER refresh
EOF
); then
    log "Writing $REFRESH_UNIT failed."
    exit 1
fi
if ! TIMER_CHANGED=$(install_if_changed "$REFRESH_TIMER" 644 <<'EOF'
[Unit]
Description=Check lan-ntp-server's networks and NTP servers every five minutes

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
); then
    log "Writing $REFRESH_TIMER failed."
    exit 1
fi

UNITS_CHANGED="$UNIT_CHANGED$REFRESH_CHANGED$TIMER_CHANGED"
# Also when an earlier run wrote the units but its daemon-reload failed
if { [ -n "$UNITS_CHANGED" ] || [ "$(systemctl show -p NeedDaemonReload --value lan-ntp-server 2>/dev/null)" = "yes" ]; } \
    && ! systemctl daemon-reload; then
    log "systemctl daemon-reload failed."
    exit 1
fi

# Something else answering NTP would make chronyd fail on every restart
if ! systemctl is-active --quiet lan-ntp-server && ss -Hlun 'sport = :123' 2>/dev/null | grep -q .; then
    log "UDP 123 is already in use: $(ss -Hlunp 'sport = :123' 2>/dev/null | head -n 1)"
    exit 1
fi

if ! systemctl enable lan-ntp-server lan-ntp-server-refresh.timer >/dev/null 2>&1; then
    log "Enabling the service failed."
    exit 1
fi
if ! systemctl start lan-ntp-server-refresh.timer >/dev/null 2>&1; then
    log "Starting the refresh timer failed."
    exit 1
fi

# Before timesyncd's first sync the service waits in ExecStartPre; at boot that must not hold
# up the other boot scripts, so only then the restart does not block
if [ -e /run/systemd/timesync/synchronized ]; then
    BLOCK=""
else
    BLOCK="--no-block"
fi
if [ -n "$UNIT_CHANGED$HELPER_CHANGED$BIN_CHANGED" ] || ! systemctl is-active --quiet lan-ntp-server; then
    if ! systemctl restart $BLOCK lan-ntp-server; then
        log "systemctl restart failed: $(journalctl -u lan-ntp-server -n 3 -o cat --no-pager 2>/dev/null | tr '\n' ' ')"
        exit 1
    fi
fi
if [ -n "$BLOCK" ]; then
    log "Service starts after the gateway's first time sync."
    exit 0
fi

# chronyd exits at once on a bad config; "activating" would then hide a restart loop
sleep 3
if [ "$(systemctl show -p SubState --value lan-ntp-server 2>/dev/null)" = "running" ]; then
    if ! SOCKETS=$("$HELPER" check-sockets); then
        log "$SOCKETS"
        exit 1
    fi
    log "Service active."
else
    log "Service failed to start: $(journalctl -u lan-ntp-server -n 3 -o cat --no-pager 2>/dev/null | tr '\n' ' ')"
    exit 1
fi
