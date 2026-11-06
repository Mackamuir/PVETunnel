#!/usr/bin/env bash
# EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM
#
# Verifies the pveTunnel path end to end for one VM, on a Proxmox VE node.
#
#   ./smoke-test.sh <VMID>
#
# Checks each link in the chain separately, so a failure says which hop is broken rather
# than just "Ansible cannot connect":
#
#   1 relay service running
#   2 relay control socket answering
#   3 VM has the QEMU virtio-serial device
#   4 VM is running
#   5 relay has a live session for that VMID   <- guest bridge is running and connected
#   6 VM reports ready with a bound ingress port
#   7 TLS handshake through the tunnel          <- WinRM is actually reachable end to end
#
# Step 7 is the one that matters: it proves controller -> relay -> virtio-serial -> bridge ->
# WinRM. Everything before it is there to localise the failure when step 7 does not pass.

set -uo pipefail

CONTROL_SOCKET=${PVE_TUNNEL_CONTROL_SOCKET:-/run/pve-tunnel/control.sock}
UNIT_NAME=EXERCISE-OUT-OF-SCOPE-pve-tunnel-relay.service

vmid=${1:-}
[[ -n "$vmid" ]] || { echo "usage: $0 <VMID>" >&2; exit 2; }

pass=0; fail=0
ok()   { printf '  [ OK ] %s\n' "$*"; pass=$((pass+1)); }
bad()  { printf '  [FAIL] %s\n' "$*"; fail=$((fail+1)); }
info() { printf '         %s\n' "$*"; }

echo "pveTunnel smoke test for VM $vmid"
echo "EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM"
echo

# 1 -----------------------------------------------------------------------------------
if systemctl is-active --quiet "$UNIT_NAME"; then
    ok "relay service is active"
else
    bad "relay service is not active"
    info "systemctl status $UNIT_NAME"
    info "journalctl -u $UNIT_NAME -n 40 --no-pager"
    echo; echo "$pass passed, $fail failed"; exit 1
fi

# 2 -----------------------------------------------------------------------------------
status_json=""
if [[ -S "$CONTROL_SOCKET" ]]; then
    if command -v socat >/dev/null; then
        status_json=$(socat - "UNIX-CONNECT:$CONTROL_SOCKET" 2>/dev/null || true)
    fi
    if [[ -n "$status_json" ]]; then
        ok "control socket answered"
    else
        bad "control socket present but returned nothing (is socat installed? apt-get install -y socat)"
    fi
else
    bad "control socket missing at $CONTROL_SOCKET"
fi

# 3 -----------------------------------------------------------------------------------
if qm config "$vmid" 2>/dev/null | grep -q 'id=pvetunnel'; then
    ok "VM has the QEMU virtio-serial device"
else
    bad "VM has no pveTunnel device in its config"
    info "attach it with: ./pve-tunnel-vm.sh add $vmid   (VM must be stopped)"
fi

# 4 -----------------------------------------------------------------------------------
state=$(qm status "$vmid" 2>/dev/null | awk '{print $2}')
if [[ "$state" == "running" ]]; then
    ok "VM is running"
else
    bad "VM is ${state:-unknown}, expected running"
    info "the remaining checks need a booted guest"
fi

# 5, 6 --------------------------------------------------------------------------------
port=""
if [[ -n "$status_json" ]]; then
    read -r live ready port <<<"$(printf '%s' "$status_json" | python3 -c '
import json,sys
try: d=json.load(sys.stdin)
except Exception: print("no no"); raise SystemExit
vm=next((v for v in d.get("vms",[]) if str(v.get("vmid"))==sys.argv[1]), None)
if not vm: print("absent absent")
else: print(("yes" if vm.get("session_live") else "no"),
             ("yes" if vm.get("ready") else "no"),
             vm.get("ingress_port") or "")
' "$vmid")"

    case "$live" in
        yes)    ok "relay has a live session for VMID $vmid (guest bridge is connected)" ;;
        absent) bad "VMID $vmid is not in the relay registry"
                info "add it with: ./pve-tunnel-vm.sh add $vmid" ;;
        *)      bad "no live session for VMID $vmid"
                info "the guest bridge is not connected. Inside the guest, check:"
                info "  the scheduled task '\\EXERCISE - OUT OF SCOPE\\EXERCISE - pveTunnel bridge - OUT OF SCOPE' is running"
                info "  the device exists: Get-Item '\\\\.\\Global\\pve-tunnel.relay.0'"
                info "  the vioser driver is present: pnputil /enum-drivers | Select-String vioser" ;;
    esac

    if [[ "$ready" == "yes" && -n "$port" ]]; then
        ok "VM reports ready, ingress bound on port $port"
    else
        bad "VM does not report ready"
    fi
fi

# 7 -----------------------------------------------------------------------------------
if [[ -n "$port" ]]; then
    if command -v openssl >/dev/null; then
        if timeout 15 openssl s_client -connect "127.0.0.1:$port" -servername winrm </dev/null 2>/dev/null \
             | grep -qE 'BEGIN CERTIFICATE|Server certificate'; then
            ok "TLS handshake completed through the tunnel: WinRM is reachable end to end"
        else
            bad "no TLS handshake through the tunnel on port $port"
            info "the tunnel may be up while WinRM is not. Inside the guest, check:"
            info "  Test-NetConnection 127.0.0.1 -Port 5986"
            info "  winrm enumerate winrm/config/listener"
            info "relay log: journalctl -u $UNIT_NAME -n 40 --no-pager | grep ingress"
        fi
    else
        if timeout 5 bash -c "</dev/tcp/127.0.0.1/$port" 2>/dev/null; then
            ok "TCP connect to the ingress port succeeded (install openssl for a real WinRM check)"
        else
            bad "cannot connect to the ingress port $port"
        fi
    fi
fi

echo
echo "$pass passed, $fail failed"
if [[ $fail -eq 0 ]]; then
    echo
    echo "Ansible can reach this VM at 127.0.0.1:$port with:"
    echo "  ansible_connection: winrm"
    echo "  ansible_host: 127.0.0.1"
    echo "  ansible_port: $port"
    echo "  ansible_winrm_scheme: https"
    echo "  ansible_winrm_server_cert_validation: ignore"
fi
[[ $fail -eq 0 ]]
