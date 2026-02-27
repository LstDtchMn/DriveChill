$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Windows Uninstall ===" -ForegroundColor Cyan

# Best-effort: remove autostart task if present.
try {
    schtasks /Delete /TN DriveChill /F | Out-Null
    Write-Host "[OK] Removed scheduled task DriveChill" -ForegroundColor Green
} catch {
    Write-Host "[WARN] Scheduled task DriveChill not found" -ForegroundColor Yellow
}

Write-Host "Uninstall complete. Remove the project folder manually if desired." -ForegroundColor White
