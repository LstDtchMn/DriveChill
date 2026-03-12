#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates DriveChill to the latest (or a specified) release from GitHub.

.PARAMETER Version
    Target version to install, e.g. "2.2.0". Defaults to the latest GitHub release.

.PARAMETER InstallDir
    Path to the existing DriveChill installation root.
    Resolution order: explicit param → NSSM service directory → script-relative fallback.

.PARAMETER Artifact
    Which release artifact to download: 'python' or 'windows'.
    Defaults to 'python'. Use 'windows' for C# native installs.

.EXAMPLE
    .\update_windows.ps1
    .\update_windows.ps1 -Version 2.2.0
    .\update_windows.ps1 -InstallDir "C:\DriveChill"
    .\update_windows.ps1 -Artifact windows -InstallDir "C:\DriveChill"
#>
param(
    [string]$Version,
    [string]$InstallDir,
    [ValidateSet('python', 'windows')]
    [string]$Artifact = 'python'
)

$ErrorActionPreference = "Stop"
$GITHUB_REPO   = "LstDtchMn/DriveChill"
$SERVICE_NAME  = "DriveChill"

Write-Host "=== DriveChill Updater ===" -ForegroundColor Cyan
Write-Host ""

# ── 1. Resolve install directory ────────────────────────────────────────────
if (-not $InstallDir) {
    # Try NSSM service directory first
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if ($nssm) {
        $backendDir = & nssm get $SERVICE_NAME AppDirectory 2>$null
        if ($backendDir -and (Test-Path $backendDir)) {
            $InstallDir = Resolve-Path (Join-Path $backendDir "..")
        }
    }
    # Fall back to script-relative parent (standalone installs)
    if (-not $InstallDir) {
        $InstallDir = Resolve-Path (Join-Path $PSScriptRoot "..")
    }
}

Write-Host "[OK] Install directory: $InstallDir" -ForegroundColor Green

# ── 2. Resolve target version ────────────────────────────────────────────────
if (-not $Version) {
    Write-Host "Fetching latest release from GitHub..." -ForegroundColor Yellow
    try {
        $headers = @{ "User-Agent" = "DriveChill-Updater" }
        $release = Invoke-RestMethod "https://api.github.com/repos/$GITHUB_REPO/releases/latest" -Headers $headers
        $Version = $release.tag_name.TrimStart('v')
        Write-Host "[OK] Latest release: v$Version" -ForegroundColor Green
    } catch {
        Write-Error "Failed to fetch latest release: $_"
        exit 1
    }
}

# Validate version string to prevent path traversal
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][a-zA-Z0-9._-]+)?$') {
    Write-Error "Invalid version string: '$Version'"
    exit 1
}

$zipName    = "DriveChill-$Artifact-$Version.zip"
$zipUrl     = "https://github.com/$GITHUB_REPO/releases/download/v$Version/$zipName"
$tempZip    = Join-Path $env:TEMP $zipName
$tempExtDir = Join-Path $env:TEMP "DriveChill-update-$Version"

# ── 3. Download ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Downloading v$Version..." -ForegroundColor Yellow
try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing
    Write-Host "[OK] Downloaded $zipName" -ForegroundColor Green
} catch {
    Write-Error "Download failed: $_`n  URL: $zipUrl"
    exit 1
}

# ── 4. Stop service ──────────────────────────────────────────────────────────
$serviceExists = (& sc.exe query $SERVICE_NAME 2>$null) -match "STATE"
if ($serviceExists) {
    Write-Host "Stopping $SERVICE_NAME service..." -ForegroundColor Yellow
    & nssm stop $SERVICE_NAME confirm 2>$null
    Start-Sleep -Seconds 3
    Write-Host "[OK] Service stopped" -ForegroundColor Green
} else {
    Write-Host "[INFO] Service '$SERVICE_NAME' not found — files will be updated in place." -ForegroundColor Yellow
}

# ── 5. Extract and copy ──────────────────────────────────────────────────────
Write-Host "Extracting update..." -ForegroundColor Yellow
if (Test-Path $tempExtDir) { Remove-Item $tempExtDir -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempExtDir -Force

# ZIP may contain a single top-level folder — unwrap it
$children = Get-ChildItem $tempExtDir
$srcRoot   = if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
    $children[0].FullName
} else {
    $tempExtDir
}

# Copy everything except the data directory (preserves DB, certs, config)
Get-ChildItem $srcRoot | Where-Object { $_.Name -ne 'data' } | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Recurse -Force
}
Write-Host "[OK] Files updated" -ForegroundColor Green

# ── 6. Update Python dependencies (Python artifact only) ─────────────────────
if ($Artifact -eq 'python') {
    $reqFile = Join-Path $InstallDir "backend\requirements.txt"
    $python  = Get-Command python -ErrorAction SilentlyContinue
    if ($python -and (Test-Path $reqFile)) {
        Write-Host "Updating Python dependencies..." -ForegroundColor Yellow
        & python -m pip install -r $reqFile --quiet
        Write-Host "[OK] Dependencies updated" -ForegroundColor Green
    }
}

# ── 7. Restart service ───────────────────────────────────────────────────────
if ($serviceExists) {
    Write-Host "Starting $SERVICE_NAME service..." -ForegroundColor Yellow
    & nssm start $SERVICE_NAME
    Write-Host "[OK] Service restarted" -ForegroundColor Green
}

# ── 8. Cleanup ───────────────────────────────────────────────────────────────
Remove-Item $tempZip    -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== Updated to v$Version ===" -ForegroundColor Green
Write-Host "Open http://localhost:8085 to verify." -ForegroundColor White
