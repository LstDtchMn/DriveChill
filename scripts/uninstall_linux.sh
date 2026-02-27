#!/usr/bin/env bash
set -euo pipefail

echo "=== DriveChill Linux Uninstall ==="

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

if command -v python3 >/dev/null 2>&1; then
  if ! python3 "$PROJECT_DIR/backend/drivechill.py" --remove-autostart; then
    echo "[WARN] Could not remove autostart via drivechill.py"
  fi
fi

echo "Removed DriveChill autostart service (if present)."
