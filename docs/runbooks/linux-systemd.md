# DriveChill Runbook - Linux systemd

## Install
1. Run `chmod +x scripts/install_linux.sh`.
2. Run `./scripts/install_linux.sh`.

## Auto-start
1. Run `python3 backend/drivechill.py --install-autostart`.
2. Run `systemctl --user status drivechill`.

## Verify Running
1. Confirm service is `active (running)`.
2. Open `http://localhost:8085`.

## Verify BIOS Fallback
1. Set fan to 60% in UI.
2. Run `systemctl --user stop drivechill`.
3. Verify RPM returns to BIOS-managed value.

## Uninstall
1. Run `python3 backend/drivechill.py --remove-autostart`.
2. Run `./scripts/uninstall_linux.sh`.
