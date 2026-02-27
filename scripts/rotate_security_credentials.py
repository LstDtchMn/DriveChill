#!/usr/bin/env python
"""Rotate webhook secret and machine API keys for security closeout."""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import secrets
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[1]
BACKEND_DIR = REPO_ROOT / "backend"
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from app.config import settings  # noqa: E402
from app.db.migration_runner import run_migrations  # noqa: E402


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _load_machine_updates(path: Path) -> list[dict[str, str | None]]:
    raw: Any = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(raw, dict):
        updates = [
            {
                "machine_id": str(machine_id),
                "api_key": str(api_key),
                "api_key_id": None,
            }
            for machine_id, api_key in raw.items()
        ]
    elif isinstance(raw, list):
        updates = []
        for item in raw:
            if not isinstance(item, dict):
                raise ValueError("machine key entries must be objects")
            machine_id = str(item.get("machine_id", "")).strip()
            api_key = item.get("api_key")
            api_key_id = item.get("api_key_id")
            if not machine_id:
                raise ValueError("machine_id is required for each machine key entry")
            if not isinstance(api_key, str) or not api_key.strip():
                raise ValueError(f"api_key is required for machine {machine_id}")
            updates.append(
                {
                    "machine_id": machine_id,
                    "api_key": api_key.strip(),
                    "api_key_id": str(api_key_id).strip() if isinstance(api_key_id, str) else None,
                }
            )
    else:
        raise ValueError("machine key file must be a JSON object or array")
    return updates


def _export_machine_template(cursor: sqlite3.Cursor, out_path: Path) -> int:
    cursor.execute("SELECT id, name, base_url FROM machines ORDER BY created_at ASC")
    rows = cursor.fetchall()
    template = [
        {
            "machine_id": row[0],
            "name": row[1],
            "base_url": row[2],
            "api_key": "",
            "api_key_id": None,
        }
        for row in rows
    ]
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(template, indent=2), encoding="utf-8")
    return len(template)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Rotate webhook signing secret and machine API keys."
    )
    parser.add_argument(
        "--db-path",
        default=str(settings.db_path),
        help="Path to DriveChill SQLite database.",
    )
    parser.add_argument(
        "--machine-keys-file",
        type=Path,
        default=None,
        help="JSON file containing machine_id -> api_key updates.",
    )
    parser.add_argument(
        "--export-machine-template",
        type=Path,
        default=None,
        help="Write a machine key rotation template JSON file.",
    )
    parser.add_argument(
        "--skip-webhook-secret",
        action="store_true",
        help="Skip webhook signing secret rotation.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report changes without committing them.",
    )
    args = parser.parse_args()

    db_path = Path(os.path.expandvars(os.path.expanduser(args.db_path))).resolve()
    db_path.parent.mkdir(parents=True, exist_ok=True)

    asyncio.run(run_migrations(db_path))

    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()

    rotated_webhook_secret = False
    webhook_secret: str | None = None
    machine_updates_applied = 0
    machine_template_count = 0

    if not args.skip_webhook_secret:
        webhook_secret = secrets.token_urlsafe(48)
        cur.execute(
            "UPDATE webhooks SET signing_secret = ?, updated_at = ? WHERE id = 1",
            (webhook_secret, _utc_now()),
        )
        rotated_webhook_secret = cur.rowcount > 0

    if args.export_machine_template is not None:
        machine_template_count = _export_machine_template(cur, args.export_machine_template.resolve())

    if args.machine_keys_file is not None:
        updates = _load_machine_updates(args.machine_keys_file.resolve())
        for item in updates:
            cur.execute(
                "UPDATE machines SET api_key = ?, api_key_id = ?, updated_at = ? WHERE id = ?",
                (
                    item["api_key"],
                    item["api_key_id"],
                    _utc_now(),
                    item["machine_id"],
                ),
            )
            machine_updates_applied += cur.rowcount

    if args.dry_run:
        conn.rollback()
    else:
        conn.commit()
    conn.close()

    print(f"Database: {db_path}")
    print(f"Dry run: {'yes' if args.dry_run else 'no'}")
    print(f"Webhook secret rotated: {'yes' if rotated_webhook_secret else 'no'}")
    if webhook_secret and not args.dry_run and rotated_webhook_secret:
        print(f"New webhook signing secret (store securely): {webhook_secret}")
    print(f"Machine keys updated: {machine_updates_applied}")
    if args.export_machine_template is not None:
        print(f"Machine template entries exported: {machine_template_count}")
        print(f"Template path: {args.export_machine_template.resolve()}")
    if args.machine_keys_file is None:
        print("Machine keys file not provided: no machine key updates applied.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
