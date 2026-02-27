# Security Credential Rotation

Use this runbook after security remediations that touch outbound error handling or secret exposure.

## Scope

- Rotate webhook signing secret.
- Rotate machine API keys (hub-side stored credentials).

## Prerequisites

- Python 3.11+
- DriveChill backend dependencies installed (`backend/requirements.txt`)
- Access to the target `drivechill.db`

## 1) Export machine rotation template

```powershell
python scripts/rotate_security_credentials.py `
  --db-path "$env:APPDATA\DriveChill\drivechill.db" `
  --export-machine-template docs/runbooks/machine-key-rotation.template.json `
  --skip-webhook-secret `
  --dry-run
```

Populate `api_key` (and optional `api_key_id`) for each machine entry in the template file.

## 2) Rotate webhook secret and apply machine keys

```powershell
python scripts/rotate_security_credentials.py `
  --db-path "$env:APPDATA\DriveChill\drivechill.db" `
  --machine-keys-file docs/runbooks/machine-key-rotation.template.json
```

The command prints the new webhook secret once. Store it in your secret manager and update downstream webhook verifiers immediately.

## 3) Verify

- `GET /api/webhooks` reports `has_signing_secret: true`.
- Machine connectivity is healthy in `/api/machines` after the new keys are applied.
- Revoke superseded API keys on each remote machine.
- Downstream webhook verifier checks:
  - `X-DriveChill-Timestamp` is within your replay window (for example, 5 minutes).
  - `X-DriveChill-Nonce` has not been seen before inside that window.
  - `X-DriveChill-Signature` equals `sha256=` HMAC-SHA256 over:
    - `${timestamp}.${nonce}.${raw_request_body}`
