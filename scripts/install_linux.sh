#!/usr/bin/env bash
# DriveChill Linux Setup Script

set -euo pipefail

echo "=== DriveChill Setup ==="
echo ""

# Check Python
if ! command -v python3 &>/dev/null; then
    echo "[ERROR] Python 3 is not installed. Install with: sudo apt install python3 python3-pip"
    exit 1
fi
echo "[OK] $(python3 --version)"

# Check Node.js
if ! command -v node &>/dev/null; then
    echo "[ERROR] Node.js is not installed. Install from https://nodejs.org"
    exit 1
fi
echo "[OK] Node.js $(node --version)"

# Check lm-sensors
if ! command -v sensors &>/dev/null; then
    echo "[WARN] lm-sensors is not installed. Install with: sudo apt install lm-sensors"
    echo "       Run 'sudo sensors-detect' after installation"
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

echo ""

# Install backend dependencies
echo "Installing backend dependencies..."
cd "$PROJECT_DIR/backend"
python3 -m pip install -r requirements.txt
echo "[OK] Backend dependencies installed"

# Install frontend dependencies
echo "Installing frontend dependencies..."
cd "$PROJECT_DIR/frontend"
npm install
echo "[OK] Frontend dependencies installed"

# Build frontend
echo "Building frontend..."
npm run build
echo "[OK] Frontend built"

echo ""
echo "=== Setup Complete ==="
echo ""
echo "To start DriveChill:"
echo "  cd $PROJECT_DIR/backend"
echo "  python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8085"
echo ""
echo "Then open http://localhost:8085 in your browser"
echo ""
echo "For development (frontend hot-reload):"
echo "  Terminal 1: cd backend && python3 -m uvicorn app.main:app --reload --port 8085"
echo "  Terminal 2: cd frontend && npm run dev"
