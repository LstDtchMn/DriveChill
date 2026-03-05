from __future__ import annotations

import asyncio
import ipaddress
import socket
from urllib.parse import urlparse


def _is_blocked_ip(ip: ipaddress.IPv4Address | ipaddress.IPv6Address, *, allow_private: bool) -> str | None:
    """Return a rejection reason if the IP is unsafe, or None if OK."""
    if ip.is_loopback:
        return "Loopback targets are not allowed"
    # IPv4-mapped loopback (::ffff:127.x.x.x) — Python <3.11 does not
    # report .is_loopback=True for these addresses.
    if isinstance(ip, ipaddress.IPv6Address):
        mapped = ip.ipv4_mapped
        if mapped is not None and mapped.is_loopback:
            return "Loopback targets are not allowed"
    if ip.is_link_local:
        return "Link-local targets are not allowed"
    if ip.is_multicast or ip.is_reserved or ip.is_unspecified:
        return "Non-routable targets are not allowed"
    if ip.is_private and not allow_private:
        return "Private network targets are not allowed"
    return None


def _resolve_and_check(hostname: str, allow_private: bool) -> tuple[bool, str | None]:
    """Synchronous DNS resolution + IP blocklist check."""
    try:
        infos = socket.getaddrinfo(hostname, None, type=socket.SOCK_STREAM)
    except socket.gaierror:
        return False, "Hostname cannot be resolved"

    resolved_ips: set[str] = set()
    for info in infos:
        try:
            sockaddr = info[4]
            ip_str = sockaddr[0]
            resolved_ips.add(ip_str)
        except Exception:
            continue

    if not resolved_ips:
        return False, "Hostname has no routable address"

    for ip_str in resolved_ips:
        ip = ipaddress.ip_address(ip_str)
        reason = _is_blocked_ip(ip, allow_private=allow_private)
        if reason:
            return False, reason

    return True, None


def _parse_and_check_scheme(url: str) -> tuple[str | None, str | None]:
    """Parse URL and return (hostname, None) on success or (None, error) on failure."""
    try:
        parsed = urlparse(url)
    except Exception:
        return None, "Invalid URL"
    if parsed.scheme not in {"http", "https"}:
        return None, "URL must start with http:// or https://"
    if not parsed.hostname:
        return None, "URL hostname is required"

    hostname = parsed.hostname.strip().lower()
    if hostname in {"localhost"}:
        return None, "localhost is not allowed"

    return hostname, None


def validate_outbound_url(
    url: str,
    *,
    allow_private: bool = False,
) -> tuple[bool, str | None]:
    """Validate HTTP(S) outbound targets and block unsafe hosts by default.

    This is a synchronous version used in Pydantic validators and other
    non-async contexts. Prefer validate_outbound_url_async in async code.
    """
    hostname, err = _parse_and_check_scheme(url)
    if err:
        return False, err
    assert hostname is not None
    return _resolve_and_check(hostname, allow_private)


async def validate_outbound_url_async(
    url: str,
    *,
    allow_private: bool = False,
) -> tuple[bool, str | None]:
    """Async version of validate_outbound_url — runs DNS resolution in a
    thread pool to avoid blocking the event loop."""
    hostname, err = _parse_and_check_scheme(url)
    if hostname is None:
        return False, err or "URL hostname is required"
    loop = asyncio.get_running_loop()
    return await loop.run_in_executor(None, _resolve_and_check, hostname, allow_private)


async def validate_outbound_url_at_request_time(
    url: str,
    *,
    allow_private: bool = False,
) -> tuple[bool, str | None]:
    """Re-validate a stored URL immediately before making a request.

    This defends against DNS rebinding: a hostname that resolved to a safe IP
    at config-save time may now resolve to a loopback/private address.
    Uses async DNS to avoid blocking the event loop.
    """
    return await validate_outbound_url_async(url, allow_private=allow_private)
