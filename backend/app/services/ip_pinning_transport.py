"""Custom httpx transport that pins DNS resolution to prevent rebinding attacks."""
from __future__ import annotations

import asyncio
import ipaddress
import socket

import httpx


def _sync_getaddrinfo(host: str, port: int) -> list:
    """Synchronous DNS resolution — isolated so tests can patch it cleanly."""
    return socket.getaddrinfo(host, port, socket.AF_UNSPEC, socket.SOCK_STREAM)


class IPPinningTransport(httpx.AsyncHTTPTransport):
    """Resolves DNS once and pins the TCP connection to that IP.

    Prevents DNS rebinding SSRF: an attacker-controlled domain could return
    a safe IP during pre-request validation, then rebind to an internal IP
    (e.g., 169.254.169.254) before the actual HTTP connection is made.
    This transport resolves DNS in the same async context as the request
    and forces the connection to the resolved IP.
    """

    def __init__(self, *, allow_private: bool = False, **kwargs: object) -> None:
        super().__init__(**kwargs)
        self._allow_private = allow_private

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        host = request.url.host
        port = request.url.port or (443 if request.url.scheme == "https" else 80)

        # Resolve DNS in a thread pool executor (non-blocking)
        loop = asyncio.get_running_loop()
        infos = await loop.run_in_executor(None, _sync_getaddrinfo, host, port)

        if not infos:
            raise httpx.ConnectError(f"Cannot resolve host: {host}")

        ip = infos[0][4][0]

        # Validate resolved IP
        addr = ipaddress.ip_address(ip)
        if not self._allow_private and addr.is_private:
            raise httpx.ConnectError(
                f"Resolved to private IP — blocked for security"
            )

        # Pin: replace hostname with resolved IP, preserve Host header.
        # Build headers explicitly to avoid duplicating the Host header that
        # httpx auto-generates from the original URL.
        pinned_url = request.url.copy_with(host=ip)
        merged_headers = {
            k: v for k, v in request.headers.items() if k.lower() != "host"
        }
        merged_headers["Host"] = host
        pinned_request = httpx.Request(
            method=request.method,
            url=pinned_url,
            headers=merged_headers,
            content=request.content,
        )
        return await super().handle_async_request(pinned_request)
