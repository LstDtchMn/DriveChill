"""Unit tests for the IP-pinning httpx transport."""
import socket
from unittest.mock import AsyncMock, MagicMock, patch

import httpx
import pytest

from app.services.ip_pinning_transport import IPPinningTransport

# Patch target: the module-level sync helper called via run_in_executor
_PATCH = "app.services.ip_pinning_transport._sync_getaddrinfo"


@pytest.mark.anyio
async def test_blocks_private_ip_by_default():
    """Transport resolves DNS to a private IP → ConnectError raised."""
    transport = IPPinningTransport(allow_private=False)

    fake_addrinfo = [(socket.AF_INET, socket.SOCK_STREAM, 0, "", ("10.0.0.1", 80))]
    with patch(_PATCH, return_value=fake_addrinfo):
        with pytest.raises(httpx.ConnectError, match="private"):
            request = httpx.Request("GET", "http://evil.example.com/api/health")
            await transport.handle_async_request(request)


@pytest.mark.anyio
async def test_allows_private_ip_when_configured():
    """Transport with allow_private=True lets private IPs through."""
    transport = IPPinningTransport(allow_private=True)

    fake_addrinfo = [(socket.AF_INET, socket.SOCK_STREAM, 0, "", ("192.168.1.50", 80))]
    with patch(_PATCH, return_value=fake_addrinfo):
        with patch.object(
            httpx.AsyncHTTPTransport,
            "handle_async_request",
            new_callable=AsyncMock,
            return_value=httpx.Response(200),
        ) as mock_parent:
            request = httpx.Request("GET", "http://internal.lan/api/health")
            resp = await transport.handle_async_request(request)
            assert resp.status_code == 200
            called_request = mock_parent.call_args[0][0]
            assert "192.168.1.50" in str(called_request.url)


@pytest.mark.anyio
async def test_pins_public_ip_and_sets_host_header():
    """Transport resolves public IP, pins URL, preserves Host header."""
    transport = IPPinningTransport(allow_private=False)

    fake_addrinfo = [(socket.AF_INET, socket.SOCK_STREAM, 0, "", ("93.184.216.34", 80))]
    with patch(_PATCH, return_value=fake_addrinfo):
        with patch.object(
            httpx.AsyncHTTPTransport,
            "handle_async_request",
            new_callable=AsyncMock,
            return_value=httpx.Response(200),
        ) as mock_parent:
            request = httpx.Request("GET", "http://example.com/api/sensors")
            await transport.handle_async_request(request)
            called_request = mock_parent.call_args[0][0]
            assert "93.184.216.34" in str(called_request.url)
            assert called_request.headers["Host"] == "example.com"


@pytest.mark.anyio
async def test_unresolvable_host_raises_connect_error():
    """Transport raises ConnectError when DNS returns no results."""
    transport = IPPinningTransport(allow_private=False)

    with patch(_PATCH, return_value=[]):
        with pytest.raises(httpx.ConnectError, match="Cannot resolve"):
            request = httpx.Request("GET", "http://nonexistent.invalid/api/health")
            await transport.handle_async_request(request)
