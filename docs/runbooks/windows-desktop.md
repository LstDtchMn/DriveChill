# DriveChill Runbook - Windows Desktop

## Install
1. Open PowerShell as Administrator.
2. Run `.\scripts\install_windows.ps1`.

## Auto-start
1. Run `python backend\drivechill.py --install-autostart`.
2. Verify task exists with `schtasks /Query /TN DriveChill`.

## Verify Running
1. Launch DriveChill (`python backend\drivechill.py` or packaged EXE).
2. Confirm tray icon appears.

## Open UI
1. Tray -> `Open Dashboard`.
2. Confirm `http://localhost:8085` loads.

## Verify BIOS Fallback
1. Set a fan to 60% in UI.
2. Tray -> `Release Fan Control` or quit app.
3. Verify RPM returns to BIOS-managed behavior.

## Uninstall
1. Run `python backend\drivechill.py --remove-autostart`.
2. Run `.\scripts\uninstall_windows.ps1`.
