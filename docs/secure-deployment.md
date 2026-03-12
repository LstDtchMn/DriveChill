# Secure Deployment

## Non-Localhost Bind Address

When DriveChill binds to a non-loopback address (e.g., `0.0.0.0`, a LAN IP, or
any address other than `127.0.0.1` / `::1` / `localhost`), authentication is
**automatically required**. Both backends enforce this at startup:

- If no user accounts exist **and** `DRIVECHILL_PASSWORD` is not set, the server
  **refuses to start** with an error message.
- If `DRIVECHILL_PASSWORD` is set, an `admin` user is auto-created on first
  startup using that password.

This prevents accidentally exposing an unauthenticated DriveChill instance on
the network.

### Docker

The default `docker-compose.yml` binds to `0.0.0.0` so the container is
reachable from the Docker host. You **must** set `DRIVECHILL_PASSWORD` before
the first run:

```bash
# .env file (next to docker-compose.yml)
DRIVECHILL_PASSWORD=your-strong-password-here
```

Or pass it inline:

```bash
DRIVECHILL_PASSWORD=changeme docker compose up -d
```

### Localhost-Only (No Auth Required)

If you only access DriveChill from the same machine, bind to loopback:

```bash
DRIVECHILL_HOST=127.0.0.1
```

Auth is optional in this configuration but can be forced with:

```bash
DRIVECHILL_FORCE_AUTH=true
DRIVECHILL_PASSWORD=your-password
```

## TLS / HTTPS

For production deployments exposed beyond localhost, enable TLS:

```bash
DRIVECHILL_SSL_CERTFILE=/path/to/cert.pem
DRIVECHILL_SSL_KEYFILE=/path/to/key.pem
```

Or generate a self-signed certificate automatically:

```bash
DRIVECHILL_SSL_GENERATE_SELF_SIGNED=true
```

## Secret Key

Set `DRIVECHILL_SECRET_KEY` to encrypt sensitive credentials (e.g., SMTP
passwords) at rest. Generate one with:

```bash
python -c "import secrets; print(secrets.token_hex(32))"
```

Without this, credentials are stored in plaintext and a warning is logged.

## Checklist

1. Set `DRIVECHILL_PASSWORD` before first start on any non-localhost deployment
2. Enable TLS for any deployment accessible over a network
3. Set `DRIVECHILL_SECRET_KEY` if using email notifications
4. Review `DRIVECHILL_ALLOW_PRIVATE_OUTBOUND_TARGETS` (default: `false`)
5. Consider `DRIVECHILL_FORCE_AUTH=true` even on localhost for shared machines
