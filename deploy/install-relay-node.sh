#!/usr/bin/env bash
# EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM
#
# Installs the pveTunnel relay on one Proxmox VE node.
#
# Run from the unpacked release directory, as root:
#   ./install-relay-node.sh
#
# Idempotent: re-running upgrades the binary in place and restarts the service. The previous
# binary is kept alongside as .previous so a bad build can be rolled back without a rebuild.

set -euo pipefail

NOTICE='EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM'
INSTALL_DIR=/opt/ludus/EXERCISE-OUT-OF-SCOPE/pve-tunnel
INSTALLED_BIN="$INSTALL_DIR/EXERCISE-OUT-OF-SCOPE-pve-tunnel-relay"
UNIT_NAME=EXERCISE-OUT-OF-SCOPE-pve-tunnel-relay.service
UNIT_PATH="/etc/systemd/system/$UNIT_NAME"
TMPFILES_PATH=/etc/tmpfiles.d/EXERCISE-OUT-OF-SCOPE-pve-tunnel.conf
REGISTRY_DIR=/etc/pve/pve-tunnel
REGISTRY_PATH="$REGISTRY_DIR/vms.json"
CONTROL_SOCKET=/run/pve-tunnel/control.sock

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

say() { printf '%s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "must run as root (the relay reads /proc/<qemu-pid>/cmdline and owns /run/pve-tunnel)"
command -v systemctl >/dev/null || die "systemd not found; this script targets a Proxmox VE node"

src_bin="$here/pve-tunnel-relay"
[[ -f "$src_bin" ]] || die "pve-tunnel-relay not found next to this script. Build it with deploy/build-release.ps1 and copy the release directory here."

# Verify against the manifest when one is present, so a truncated copy is caught before install.
manifest="$here/EXERCISE-OUT-OF-SCOPE-pveTunnel-release-manifest.json"
if [[ -f "$manifest" ]] && command -v python3 >/dev/null; then
    expected="$(python3 - "$manifest" <<'PY'
import json,sys
m=json.load(open(sys.argv[1]))
print(next((a["sha256"] for a in m.get("artifacts",[]) if a["file"]=="pve-tunnel-relay"), ""))
PY
)"
    if [[ -n "$expected" ]]; then
        actual="$(sha256sum "$src_bin" | awk '{print $1}')"
        [[ "$actual" == "$expected" ]] || die "relay checksum mismatch: manifest says $expected, file is $actual"
        say "checksum verified against release manifest"
    fi
fi

say "installing pveTunnel relay"
say "$NOTICE"

install -d -m 0755 "$INSTALL_DIR"
if [[ -f "$INSTALLED_BIN" ]]; then
    cp -f "$INSTALLED_BIN" "$INSTALLED_BIN.previous"
    say "kept previous binary at $INSTALLED_BIN.previous"
fi
install -m 0755 "$src_bin" "$INSTALLED_BIN"
printf '%s\n' "$NOTICE" > "$INSTALL_DIR/EXERCISE-OUT-OF-SCOPE.txt"
[[ -f "$manifest" ]] && cp -f "$manifest" "$INSTALL_DIR/"

install -m 0644 "$here/pve-tunnel.conf" "$TMPFILES_PATH"
systemd-tmpfiles --create "$TMPFILES_PATH" >/dev/null 2>&1 || true

# The registry lives on the cluster filesystem so a live migration finds the same allocation on
# the destination node. /etc/pve is pmxcfs: it manages its own ownership and rejects chmod, so
# do not treat a failed mode change there as fatal.
install -d "$REGISTRY_DIR" 2>/dev/null || mkdir -p "$REGISTRY_DIR"
if [[ ! -f "$REGISTRY_PATH" ]]; then
    cat > "$REGISTRY_PATH" <<'JSON'
{
  "_notice": "EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM",
  "_managed": "Entries are added by pve-tunnel-vm.sh. Ports must be unique cluster-wide.",
  "version": 1,
  "vms": []
}
JSON
    say "seeded empty registry at $REGISTRY_PATH"
else
    say "registry already present at $REGISTRY_PATH, left alone"
fi
chmod 0600 "$REGISTRY_PATH" 2>/dev/null || true

install -m 0644 "$here/pve-tunnel-relay.service" "$UNIT_PATH"

systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null
systemctl restart "$UNIT_NAME"

say "waiting for the relay to report ready..."
for _ in $(seq 1 20); do
    if [[ -S "$CONTROL_SOCKET" ]]; then
        say "relay is up"
        if command -v socat >/dev/null; then
            socat - "UNIX-CONNECT:$CONTROL_SOCKET" 2>/dev/null | head -c 400 || true
            echo
        else
            say "(install socat to query status: apt-get install -y socat)"
        fi
        say
        say "installed:  $INSTALLED_BIN"
        say "unit:       $UNIT_NAME"
        say "registry:   $REGISTRY_PATH"
        say "next:       ./pve-tunnel-vm.sh add <VMID>"
        exit 0
    fi
    sleep 0.5
done

say "relay did not create $CONTROL_SOCKET within 10s. Recent log:"
journalctl -u "$UNIT_NAME" -n 30 --no-pager || true
die "relay failed to start"
