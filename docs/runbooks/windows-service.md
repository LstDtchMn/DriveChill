# DriveChill Runbook - Windows Service

## Install Service
1. Open PowerShell as Administrator.
2. Run `.\scripts\install_windows_service.ps1`.

## Verify Running
1. Run `sc query DriveChill`.
2. Expect `STATE: RUNNING`.

## Open UI
1. Open `http://localhost:8085`.

## Stop/Start
1. Stop: `sc stop DriveChill`
2. Start: `sc start DriveChill`

## Verify BIOS Fallback
1. Set fan to 60% in UI.
2. Stop service.
3. Confirm fan returns to BIOS control.

## Uninstall Service
1. Run `.\scripts\uninstall_windows_service.ps1`.
