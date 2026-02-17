# DriveChill — Implementation Plan

## Architecture Overview

```
┌─────────────────────────────────────────────────┐
│                  Browser / UI                    │
│            React + Next.js + Tailwind            │
│         (Charts, Gauges, Curve Editor)           │
└────────────────┬────────────────────┬────────────┘
                 │ REST API           │ WebSocket
                 │ (config/CRUD)      │ (real-time data)
┌────────────────┴────────────────────┴────────────┐
│              Python Backend (FastAPI)             │
│  ┌──────────┐ ┌───────────┐ ┌──────────────────┐ │
│  │ Sensor   │ │ Fan Curve │ │ Alert & Logging  │ │
│  │ Service  │ │ Engine    │ │ Service          │ │
│  └────┬─────┘ └───────────┘ └──────────────────┘ │
│       │                                           │
│  ┌────┴──────────────────────────────────────┐   │
│  │         Hardware Abstraction Layer         │   │
│  │  ┌─────────────┐  ┌────────────────────┐  │   │
│  │  │ LHM Backend │  │ lm-sensors Backend │  │   │
│  │  │ (Windows)   │  │ (Linux/Docker)     │  │   │
│  │  └─────────────┘  └────────────────────┘  │   │
│  │  ┌─────────────────────────────────────┐  │   │
│  │  │ USB Controllers (Corsair, NZXT...) │  │   │
│  │  └─────────────────────────────────────┘  │   │
│  └───────────────────────────────────────────┘   │
└──────────────────────────────────────────────────┘
```

---

## Phase 1: Project Scaffolding & Core Backend

### 1.1 — Project structure & config files
- `.gitignore`, `README.md`, `LICENSE` (MIT)
- `backend/pyproject.toml` with dependencies
- `backend/requirements.txt`
- `frontend/package.json`, `next.config.js`, `tailwind.config.js`, `tsconfig.json`

### 1.2 — Backend: FastAPI app skeleton
- `backend/app/main.py` — FastAPI app with CORS, lifespan, static mount
- `backend/app/config.py` — Settings via Pydantic (port, polling interval, data dir)
- `backend/app/models/` — Pydantic models for sensors, fans, profiles, alerts

### 1.3 — Hardware abstraction layer
- `backend/app/hardware/base.py` — Abstract `HardwareBackend` class
- `backend/app/hardware/lhm_backend.py` — LibreHardwareMonitor integration (Windows)
- `backend/app/hardware/lm_sensors.py` — lm-sensors + psutil integration (Linux)
- `backend/app/hardware/mock_backend.py` — Mock data for development/testing
- Platform auto-detection to select the right backend

### 1.4 — Sensor & Fan services
- `backend/app/services/sensor_service.py` — Polls hardware, caches readings
- `backend/app/services/fan_service.py` — Sets fan speeds via hardware backend
- `backend/app/services/curve_engine.py` — Evaluates fan curves (linear interpolation between user-defined points)

### 1.5 — REST API routes
- `GET /api/sensors` — Current sensor readings (temps, fans, loads)
- `GET/PUT /api/profiles` — Fan profiles CRUD
- `GET/PUT /api/profiles/{id}/curves` — Fan curve points
- `GET/PUT /api/settings` — App settings
- `GET /api/history` — Historical data (last N hours)
- `POST /api/alerts` — Alert config

### 1.6 — WebSocket endpoint
- `WS /api/ws` — Streams sensor data at configurable interval (default 1s)

---

## Phase 2: Frontend Dashboard

### 2.1 — Next.js app shell
- App layout with sidebar navigation
- Dark/light theme toggle (CSS variables + Tailwind)
- Responsive design

### 2.2 — Dashboard page
- **System overview cards** — CPU, GPU, HDD, Case temps at a glance
- **Temperature gauges** — Radial gauges with color zones (green/yellow/red)
- **Fan speed indicators** — RPM display with percentage bars
- **Live temperature chart** — Time-series line chart (last 5–60 minutes)

### 2.3 — Component library
- `TempGauge` — Radial gauge component (SVG-based)
- `FanSpeedCard` — Fan info card with RPM + percentage
- `TempChart` — Recharts/Chart.js time-series graph
- `SystemOverview` — Grid of sensor cards
- `Sidebar` / `Header` / `ThemeToggle`

### 2.4 — Real-time data hooks
- `useWebSocket` — WebSocket connection with auto-reconnect
- `useSensors` — Sensor state management from WS data
- `useFanCurves` — Fan curve state + API calls

---

## Phase 3: Fan Curve Editor

### 3.1 — Curve editor component
- Interactive SVG/Canvas graph
- Draggable control points on temperature→speed curve
- X-axis: Temperature (°C), Y-axis: Fan Speed (%)
- Snap-to-grid option
- Per-fan or per-zone curves

### 3.2 — Preset profiles
- **Silent** — Fans low until high temps
- **Balanced** — Moderate ramp-up
- **Performance** — Aggressive cooling
- **Full Speed** — 100% always
- One-click apply with visual preview

### 3.3 — Profile management
- Save/load custom profiles
- Import/export as JSON
- Assign profiles per sensor zone

---

## Phase 4: Alerts, Logging & Settings

### 4.1 — Alert system
- Temperature threshold alerts (configurable per sensor)
- Browser notifications + optional sound
- Alert history log

### 4.2 — Data logging
- SQLite database for historical data
- Configurable retention (1 day – 1 year)
- Export to CSV

### 4.3 — Settings page
- Polling interval
- Temperature units (°C/°F)
- Theme selection
- Startup behavior
- Hardware backend config

---

## Phase 5: Docker & Windows Service

### 5.1 — Docker support
- `Dockerfile` (Python + Node multi-stage build)
- `docker-compose.yml` with volume mounts
- Linux sensor access via privileged mode

### 5.2 — Windows service / tray
- System tray icon (pystray)
- Auto-start on login
- Background service mode
- PowerShell install script

---

## Phase 6: Polish & Documentation

### 6.1 — README with screenshots, setup instructions
### 6.2 — Error handling & edge cases
### 6.3 — Loading states, animations, transitions

---

## Tech Stack Summary

| Component | Technology |
|-----------|-----------|
| Frontend | Next.js 14, React 18, TypeScript, Tailwind CSS |
| Charts | Recharts |
| Curve Editor | Custom SVG with drag interactions |
| Backend | Python 3.11+, FastAPI, Uvicorn |
| WebSocket | FastAPI WebSocket |
| Database | SQLite (via aiosqlite) |
| HW (Windows) | LibreHardwareMonitor CLI/API |
| HW (Linux) | lm-sensors, psutil, pySMART |
| Docker | Multi-stage build, docker-compose |
| Windows Tray | pystray |
