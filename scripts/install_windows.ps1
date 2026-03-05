# DriveChill Windows Setup Script
# Run as Administrator

$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Setup ===" -ForegroundColor Cyan
Write-Host ""

# -----------------------------------------------------------------------
# smartmontools — optional but needed for drive SMART health data.
# Installed via winget if available; degrades gracefully if skipped.
# -----------------------------------------------------------------------
$smartctl = Get-Command smartctl -ErrorAction SilentlyContinue
if ($smartctl) {
    Write-Host "[OK] smartmontools already installed ($($smartctl.Source))" -ForegroundColor Green
} else {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "Installing smartmontools via winget..." -ForegroundColor Yellow
        winget install --id Smartmontools.Smartmontools -e --accept-source-agreements --accept-package-agreements
        # Refresh PATH so smartctl is findable in the same session
        $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") +
                    ";" +
                    [System.Environment]::GetEnvironmentVariable("PATH","User")
        if (Get-Command smartctl -ErrorAction SilentlyContinue) {
            Write-Host "[OK] smartmontools installed" -ForegroundColor Green
        } else {
            Write-Host "[WARN] smartmontools installed; restart your terminal if 'smartctl' is not on PATH" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[WARN] winget not found. Install smartmontools manually for drive health monitoring:" -ForegroundColor Yellow
        Write-Host "        https://www.smartmontools.org/wiki/Download" -ForegroundColor Gray
        Write-Host "       DriveChill will still run but the Drives page will show degraded mode." -ForegroundColor Gray
    }
}
Write-Host ""

# Check Python
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Host "[ERROR] Python is not installed. Install Python 3.11+ from python.org" -ForegroundColor Red
    exit 1
}

$pyVersion = python --version 2>&1
Write-Host "[OK] $pyVersion" -ForegroundColor Green

# Check Node.js
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Host "[ERROR] Node.js is not installed. Install from nodejs.org" -ForegroundColor Red
    exit 1
}

$nodeVersion = node --version 2>&1
Write-Host "[OK] Node.js $nodeVersion" -ForegroundColor Green
Write-Host ""

# Install backend dependencies
Write-Host "Installing backend dependencies..." -ForegroundColor Yellow
Set-Location "$PSScriptRoot\..\backend"
python -m pip install -r requirements.txt
Write-Host "[OK] Backend dependencies installed" -ForegroundColor Green

# Install frontend dependencies
Write-Host "Installing frontend dependencies..." -ForegroundColor Yellow
Set-Location "$PSScriptRoot\..\frontend"
npm install
Write-Host "[OK] Frontend dependencies installed" -ForegroundColor Green

# Build frontend
Write-Host "Building frontend..." -ForegroundColor Yellow
npm run build
Write-Host "[OK] Frontend built" -ForegroundColor Green

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "To start DriveChill:" -ForegroundColor White
Write-Host "  cd backend" -ForegroundColor Gray
Write-Host "  python drivechill.py" -ForegroundColor Gray
Write-Host ""
Write-Host "Then open http://localhost:8085 in your browser" -ForegroundColor White
Write-Host ""
Write-Host "For development (frontend + backend hot-reload):" -ForegroundColor White
Write-Host "  Terminal 1: cd backend && python -m uvicorn app.main:app --reload --port 8085" -ForegroundColor Gray
Write-Host "  Terminal 2: cd frontend && npm run dev" -ForegroundColor Gray
Write-Host ""
Write-Host "NOTE: Run as Administrator to enable hardware sensor access (fan control requires kernel driver)." -ForegroundColor Yellow
