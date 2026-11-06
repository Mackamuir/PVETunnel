#!/usr/bin/env bash
# EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM
#
# Attaches or detaches the pveTunnel channel for one VM on a Proxmox VE node.
#
#   ./pve-tunnel-vm.sh add    <VMID> [--port N] [--egress HOST:PORT]
#   ./pve-tunnel-vm.sh remove <VMID>
#   ./pve-tunnel-vm.sh port   <VMID>      # prints the allocated port, for Ansible inventory
#   ./pve-tunnel-vm.sh status
#
# "add" allocates a management port, writes the registry entry, and applies the QEMU
# virtio-serial device. The VM must be stopped: QEMU reads args at start, so a running VM
# would take the device only at its next boot and the registry would disagree with reality.
#
# PORT ALLOCATION IS TEST-SCALE. It takes the lowest free port in the range and records it in
# the shared registry, which is enough for a single node or a small cluster driven from one
# place. It is NOT the cluster-safe allocator the integration document calls a design gate:
# there is no locking, so two operators running this simultaneously on different nodes could
# race. Run it from one place until that decision is made.

set -euo pipefail

REGISTRY_PATH=${PVE_TUNNEL_REGISTRY:-/etc/pve/pve-tunnel/vms.json}
CONTROL_SOCKET=${PVE_TUNNEL_CONTROL_SOCKET:-/run/pve-tunnel/control.sock}
RELAY_SOCKET=${PVE_TUNNEL_SOCKET:-/run/pve-tunnel/relay.sock}
PORT_RANGE_FIRST=${PVE_TUNNEL_PORT_FIRST:-24000}
PORT_RANGE_LAST=${PVE_TUNNEL_PORT_LAST:-24999}
DEFAULT_EGRESS=${PVE_TUNNEL_DEFAULT_EGRESS:-127.0.0.1:5000}
CHARDEV_ID=pvetunnel
PORT_NAME=pve-tunnel.relay.0

QEMU_ARGS="-device virtio-serial-pci -chardev socket,id=${CHARDEV_ID},path=${RELAY_SOCKET},server=off,reconnect=5 -device virtserialport,chardev=${CHARDEV_ID},name=${PORT_NAME}"

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
say() { printf '%s\n' "$*"; }

[[ $EUID -eq 0 ]] || die "must run as root"
command -v python3 >/dev/null || die "python3 is required"
command -v qm >/dev/null || die "qm not found; this must run on a Proxmox VE node"
[[ -f "$REGISTRY_PATH" ]] || die "registry not found at $REGISTRY_PATH; run install-relay-node.sh first"

cmd=${1:-}; shift || true

registry_py() { python3 - "$REGISTRY_PATH" "$@"; }

case "$cmd" in
add)
    vmid=${1:-}; shift || true
    [[ -n "$vmid" ]] || die "usage: $0 add <VMID> [--port N] [--egress HOST:PORT]"
    [[ "$vmid" =~ ^[0-9]{1,10}$ ]] || die "VMID must be numeric"

    want_port=""
    egress="$DEFAULT_EGRESS"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --port)   want_port=${2:-}; shift 2 ;;
            --egress) egress=${2:-};    shift 2 ;;
            *) die "unknown option: $1" ;;
        esac
    done

    qm config "$vmid" >/dev/null 2>&1 || die "VM $vmid not found on this node"

    state=$(qm status "$vmid" | awk '{print $2}')
    if [[ "$state" != "stopped" ]]; then
        die "VM $vmid is $state. Stop it first: QEMU reads args at start, so attaching now would leave the registry claiming a channel the VM does not have until its next boot."
    fi

    port=$(registry_py add "$vmid" "$want_port" "$egress" "$PORT_RANGE_FIRST" "$PORT_RANGE_LAST" <<'PY'
import json, sys
path, action, vmid, want, egress, first, last = sys.argv[1:8]
first, last = int(first), int(last)
reg = json.load(open(path))
vms = reg.setdefault("vms", [])

existing = next((v for v in vms if str(v.get("vmid")) == vmid), None)
if existing:
    print(existing["managementPort"]); sys.exit(0)

used = {int(v["managementPort"]) for v in vms if "managementPort" in v}
if want:
    port = int(want)
    if port in used:
        owner = next(v["vmid"] for v in vms if int(v["managementPort"]) == port)
        sys.exit(f"port {port} is already allocated to VMID {owner}")
    if not (first <= port <= last):
        sys.exit(f"port {port} is outside the range {first}-{last}")
else:
    port = next((p for p in range(first, last + 1) if p not in used), None)
    if port is None:
        sys.exit(f"no free port in range {first}-{last}")

vms.append({
    "vmid": vmid,
    "managementPort": port,
    "ingress": True,
    "egress": True,
    "egressTarget": egress,
    "ingressTarget": "127.0.0.1:5986",
})
vms.sort(key=lambda v: int(v["vmid"]))
tmp = path + ".tmp"
with open(tmp, "w") as f:
    json.dump(reg, f, indent=2)
    f.write("\n")
import os; os.replace(tmp, path)
print(port)
PY
    ) || die "registry update failed"

    say "VM $vmid: management port $port, egress $egress"

    current_args=$(qm config "$vmid" | sed -n 's/^args: //p' || true)
    if [[ "$current_args" == *"id=${CHARDEV_ID}"* ]]; then
        say "VM $vmid: QEMU channel already attached, left alone"
    elif [[ -n "$current_args" ]]; then
        die "VM $vmid already has unrelated 'args:' set and merging is not safe to automate. Current value:
  $current_args
Append this by hand if it is compatible:
  $QEMU_ARGS"
    else
        qm set "$vmid" -args "$QEMU_ARGS" >/dev/null
        say "VM $vmid: QEMU virtio-serial channel attached"
    fi

    say
    say "Done. Start the VM, then check it is ready:"
    say "  ./smoke-test.sh $vmid"
    say
    say "Ansible reaches this VM at 127.0.0.1:$port on this node (WinRM HTTPS)."
    ;;

remove)
    vmid=${1:-}
    [[ -n "$vmid" ]] || die "usage: $0 remove <VMID>"

    registry_py remove "$vmid" <<'PY'
import json, os, sys
path, action, vmid = sys.argv[1:4]
reg = json.load(open(path))
before = len(reg.get("vms", []))
reg["vms"] = [v for v in reg.get("vms", []) if str(v.get("vmid")) != vmid]
tmp = path + ".tmp"
with open(tmp, "w") as f:
    json.dump(reg, f, indent=2); f.write("\n")
os.replace(tmp, path)
print(f"removed {before - len(reg['vms'])} registry entry/entries")
PY

    if qm config "$vmid" >/dev/null 2>&1; then
        current_args=$(qm config "$vmid" | sed -n 's/^args: //p' || true)
        if [[ "$current_args" == "$QEMU_ARGS" ]]; then
            qm set "$vmid" -delete args >/dev/null
            say "VM $vmid: QEMU channel detached"
        elif [[ -n "$current_args" ]]; then
            say "VM $vmid: 'args:' differs from what this script sets; leaving it alone:"
            say "  $current_args"
        fi
    fi
    ;;

port)
    vmid=${1:-}
    [[ -n "$vmid" ]] || die "usage: $0 port <VMID>"
    registry_py port "$vmid" <<'PY'
import json, sys
path, action, vmid = sys.argv[1:4]
reg = json.load(open(path))
hit = next((v for v in reg.get("vms", []) if str(v.get("vmid")) == vmid), None)
if not hit: sys.exit(f"VMID {vmid} is not in the registry")
print(hit["managementPort"])
PY
    ;;

status)
    say "registry: $REGISTRY_PATH"
    registry_py status <<'PY'
import json, sys
reg = json.load(open(sys.argv[1]))
vms = reg.get("vms", [])
if not vms:
    print("  (no VMs registered)")
else:
    print(f"  {'VMID':<8}{'PORT':<8}{'INGRESS':<9}{'EGRESS TARGET'}")
    for v in sorted(vms, key=lambda x: int(x['vmid'])):
        print(f"  {v['vmid']:<8}{v['managementPort']:<8}{str(v.get('ingress', False)):<9}{v.get('egressTarget','')}")
PY
    say
    if [[ -S "$CONTROL_SOCKET" ]] && command -v socat >/dev/null; then
        say "relay status:"
        socat - "UNIX-CONNECT:$CONTROL_SOCKET" 2>/dev/null | python3 -m json.tool 2>/dev/null || say "  (could not read control socket)"
    else
        say "relay control socket not available at $CONTROL_SOCKET (is the service running? is socat installed?)"
    fi
    ;;

*)
    die "usage: $0 {add|remove|port|status} [VMID]"
    ;;
esac
