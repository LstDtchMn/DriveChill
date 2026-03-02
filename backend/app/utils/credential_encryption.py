"""AES-256-GCM encryption helpers for sensitive credentials stored at rest.

Format: ``v1:<base64(12-byte-nonce || ciphertext || 16-byte-tag)>``

Usage::

    from app.utils.credential_encryption import encrypt, decrypt, is_encrypted

    stored = encrypt("my-password", secret_key="deployment-secret")
    plaintext = decrypt(stored, secret_key="deployment-secret")

When ``secret_key`` is empty the helpers fall back to plaintext with a
warning, so the app works out-of-the-box without requiring extra config.
"""

from __future__ import annotations

import base64
import hashlib
import logging
import os

logger = logging.getLogger(__name__)

_PREFIX = "v1:"
_NONCE_LEN = 12
_TAG_LEN = 16
_AAD = b"smtp"  # additional authenticated data — binds ciphertext to context


def _derive_key(secret: str) -> bytes:
    """Derive a 32-byte AES key from the deployment secret via SHA-256."""
    return hashlib.sha256(secret.encode()).digest()


def encrypt(plaintext: str, secret_key: str) -> str:
    """Encrypt *plaintext* and return a ``v1:…`` ciphertext string.

    Falls back to returning *plaintext* unchanged (with a warning) when
    *secret_key* is empty so the application works without extra config.
    """
    if not secret_key:
        logger.warning(
            "DRIVECHILL_SECRET_KEY is not set; SMTP password stored in plaintext. "
            "Set the env var to enable at-rest encryption."
        )
        return plaintext

    from cryptography.hazmat.primitives.ciphers.aead import AESGCM

    nonce = os.urandom(_NONCE_LEN)
    aesgcm = AESGCM(_derive_key(secret_key))
    # AESGCM.encrypt returns ciphertext + tag concatenated (tag appended at end)
    ct_and_tag = aesgcm.encrypt(nonce, plaintext.encode(), _AAD)
    payload = base64.b64encode(nonce + ct_and_tag).decode()
    return _PREFIX + payload


def decrypt(stored: str, secret_key: str) -> str:
    """Decrypt a ``v1:…`` ciphertext string and return the plaintext.

    Returns *stored* unchanged if it is not encrypted (plaintext fallback).
    Returns an empty string when the key is missing but the value is encrypted.
    """
    if not stored.startswith(_PREFIX):
        return stored  # plaintext — no migration needed yet

    if not secret_key:
        logger.warning(
            "DRIVECHILL_SECRET_KEY is not set but the stored SMTP password is "
            "encrypted. Returning empty password; configure the secret key to "
            "decrypt credentials."
        )
        return ""

    from cryptography.hazmat.primitives.ciphers.aead import AESGCM

    try:
        data = base64.b64decode(stored[len(_PREFIX):])
        nonce = data[:_NONCE_LEN]
        ct_and_tag = data[_NONCE_LEN:]  # cryptography lib separates tag internally
        aesgcm = AESGCM(_derive_key(secret_key))
        return aesgcm.decrypt(nonce, ct_and_tag, _AAD).decode()
    except Exception:
        logger.exception("Failed to decrypt stored credential — returning empty string")
        return ""


def is_encrypted(value: str) -> bool:
    """Return True if *value* looks like an encrypted credential."""
    return value.startswith(_PREFIX)
