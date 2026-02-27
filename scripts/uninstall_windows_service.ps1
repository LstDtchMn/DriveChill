$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Windows Service Uninstall ===" -ForegroundColor Cyan

try {
    sc.exe stop DriveChill | Out-Null
} catch {
    Write-Host "[WARN] Could not stop DriveChill (it may not be running)." -ForegroundColor Yellow
}

Start-Sleep -Seconds 1

try {
    sc.exe delete DriveChill | Out-Null
} catch {
    Write-Host "[WARN] Could not delete DriveChill (it may not exist)." -ForegroundColor Yellow
}

Write-Host "[OK] Service DriveChill removed (if it existed)." -ForegroundColor Green
