#!/usr/bin/env python3
"""Bump DriveChill version across all source files and create a git tag.

Usage:
    python scripts/bump_version.py 2.2.0
"""

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Each entry: (relative path, regex pattern, replacement factory)
_FILES = [
    (
        "frontend/package.json",
        r'"version":\s*"[^"]+"',
        lambda v: f'"version": "{v}"',
    ),
    (
        "backend/app/config.py",
        r'app_version:\s*str\s*=\s*"[^"]+"',
        lambda v: f'app_version: str = "{v}"',
    ),
    (
        "backend-cs/AppSettings.cs",
        r'(public string AppVersion \{ get; set; \} = ")[^"]+(")',
        lambda v: rf'\g<1>{v}\g<2>',
    ),
]


def _bump_file(rel: str, pattern: str, replacement) -> None:
    path = ROOT / rel
    text = path.read_text(encoding="utf-8")
    new_text, n = re.subn(pattern, replacement, text)
    if n == 0:
        sys.exit(f"[error] Pattern not found in {rel}\n  pattern: {pattern}")
    path.write_text(new_text, encoding="utf-8")
    print(f"  updated {rel}")


def _bump_csproj(version: str) -> None:
    path = ROOT / "backend-cs/DriveChill.csproj"
    text = path.read_text(encoding="utf-8")
    text, n1 = re.subn(
        r"<Version>[^<]+</Version>",
        f"<Version>{version}</Version>",
        text,
    )
    text, n2 = re.subn(
        r"<AssemblyVersion>[^<]+</AssemblyVersion>",
        f"<AssemblyVersion>{version}.0</AssemblyVersion>",
        text,
    )
    if n1 == 0 or n2 == 0:
        sys.exit("[error] <Version> or <AssemblyVersion> not found in DriveChill.csproj")
    path.write_text(text, encoding="utf-8")
    print("  updated backend-cs/DriveChill.csproj")


def _git(args: list[str]) -> None:
    subprocess.run(["git", *args], cwd=ROOT, check=True)


def bump(version: str) -> None:
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[\w.]+)?", version):
        sys.exit(f"[error] Invalid version '{version}'. Expected X.Y.Z or X.Y.Z-rc1")

    print(f"Bumping to {version}...")

    for rel, pattern, replacement in _FILES:
        _bump_file(rel, pattern, replacement(version))

    _bump_csproj(version)

    staged = [
        "frontend/package.json",
        "backend/app/config.py",
        "backend-cs/AppSettings.cs",
        "backend-cs/DriveChill.csproj",
    ]
    _git(["add", *staged])
    _git(["commit", "-m", f"chore(release): bump version to {version}"])
    _git(["tag", f"v{version}", "-m", f"DriveChill v{version}"])

    print(f"\nDone — tag v{version} created.")
    print("Push with:  git push origin main --tags")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(f"Usage: python {sys.argv[0]} X.Y.Z")
    bump(sys.argv[1])
