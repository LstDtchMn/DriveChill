"""Self-signed TLS certificate generation for DriveChill HTTPS support.

Usage:
    DRIVECHILL_SSL_GENERATE_SELF_SIGNED=true python -m uvicorn app.main:app

Or provide your own cert/key:
    DRIVECHILL_SSL_CERTFILE=/path/to/cert.pem DRIVECHILL_SSL_KEYFILE=/path/to/key.pem
"""

from __future__ import annotations

import datetime
import logging
import os
from pathlib import Path

logger = logging.getLogger(__name__)


def generate_self_signed_cert(
    data_dir: Path,
    hostname: str = "localhost",
) -> tuple[str, str]:
    """Generate a self-signed TLS certificate and private key.

    Returns (certfile_path, keyfile_path) as strings.
    Files are saved to data_dir/tls/.
    """
    try:
        from cryptography import x509
        from cryptography.x509.oid import NameOID
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
    except ImportError:
        raise RuntimeError(
            "The 'cryptography' package is required for self-signed certificate generation. "
            "Install it with: pip install cryptography"
        )

    tls_dir = data_dir / "tls"
    tls_dir.mkdir(parents=True, exist_ok=True)

    cert_path = tls_dir / "drivechill.crt"
    key_path = tls_dir / "drivechill.key"

    # Skip generation if both files already exist
    if cert_path.exists() and key_path.exists():
        logger.info("TLS certificate already exists at %s", cert_path)
        return str(cert_path), str(key_path)

    logger.info("Generating self-signed TLS certificate for %s", hostname)

    # Generate RSA private key
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)

    # Build certificate
    subject = issuer = x509.Name([
        x509.NameAttribute(NameOID.COMMON_NAME, hostname),
        x509.NameAttribute(NameOID.ORGANIZATION_NAME, "DriveChill (self-signed)"),
    ])

    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now)
        .not_valid_after(now + datetime.timedelta(days=365))
        .add_extension(
            x509.SubjectAlternativeName([
                x509.DNSName(hostname),
                x509.DNSName("localhost"),
                x509.IPAddress(
                    __import__("ipaddress").ip_address("127.0.0.1")
                ),
            ]),
            critical=False,
        )
        .sign(key, hashes.SHA256())
    )

    # Write key (restrictive permissions)
    key_path.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.TraditionalOpenSSL,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    # Best-effort: restrict key file permissions (ignored on Windows)
    try:
        os.chmod(key_path, 0o600)
    except OSError:
        pass

    # Write certificate
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))

    logger.info("TLS certificate written to %s", cert_path)
    return str(cert_path), str(key_path)
