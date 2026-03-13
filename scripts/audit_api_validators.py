#!/usr/bin/env python3
"""Audit POST/PUT/PATCH/DELETE route handlers for request-body validation.

Scans both backends and reports any POST/PUT/PATCH endpoint that accepts a
request body without a typed/validated model.  DELETE routes are included in
the scan scope but are not required to have a body model.

Python (FastAPI): flags handlers with POST/PUT decorator that have no Pydantic
BaseModel parameter (e.g. raw dict, no body when one is expected).

C# (ASP.NET Core): flags HttpPost/HttpPut methods whose [FromBody] parameter
is JsonElement (untyped) without manual validation in the method body.

Routes that intentionally skip validation should include a comment:
    # audit:skip <reason>          (Python)
    // audit:skip <reason>         (C#)

Exit codes:
    0 — all routes validated (or explicitly skipped)
    1 — one or more routes lack validation
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# Python scanner
# ---------------------------------------------------------------------------

# Matches @router.post(...), @router.put(...), @router.patch(...), or @router.delete(...)
_PY_ROUTE_RE = re.compile(
    r"@router\.(post|put|patch|delete)\s*\(",
    re.IGNORECASE,
)

# Routes that intentionally have no body (action endpoints)
_PY_NO_BODY_PATTERNS = {
    "release", "resume", "activate", "verify", "clear", "rescan", "test",
    "refresh", "cancel", "start", "stop", "abort", "health-check", "logout",
}


def _py_handler_has_body_model(lines: list[str], def_line_idx: int) -> bool:
    """Check if a Python def has a Pydantic body parameter (type-hinted non-Request param)."""
    # Gather the full signature (may span multiple lines)
    sig = ""
    for i in range(def_line_idx, min(def_line_idx + 10, len(lines))):
        sig += lines[i]
        if ")" in lines[i] and ":" in lines[i]:
            break

    # Remove known non-body params
    skip_types = {"Request", "request", "Response", "BackgroundTasks", "Depends", "CancellationToken"}
    # Find params between ( and ):
    m = re.search(r"\((.+?)\)", sig, re.DOTALL)
    if not m:
        return False

    params = m.group(1)
    # Look for a param with a type annotation that is capitalized (Pydantic model)
    for part in params.split(","):
        part = part.strip()
        if ":" not in part:
            continue
        _, type_hint = part.split(":", 1)
        type_hint = type_hint.strip().split("=")[0].strip()
        if type_hint in skip_types or type_hint.startswith("Depends"):
            continue
        # Capitalized type hint = likely a Pydantic model
        if type_hint and type_hint[0].isupper():
            return True
    return False


def _py_is_no_body_route(route_path: str) -> bool:
    """Check if route path suggests an action endpoint with no body."""
    parts = route_path.strip("\"'/").split("/")
    last = parts[-1] if parts else ""
    # Strip path params like {id}
    last = re.sub(r"\{[^}]+\}", "", last).strip("/")
    if not last:
        last = parts[-2] if len(parts) > 1 else ""
    return last.lower() in _PY_NO_BODY_PATTERNS


def scan_python(backend_dir: Path) -> list[dict]:
    """Scan Python route files for unvalidated POST/PUT handlers."""
    issues: list[dict] = []
    routes_dir = backend_dir / "app" / "api" / "routes"
    if not routes_dir.exists():
        return issues

    for py_file in sorted(routes_dir.glob("*.py")):
        lines = py_file.read_text(encoding="utf-8").splitlines()
        for i, line in enumerate(lines):
            m = _PY_ROUTE_RE.search(line)
            if not m:
                continue

            method = m.group(1).upper()

            # Check for audit:skip in surrounding lines (decorator + next few lines)
            context = "\n".join(lines[max(0, i - 1) : min(len(lines), i + 8)])
            if "audit:skip" in context:
                continue

            # Extract route path
            route_m = re.search(r'["\']([^"\']+)["\']', line)
            route_path = route_m.group(1) if route_m else "unknown"

            # Find the def line
            def_idx = None
            for j in range(i + 1, min(i + 5, len(lines))):
                if lines[j].strip().startswith("async def ") or lines[j].strip().startswith("def "):
                    def_idx = j
                    break

            if def_idx is None:
                continue

            func_name_m = re.search(r"def (\w+)", lines[def_idx])
            func_name = func_name_m.group(1) if func_name_m else "unknown"

            # DELETE routes don't need a body model — they just need existence
            # checks handled by the handler.  Skip body-model audit for DELETE.
            if method == "DELETE":
                continue

            # Skip no-body action routes
            if _py_is_no_body_route(route_path):
                continue

            if not _py_handler_has_body_model(lines, def_idx):
                issues.append({
                    "file": str(py_file.relative_to(backend_dir.parent)),
                    "line": i + 1,
                    "method": method,
                    "route": route_path,
                    "handler": func_name,
                    "reason": "No typed Pydantic body parameter",
                })

    return issues


# ---------------------------------------------------------------------------
# C# scanner
# ---------------------------------------------------------------------------

_CS_ROUTE_RE = re.compile(
    r"\[(HttpPost|HttpPut|HttpPatch|HttpDelete)",
    re.IGNORECASE,
)


def _cs_method_has_validation(lines: list[str], start: int) -> bool:
    """Check if a C# method body contains manual validation patterns."""
    # Scan up to 60 lines of method body for validation indicators
    brace_depth = 0
    in_method = False
    for i in range(start, min(start + 80, len(lines))):
        line = lines[i]
        if "{" in line:
            brace_depth += line.count("{") - line.count("}")
            in_method = True
        elif "}" in line:
            brace_depth += line.count("{") - line.count("}")
        if in_method and brace_depth <= 0:
            break

        # Check for validation patterns
        validation_patterns = [
            "BadRequest", "return BadRequest", "return StatusCode(4",
            "IsNullOrWhiteSpace", "IsNullOrEmpty",
            "TryGetProperty", ".Length", "!= null",
            "Validate", "validator",
            "Math.Clamp", "NotFound",
        ]
        for pat in validation_patterns:
            if pat in line:
                return True
    return False


def scan_csharp(backend_dir: Path) -> list[dict]:
    """Scan C# controller files for unvalidated POST/PUT handlers."""
    issues: list[dict] = []
    api_dir = backend_dir / "Api"
    if not api_dir.exists():
        return issues

    for cs_file in sorted(api_dir.glob("*.cs")):
        lines = cs_file.read_text(encoding="utf-8").splitlines()
        for i, line in enumerate(lines):
            m = _CS_ROUTE_RE.search(line)
            if not m:
                continue

            tag = m.group(1)
            method = "POST" if "Post" in tag else ("PATCH" if "Patch" in tag else ("DELETE" if "Delete" in tag else "PUT"))

            # Check for audit:skip
            context = "\n".join(lines[max(0, i - 1) : min(len(lines), i + 8)])
            if "audit:skip" in context:
                continue

            # Find the method signature (next line starting with 'public')
            sig_idx = None
            for j in range(i + 1, min(i + 4, len(lines))):
                if "public" in lines[j] and ("IActionResult" in lines[j] or "Task<" in lines[j]):
                    sig_idx = j
                    break

            if sig_idx is None:
                continue

            func_name_m = re.search(r"(\w+)\s*\(", lines[sig_idx])
            func_name = func_name_m.group(1) if func_name_m else "unknown"

            # Check if it takes a [FromBody] parameter
            sig_text = lines[sig_idx]
            has_from_body = "[FromBody]" in sig_text

            if not has_from_body:
                # No body — likely an action endpoint, skip
                continue

            # Check if body is JsonElement (untyped)
            if "JsonElement" in sig_text:
                # JsonElement is untyped — check for manual validation in body
                if not _cs_method_has_validation(lines, sig_idx):
                    issues.append({
                        "file": str(cs_file.relative_to(backend_dir.parent)),
                        "line": i + 1,
                        "method": method,
                        "handler": func_name,
                        "reason": "JsonElement body without validation",
                    })
                # JsonElement with validation is fine — intentional flexible parsing
                continue

            # Typed model — check if the handler has any validation
            if not _cs_method_has_validation(lines, sig_idx):
                issues.append({
                    "file": str(cs_file.relative_to(backend_dir.parent)),
                    "line": i + 1,
                    "method": method,
                    "handler": func_name,
                    "reason": "Typed body model without handler validation",
                })

    return issues


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> int:
    root = Path(__file__).resolve().parent.parent
    py_backend = root / "backend"
    cs_backend = root / "backend-cs"

    all_issues: list[dict] = []

    if py_backend.exists():
        all_issues.extend(scan_python(py_backend))
    if cs_backend.exists():
        all_issues.extend(scan_csharp(cs_backend))

    if not all_issues:
        print("OK: All POST/PUT/PATCH routes have validated request bodies (or audit:skip).")
        return 0

    print(f"FAIL: {len(all_issues)} route(s) missing request body validation:\n")
    for issue in all_issues:
        route = issue.get("route", "")
        route_str = f" [{route}]" if route else ""
        print(f"  {issue['file']}:{issue['line']}  {issue['method']} {issue['handler']}{route_str}")
        print(f"    -> {issue['reason']}")
        print()

    print("To suppress a false positive, add a comment near the route decorator:")
    print("  Python: # audit:skip <reason>")
    print("  C#:     // audit:skip <reason>")
    return 1


if __name__ == "__main__":
    sys.exit(main())
