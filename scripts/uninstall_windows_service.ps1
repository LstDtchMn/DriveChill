$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Windows Service Uninstall ===" -ForegroundColor Cyan

$nssm = Get-Command nssm -ErrorAction SilentlyContinue

if ($nssm) {
    # Installed via NSSM — use NSSM to stop and remove
    try {
        & nssm stop DriveChill 2>$null
    } catch {
        Write-Host "[WARN] Could not stop DriveChill (it may not be running)." -ForegroundColor Yellow
    }

    Start-Sleep -Seconds 1

    try {
        & nssm remove DriveChill confirm 2>$null
    } catch {
        Write-Host "[WARN] Could not remove DriveChill via NSSM (it may not exist)." -ForegroundColor Yellow
    }
} else {
    # Fallback: try sc.exe in case service was registered without NSSM
    try {
        sc.exe stop DriveChill 2>$null | Out-Null
    } catch {
        Write-Host "[WARN] Could not stop DriveChill (it may not be running)." -ForegroundColor Yellow
    }

    Start-Sleep -Seconds 1

    try {
        sc.exe delete DriveChill 2>$null | Out-Null
    } catch {
        Write-Host "[WARN] Could not delete DriveChill via sc.exe (it may not exist)." -ForegroundColor Yellow
    }
}

Write-Host "[OK] Service DriveChill removed (if it existed)." -ForegroundColor Green
