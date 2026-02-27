# DriveChill

**PC Fan Controller** — Temperature-based fan speed management with a modern web dashboard.

DriveChill monitors CPU, GPU, hard drive, and case temperatures and automatically adjusts fan speeds using customizable fan curves. It runs as a lightweight Python backend with a polished React web interface.

## Features

- **Real-time Dashboard** — Live temperature gauges, fan speed indicators, and time-series charts
- **Custom Fan Curves** — Drag-and-drop curve editor with per-sensor/per-fan control
- **Preset Profiles** — Silent, Balanced, Performance, and Full Speed one-click presets
- **Alerts & Logging** — Temperature threshold alerts with browser notifications and SQLite history
- **Dark/Light Theme** — Clean modern UI with smooth theme switching
- **Multi-Platform** — Windows (LibreHardwareMonitor), Linux (lm-sensors), and Docker
- **Remote Access** — Mobile-responsive dashboard, multi-machine hub monitoring, webhook integrations
- **Security** — Session auth, API keys, CSRF protection, HTTPS support (self-signed or custom cert)
- **USB Controller Support** — Architecture supports Corsair, NZXT, and other USB fan controllers

## Quick Start

### Windows

1. Install [Python 3.11+](https://python.org) and [Node.js 20+](https://nodejs.org)
2. Install and run [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) with Web Server enabled (Settings → Web Server → Run)
3. Run the setup script:
   ```powershell
   .\scripts\install_windows.ps1
   ```
4. Start the backend:
   ```powershell
   cd backend
   python -m uvicorn app.main:app --host 0.0.0.0 --port 8085
   ```
5. Open http://localhost:8085

   HTTPS note: `DRIVECHILL_SSL_CERTFILE`, `DRIVECHILL_SSL_KEYFILE`, and
   `DRIVECHILL_SSL_GENERATE_SELF_SIGNED=true` are applied by
   `python backend\drivechill.py` (packaged entry point). If you launch via
   `python -m uvicorn ...`, pass Uvicorn `--ssl-certfile/--ssl-keyfile` options.

### Linux

1. Install prerequisites:
   ```bash
   sudo apt install python3 python3-pip nodejs npm lm-sensors
   sudo sensors-detect
   ```
2. Run the setup script:
   ```bash
   chmod +x scripts/install_linux.sh
   ./scripts/install_linux.sh
   ```
3. Start the backend:
   ```bash
   cd backend
   python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8085
   ```
4. Open http://localhost:8085

   HTTPS note: TLS env settings are handled by `backend/drivechill.py`. With
   direct `uvicorn` startup, pass `--ssl-certfile/--ssl-keyfile` explicitly.

### Docker

```bash
cd docker
docker-compose up -d
```

If minimal permissions cannot access sensors on your host:

```bash
cd docker
docker compose -f docker-compose.privileged.yml up -d
```

## Development

Run the backend and frontend separately for hot-reload:

```bash
# Terminal 1: Backend (with auto-reload)
cd backend
DRIVECHILL_HARDWARE_BACKEND=mock python -m uvicorn app.main:app --reload --port 8085

# Terminal 2: Frontend (Next.js dev server)
cd frontend
npm run dev
```

The mock backend generates realistic fake sensor data for development without needing real hardware.

## Architecture

```
Browser (React/Next.js) ←→ FastAPI (Python)
                              ↕
                    Hardware Abstraction Layer
                   ┌──────────┬──────────────┐
                   │ LHM      │ lm-sensors   │
                   │ (Windows) │ (Linux)      │
                   └──────────┴──────────────┘
```

| Layer     | Technology                              |
|-----------|----------------------------------------|
| Frontend  | Next.js 14, React 18, TypeScript, Tailwind CSS |
| Charts    | Recharts                               |
| Backend   | Python 3.11+, FastAPI, Uvicorn         |
| Real-time | WebSocket                              |
| Database  | SQLite (aiosqlite)                     |
| Windows   | LibreHardwareMonitor HTTP API           |
| Linux     | lm-sensors, psutil                     |

## Configuration

Environment variables (prefix `DRIVECHILL_`):

| Variable | Default | Description |
|----------|---------|-------------|
| `HARDWARE_BACKEND` | `auto` | `auto`, `lhm`, `lm_sensors`, or `mock` |
| `PORT` | `8085` | HTTP server port |
| `SENSOR_POLL_INTERVAL` | `1.0` | Seconds between sensor reads |
| `HISTORY_RETENTION_HOURS` | `24` | How long to keep logged data |
| `TEMP_UNIT` | `C` | `C` or `F` |
| `PASSWORD` | _(none)_ | Admin password (required for non-localhost binding) |
| `SSL_CERTFILE` | _(none)_ | Path to PEM TLS certificate |
| `SSL_KEYFILE` | _(none)_ | Path to PEM TLS private key |
| `SSL_GENERATE_SELF_SIGNED` | `false` | Auto-generate a self-signed certificate |

## License

MIT

## Runbooks

- Windows desktop: `docs/runbooks/windows-desktop.md`
- Windows service: `docs/runbooks/windows-service.md`
- Linux systemd: `docs/runbooks/linux-systemd.md`
- Security credential rotation: `docs/runbooks/security-credential-rotation.md`
