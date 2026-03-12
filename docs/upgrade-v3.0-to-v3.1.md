# Upgrading from v3.0.0 to v3.1.0

## Pre-Upgrade Checklist

- [ ] Back up your `data/` directory (contains SQLite database)
- [ ] Note your current `settings.json` location (C# backend only)
- [ ] Download the v3.1.0 release artifact for your platform

## Database Migrations

v3.1.0 adds one new migration that runs automatically on first startup:

| Backend | Migration | Changes |
|---------|-----------|---------|
| Python  | `021_scheduler_observability.sql` | Adds `last_error`, `last_attempted_at`, `consecutive_failures` columns to `report_schedules` |
| C#      | `002_scheduler_observability.sql` | Same columns via ALTER TABLE |

**No manual SQL is required.** Migrations run automatically at startup. If you are running both backends against the same database, either backend can apply the migration — the other will detect it as already applied.

## Breaking Changes

### API Response Format: `GET /api/fans`

The C# backend previously returned a bare JSON array from `GET /api/fans`:

```json
// v3.0 (C# only — Python was already correct)
[{ "id": "fan1", "speed": 50 }, ...]
```

v3.1.0 wraps the response in an object to match the Python backend:

```json
// v3.1.0 (both backends)
{ "fans": [{ "id": "fan1", "speed": 50 }, ...] }
```

**Impact:** If you have custom scripts or integrations that call `GET /api/fans` against the C# backend and parse the response as a top-level array, update them to read `response.fans` instead.

### Timezone Validation on Schedules

Profile schedules and report schedules now **reject invalid timezone strings** at save time. Previously, an invalid timezone like `"Fake/Zone"` was silently accepted and fell back to UTC at runtime.

**Impact:** If you have saved schedules with non-IANA timezone strings, they will continue to work (existing data is not re-validated), but any future PUT/POST with an invalid timezone will return 422 Unprocessable Entity.

## New Environment Variables

No new required environment variables. The following existing variables are unchanged:

| Variable | Default | Notes |
|----------|---------|-------|
| `DRIVECHILL_ALLOW_PRIVATE_BROKER_TARGETS` | `false` | New in v3.1.0 — set to `true` if your MQTT broker is on a private/LAN IP |

## Configuration Changes

### Settings Page Redesign

The Settings page has been reorganised into 5 horizontal tabs:
- **General** — polling, temp unit, ramp rate, retention, sensor labels, system info
- **Notifications** — webhooks, email, push, notification channels
- **Automation** — virtual sensors, profile schedules, report schedules, noise profiler
- **Security** — API keys, password, user management
- **Infrastructure** — config export/import, update check

No configuration migration is needed — all settings are preserved. The layout change is purely visual.

### MailKit SMTP Migration (C# Backend)

The C# backend now uses MailKit instead of the deprecated `System.Net.Mail.SmtpClient`. The behavior of the `use_ssl` and `use_tls` email settings has changed:

| Setting | v3.0 Behavior | v3.1 Behavior |
|---------|---------------|---------------|
| `use_ssl: true` | Ambiguous SSL negotiation | Implicit TLS (`SslOnConnect`, port 465) |
| `use_tls: true` | Ambiguous TLS negotiation | STARTTLS (`StartTls`, port 587) |

**Impact:** If your SMTP server uses port 587 with STARTTLS, ensure `use_tls: true` (not `use_ssl`). If it uses port 465 with implicit TLS, use `use_ssl: true`.

## Upgrade Steps

### Python Backend

```bash
# 1. Stop the service
# (NSSM) nssm stop DriveChill
# (Manual) Ctrl+C the running process

# 2. Back up data directory
cp -r data/ data-backup-v3.0/

# 3. Update files (automated)
.\scripts\update_windows.ps1 -Version 3.1.0

# Or manually: extract release ZIP, preserving data/
# Then: pip install -r requirements.txt

# 4. Start the service
# (NSSM) nssm start DriveChill
# (Manual) python drivechill.py --headless
```

### C# Backend

```powershell
# 1. Stop the service or tray application

# 2. Back up data directory
Copy-Item -Recurse data\ data-backup-v3.0\

# 3. Update (automated)
.\scripts\update_windows.ps1 -Artifact windows -Version 3.1.0

# Or manually: extract release ZIP over install directory, preserving data/

# 4. Start the application
```

### Docker

```bash
# 1. Pull new image
docker compose pull

# 2. Recreate container (data persists via volume)
docker compose up -d

# Or pin the version:
# image: ghcr.io/lstdtchmn/drivechill:3.1.0
```

## Rollback Procedure

If something goes wrong after upgrading:

1. **Stop** the service or process
2. **Restore** the previous release files (extract old ZIP over install directory)
3. **Restore** the database backup: `cp data-backup-v3.0/drivechill.db data/drivechill.db`
4. For Python: `pip install -r requirements.txt` to restore old dependencies
5. **Restart** the service

**Important:** The `021_scheduler_observability` migration adds columns to `report_schedules`. Rolling back to v3.0 after this migration has run is safe — SQLite ignores extra columns in SELECT statements, and the v3.0 code does not reference the new columns. However, if you need a clean rollback, restore the database backup from step 3.

## Verification

After upgrading, verify the installation:

1. Open the dashboard — the version in **Settings > General > System Info** should read **3.1.0**
2. Check that all fan curves are still active and temperatures are being read
3. If using report or profile schedules with non-UTC timezones, verify they fire at the expected local time
4. If using the C# backend with custom API integrations, verify they handle the new `{ fans: [...] }` response format

## Test Results

v3.1.0 ships with:
- **697 Python tests** (13 skipped)
- **400 C# tests**
- **8 Playwright E2E specs** covering all pages
