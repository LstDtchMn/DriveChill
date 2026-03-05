# Updating DriveChill

## Windows — Python Backend

**Service install (NSSM):**

```powershell
# As Administrator — detects install dir from NSSM service automatically
.\scripts\update_windows.ps1

# Or specify version and/or install dir explicitly
.\scripts\update_windows.ps1 -Version 2.1.1 -InstallDir "C:\DriveChill"
```

The script will:
1. Fetch the latest release from GitHub (or use `-Version`)
2. Download `DriveChill-python-<version>.zip`
3. Stop the NSSM service (if running)
4. Extract files, preserving the `data/` directory (DB, certs, config)
5. Run `pip install -r requirements.txt`
6. Restart the service

**Standalone (no service):**

Same command — the script falls back to its own parent directory when no NSSM
service is found. Stop the running process manually before updating.

## Windows — C# Native App

**Service install:**

```powershell
.\scripts\update_windows.ps1 -Artifact windows
```

**From the dashboard:**

Settings → Check for Updates → Apply. The backend calls `update_windows.ps1`
with `-Artifact windows` automatically.

**Manual:**

Download `DriveChill-windows-<version>.zip` from the GitHub Releases page,
stop the service, extract over the existing directory (preserve `data/`),
and restart.

## Docker

Docker containers update via image pull:

```bash
docker compose pull && docker compose up -d
```

Or pin a specific version in `docker-compose.yml`:

```yaml
image: ghcr.io/lstdtchmn/drivechill:2.1.1
```

The `POST /api/update/apply` endpoint returns a manual command for Docker
deployments — it does not attempt automated updates inside the container.

## Rollback

Keep the previous release ZIP before updating. To roll back:

1. Stop the service or process
2. Extract the old ZIP over the install directory (preserving `data/`)
3. For Python: run `pip install -r requirements.txt` to restore old dependencies
4. Restart

The `data/` directory contains the SQLite database and is preserved across
updates. Database migrations are forward-only — rolling back to a version
before a schema migration may cause errors if new tables/columns are referenced.

## Version Sources

All version strings are kept in sync by `scripts/bump_version.py`:

| File | Field |
|------|-------|
| `frontend/package.json` | `version` |
| `backend/app/config.py` | `app_version` |
| `backend-cs/AppSettings.cs` | `AppVersion` |
| `backend-cs/DriveChill.csproj` | `<Version>` + `<AssemblyVersion>` |
