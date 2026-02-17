# DriveChill Windows Setup Script
# Run as Administrator

$ErrorActionPreference = "Stop"

Write-Host "=== DriveChill Setup ===" -ForegroundColor Cyan
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
Write-Host "  python -m uvicorn app.main:app --host 0.0.0.0 --port 8085" -ForegroundColor Gray
Write-Host ""
Write-Host "Then open http://localhost:8085 in your browser" -ForegroundColor White
Write-Host ""
Write-Host "For development (frontend hot-reload):" -ForegroundColor White
Write-Host "  Terminal 1: cd backend && python -m uvicorn app.main:app --reload --port 8085" -ForegroundColor Gray
Write-Host "  Terminal 2: cd frontend && npm run dev" -ForegroundColor Gray
Write-Host ""
Write-Host "IMPORTANT: Ensure LibreHardwareMonitor is running with Web Server enabled" -ForegroundColor Yellow
Write-Host "  (Settings > Web Server > Run on port 8086)" -ForegroundColor Yellow
