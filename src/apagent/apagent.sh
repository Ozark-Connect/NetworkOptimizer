#!/bin/sh
# Architecture gate. A wrong-arch binary dies with "Exec format error", which tells an operator
# nothing, so refuse before exec and say what is missing. Exit 78 (EX_CONFIG) means "cannot run
# here" as distinct from a crash in the deploy tooling.
#
# This wrapper ships alongside the binary in a tmpfs install directory. The AP agent is ephemeral:
# nothing here is expected to survive a reboot, and the server redeploys it.

DIR=$(dirname "$0")

# The kernel reports "mips" for both byte orders (a little-endian U6-Lite says "mips"), so the ELF
# header's EI_DATA byte of busybox decides: 1 little-endian, 2 big-endian. od is absent on U7.
mips_bin() {
  b=$(dd if=/bin/busybox bs=1 skip=5 count=1 2>/dev/null)
  if [ "$b" = "$(printf '\001')" ]; then echo apagent-linux-mipsle
  elif [ "$b" = "$(printf '\002')" ]; then echo apagent-linux-mips
  fi
}

case "$(uname -m)" in
  armv6l|armv7l|armv8l) BIN=apagent-linux-arm ;;
  aarch64|arm64)        BIN=apagent-linux-arm64 ;;
  mips|mips32|mipsel|mips32el)
    BIN=$(mips_bin)
    if [ -z "$BIN" ]; then echo "apagent: could not read the byte order of $(uname -m)" >&2; exit 78; fi ;;
  *) echo "apagent: unsupported arch: $(uname -m) (need armv7l, mips, or aarch64)" >&2; exit 78 ;;
esac

if [ ! -x "$DIR/$BIN" ]; then
  echo "apagent: missing build for $(uname -m): $BIN" >&2
  exit 78
fi

exec "$DIR/$BIN" "$@"
