$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Windows Service Install ===" -ForegroundColor Cyan

# Windows SCM requires a Win32 service binary that calls StartServiceCtrlDispatcher.
# Python/uvicorn is not one, so `sc.exe create` with `cmd /c` will not work.
# We use NSSM (https://nssm.cc) to wrap the process as a proper service.
# Install via: choco install nssm  OR  scoop install nssm

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
& nssm set DriveChill AppStopMethodSkip 6
& nssm set DriveChill AppStopMethodConsole 5000
& nssm start DriveChill

Write-Host "[OK] Service DriveChill installed and started via NSSM" -ForegroundColor Green
