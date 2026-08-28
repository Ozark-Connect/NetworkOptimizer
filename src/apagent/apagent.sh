#!/bin/sh
# Architecture gate. A wrong-arch binary dies with "Exec format error", which tells an operator
# nothing, so refuse before exec and say what is missing. Exit 78 (EX_CONFIG) means "cannot run
# here" as distinct from a crash in the deploy tooling.
#
# This wrapper ships alongside the binary in a tmpfs install directory. The AP agent is ephemeral:
# nothing here is expected to survive a reboot, and the server redeploys it.

DIR=$(dirname "$0")

case "$(uname -m)" in
  armv6l|armv7l|armv8l) BIN=apagent-linux-arm ;;
  aarch64|arm64)        BIN=apagent-linux-arm64 ;;
  *) echo "apagent: unsupported arch: $(uname -m) (need armv7l or aarch64)" >&2; exit 78 ;;
esac

if [ ! -x "$DIR/$BIN" ]; then
  echo "apagent: missing build for $(uname -m): $BIN" >&2
  exit 78
fi

exec "$DIR/$BIN" "$@"
