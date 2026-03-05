#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Windows Service Install ===" -ForegroundColor Cyan

# Windows SCM requires a Win32 service binary that calls StartServiceCtrlDispatcher.
# Python/uvicorn is not one, so `sc.exe create` with `cmd /c` will not work.
# We use NSSM (https://nssm.cc) to wrap the process as a proper service.
# Install via: choco install nssm  OR  scoop install nssm

# smartmontools — check and install if missing (needed for drive health monitoring).
$smartctl = Get-Command smartctl -ErrorAction SilentlyContinue
if ($smartctl) {
    Write-Host "[OK] smartmontools found ($($smartctl.Source))" -ForegroundColor Green
} else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "Installing smartmontools via winget..." -ForegroundColor Yellow
        winget install --id Smartmontools.Smartmontools -e --accept-source-agreements --accept-package-agreements
        $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") +
                    ";" +
                    [System.Environment]::GetEnvironmentVariable("PATH","User")
        Write-Host "[OK] smartmontools installed" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Install smartmontools for drive health monitoring: https://www.smartmontools.org/wiki/Download" -ForegroundColor Yellow
    }
}

$nssm = Get-Command nssm -ErrorAction SilentlyContinue
if (-not $nssm) {
    Write-Host "[ERROR] NSSM is required to install DriveChill as a Windows service." -ForegroundColor Red
    Write-Host "        Install it with:  choco install nssm  OR  scoop install nssm" -ForegroundColor Yellow
    exit 1
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host "[ERROR] Python is required." -ForegroundColor Red
    exit 1
}

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$backendDir = Join-Path $repoRoot "backend"

# Install the service via NSSM
& nssm install DriveChill "$($python.Source)"
& nssm set DriveChill AppDirectory "$backendDir"
& nssm set DriveChill AppParameters "drivechill.py --headless"
& nssm set DriveChill DisplayName "DriveChill"
& nssm set DriveChill Description "DriveChill temperature-based fan control service"
& nssm set DriveChill Start SERVICE_AUTO_START
# Graceful shutdown: send Ctrl+C first, allow 10s for cleanup before escalating
& nssm set DriveChill AppStopMethodConsole 10000
& nssm set DriveChill AppStopMethodWindow 5000
& nssm set DriveChill AppStopMethodThreads 3000
& nssm start DriveChill

Write-Host "[OK] Service DriveChill installed and started via NSSM" -ForegroundColor Green
