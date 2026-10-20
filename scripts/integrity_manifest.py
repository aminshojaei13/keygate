#!/usr/bin/env python3
"""
Keygate Application Integrity Manifest CLI & Verification Engine (Python 3 reference)

Provides language-agnostic generation, signing, and verification of Keygate
application integrity manifests. Works across macOS, Linux, and Windows.

Usage:
    python3 scripts/integrity_manifest.py create --dir ./dist --product prd_123 --version 1.0.0 --key-id k1 --key <seed_hex>
    python3 scripts/integrity_manifest.py verify --manifest integrity.manifest.json --pubkey <pub_hex> --dir ./installed
    python3 scripts/integrity_manifest.py inspect --manifest integrity.manifest.json
"""

import argparse
import base64
import datetime
import hashlib
import json
import os
import posixpath
import sys

# Format constants
CURRENT_MANIFEST_VERSION = 1
FORMAT_IDENTIFIER = "keygate-integrity-v1"
ALGORITHM_ED25519 = "ed25519"

DEFAULT_EXCLUDES = {
    ".*", "*.sig", "*.tmp", "manifest*.json", "*.integrity.json", ".DS_Store", "Thumbs.db"
}


def base64url_encode(data: bytes) -> str:
    """Encodes bytes to unpadded base64url (RFC 4648 §5)."""
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def base64url_decode(s: str) -> bytes:
    """Decodes unpadded or padded base64 / base64url strings."""
    s = s.strip()
    # Normalize urlsafe characters
    s = s.replace("-", "+").replace("_", "/")
    pad = len(s) % 4
    if pad:
        s += "=" * (4 - pad)
    return base64.b64decode(s.encode("ascii"))


def validate_relative_path(rel_path: str) -> None:
    """
    Enforces strict path security rules:
    - POSIX forward slash only
    - No leading slash, drive letters, or empty components
    - No directory traversal segments ('.' or '..')
    - No null bytes or control characters
    """
    if not rel_path:
        raise ValueError("path cannot be empty")
    if "\\" in rel_path:
        raise ValueError(f"path {rel_path!r} contains backslashes; must use forward slashes '/'")
    if rel_path.startswith("/"):
        raise ValueError(f"path {rel_path!r} cannot be absolute")
    if "\x00" in rel_path:
        raise ValueError(f"path {rel_path!r} contains null byte")

    for c in rel_path:
        if ord(c) < 32 or ord(c) == 127:
            raise ValueError(f"path {rel_path!r} contains illegal control character")

    segments = rel_path.split("/")
    for seg in segments:
        if not seg:
            raise ValueError(f"path {rel_path!r} contains consecutive slashes or empty component")
        if seg in (".", ".."):
            raise ValueError(f"path traversal segment {seg!r} forbidden in {rel_path!r}")

    cleaned = posixpath.normpath(rel_path)
    if cleaned != rel_path:
        raise ValueError(f"path {rel_path!r} is not canonical (differs from {cleaned!r})")
    if cleaned.startswith("../") or cleaned == "..":
        raise ValueError(f"path traversal detected in {rel_path!r}")


def hash_file(file_path: str) -> tuple[str, int]:
    """Streams a file and returns (sha256_hex, size_bytes)."""
    h = hashlib.sha256()
    size = 0
    with open(file_path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
            size += len(chunk)
    return h.hexdigest(), size


def canonicalize_manifest(manifest: dict) -> bytes:
    """
    Produces deterministic, canonical JSON (RFC 8785 compatible):
    - Object keys sorted lexicographically
    - Files sorted by path lexicographically
    - Compact formatting (no insignificant whitespace)
    - UTF-8 encoding
    """
    m = dict(manifest)
    # Sort files by path
    if "files" in m:
        m["files"] = sorted(m["files"], key=lambda x: x["path"])
        for f in m["files"]:
            validate_relative_path(f["path"])
            if len(f.get("sha256", "")) != 64:
                raise ValueError(f"invalid sha256 length for {f['path']}")
            if f.get("size", 0) < 0:
                raise ValueError(f"negative size for {f['path']}")

    if "manifest_version" not in m:
        m["manifest_version"] = CURRENT_MANIFEST_VERSION
    if "created_at" not in m:
        m["created_at"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # Compact JSON with sorted keys
    return json.dumps(m, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def scan_directory(base_dir: str, explicit_files: list[str] = None) -> list[dict]:
    """Scans base_dir and returns sorted list of file entries."""
    abs_base = os.path.abspath(base_dir)
    if not os.path.isdir(abs_base):
        raise ValueError(f"base_dir {base_dir!r} is not a directory")

    entries = []

    if explicit_files:
        for f in explicit_files:
            posix_path = f.replace(os.sep, "/")
            validate_relative_path(posix_path)
            full_path = os.path.join(abs_base, *posix_path.split("/"))
            digest, size = hash_file(full_path)
            entries.append({"path": posix_path, "sha256": digest, "size": size})
    else:
        for root, dirs, files in os.walk(abs_base):
            # Exclude dot-directories
            dirs[:] = [d for d in dirs if not d.startswith(".")]
            for file_name in files:
                if file_name.startswith(".") or file_name.endswith(".sig") or "manifest" in file_name:
                    continue
                abs_path = os.path.join(root, file_name)
                rel_path = os.path.relpath(abs_path, abs_base).replace(os.sep, "/")
                validate_relative_path(rel_path)
                digest, size = hash_file(abs_path)
                entries.append({"path": rel_path, "sha256": digest, "size": size})

    entries.sort(key=lambda x: x["path"])
    return entries


# ─── Ed25519 Signing & Verification (Portable Pure-Python / Cryptography) ───

def ed25519_sign(privkey_bytes: bytes, message: bytes) -> bytes:
    """Signs message using Ed25519 private key (seed or expanded)."""
    try:
        from cryptography.hazmat.primitives.asymmetric import ed25519
        if len(privkey_bytes) == 32:
            key = ed25519.Ed25519PrivateKey.from_private_bytes(privkey_bytes)
        elif len(privkey_bytes) == 64:
            key = ed25519.Ed25519PrivateKey.from_private_bytes(privkey_bytes[:32])
        else:
            raise ValueError(f"invalid ed25519 private key size: {len(privkey_bytes)}")
        return key.sign(message)
    except ImportError:
        # Pure-python RFC 8032 fallback when cryptography module is absent
        return _pure_python_ed25519_sign(privkey_bytes[:32], message)


def ed25519_verify(pubkey_bytes: bytes, message: bytes, signature_bytes: bytes) -> bool:
    """Verifies Ed25519 signature."""
    try:
        from cryptography.hazmat.primitives.asymmetric import ed25519
        if len(pubkey_bytes) != 32:
            return False
        key = ed25519.Ed25519PublicKey.from_public_bytes(pubkey_bytes)
        key.verify(signature_bytes, message)
        return True
    except Exception:
        return _pure_python_ed25519_verify(pubkey_bytes, message, signature_bytes)


# ─── Minimal RFC 8032 Reference Math (Zero-Dependency Fallback) ───
b = 256
q = 2**255 - 19
l = 2**252 + 27742317777372353535851937790883648493
d = -121665 * pow(121666, q - 2, q) % q
I = pow(2, (q - 1) // 4, q)

def _xrecover(y):
    xx = (y * y - 1) * pow(d * y * y + 1, q - 2, q) % q
    x = pow(xx, (q + 3) // 8, q)
    if (x * x - xx) % q != 0:
        x = (x * I) % q
    if x % 2 != 0:
        x = q - x
    return x

By = 4 * pow(5, q - 2, q) % q
Bx = _xrecover(By)
B = [Bx % q, By % q]

def _edwards(P, Q):
    x1, y1 = P
    x2, y2 = Q
    x3 = (x1 * y2 + x2 * y1) * pow(1 + d * x1 * x2 * y1 * y2, q - 2, q) % q
    y3 = (y1 * y2 + x1 * x2) * pow(1 - d * x1 * x2 * y1 * y2, q - 2, q) % q
    return [x3, y3]

def _scalarmult(P, e):
    if e == 0:
        return [0, 1]
    Q = _scalarmult(P, e // 2)
    Q = _edwards(Q, Q)
    if e & 1:
        Q = _edwards(Q, P)
    return Q

def _encodeint(y):
    return int(y).to_bytes(32, "little")

def _encodepoint(P):
    x, y = P
    bits = [(y >> i) & 1 for i in range(b - 1)] + [x & 1]
    return bytes(sum(bits[i * 8 + j] << j for j in range(8)) for i in range(b // 8))

def _bit(h, i):
    return (h[i // 8] >> (i % 8)) & 1

def _pure_python_ed25519_publickey(secret_seed):
    h = hashlib.sha512(secret_seed).digest()
    a = 2**(b - 2) + sum(2**i * _bit(h, i) for i in range(3, b - 2))
    A = _scalarmult(B, a)
    return _encodepoint(A)

def _pure_python_ed25519_sign(secret_seed, message):
    h = hashlib.sha512(secret_seed).digest()
    a = 2**(b - 2) + sum(2**i * _bit(h, i) for i in range(3, b - 2))
    A = _scalarmult(B, a)
    r = int.from_bytes(hashlib.sha512(h[32:] + message).digest(), "little") % l
    R = _scalarmult(B, r)
    k = int.from_bytes(hashlib.sha512(_encodepoint(R) + _encodepoint(A) + message).digest(), "little") % l
    S = (r + k * a) % l
    return _encodepoint(R) + _encodeint(S)

def _pure_python_ed25519_verify(pubkey, message, signature):
    if len(pubkey) != 32 or len(signature) != 64:
        return False
    Rraw, Sraw = signature[:32], signature[32:]
    S = int.from_bytes(Sraw, "little")
    if S >= l:
        return False
    # Decode A point
    Ay = int.from_bytes(pubkey, "little") & ((1 << 255) - 1)
    Ax = _xrecover(Ay)
    if (int.from_bytes(pubkey, "little") >> 255) != (Ax & 1):
        Ax = q - Ax
    A = [Ax, Ay]
    # Decode R point
    Ry = int.from_bytes(Rraw, "little") & ((1 << 255) - 1)
    Rx = _xrecover(Ry)
    if (int.from_bytes(Rraw, "little") >> 255) != (Rx & 1):
        Rx = q - Rx
    R = [Rx, Ry]
    k = int.from_bytes(hashlib.sha512(Rraw + pubkey + message).digest(), "little") % l
    return _scalarmult(B, S) == _edwards(R, _scalarmult(A, k))


# ─── High-Level Manifest Operations ───

def sign_manifest(manifest: dict, privkey_bytes: bytes, key_id: str) -> dict:
    """Signs manifest and returns signed envelope."""
    m = dict(manifest)
    m["signing_key_id"] = key_id
    m["manifest_version"] = CURRENT_MANIFEST_VERSION
    canonical_bytes = canonicalize_manifest(m)

    payload_b64 = base64url_encode(canonical_bytes)
    sig_bytes = ed25519_sign(privkey_bytes, payload_b64.encode("ascii"))
    sig_b64 = base64url_encode(sig_bytes)

    return {
        "format": FORMAT_IDENTIFIER,
        "manifest_version": CURRENT_MANIFEST_VERSION,
        "payload": payload_b64,
        "signature": sig_b64,
        "signing_key_id": key_id,
        "algorithm": ALGORITHM_ED25519,
        "manifest": m,
    }


def verify_envelope(envelope: dict, trusted_keys: dict[str, bytes]) -> dict:
    """Verifies envelope signature and returns canonical manifest."""
    if envelope.get("format") != FORMAT_IDENTIFIER:
        raise ValueError(f"unsupported format {envelope.get('format')}")
    if envelope.get("manifest_version") != CURRENT_MANIFEST_VERSION:
        raise ValueError(f"unsupported manifest version {envelope.get('manifest_version')}")
    if envelope.get("algorithm") != ALGORITHM_ED25519:
        raise ValueError(f"unsupported algorithm {envelope.get('algorithm')}")

    key_id = envelope.get("signing_key_id")
    if not key_id or key_id not in trusted_keys:
        raise ValueError(f"unknown signing key ID {key_id!r}")

    pubkey_bytes = trusted_keys[key_id]
    payload_str = envelope["payload"]
    sig_bytes = base64url_decode(envelope["signature"])

    if not ed25519_verify(pubkey_bytes, payload_str.encode("ascii"), sig_bytes):
        raise ValueError("signature verification failed: manifest is untrusted")

    payload_bytes = base64url_decode(payload_str)
    manifest = json.loads(payload_bytes.decode("utf-8"))

    for f in manifest.get("files", []):
        validate_relative_path(f["path"])

    return manifest


def verify_installed_files(manifest: dict, install_dir: str) -> dict:
    """Verifies all files in manifest against install_dir."""
    abs_root = os.path.abspath(install_dir)
    passed = 0
    missing = []
    modified = []
    errors = []

    for entry in manifest.get("files", []):
        rel_path = entry["path"]
        try:
            validate_relative_path(rel_path)
            target = os.path.abspath(os.path.join(abs_root, *rel_path.split("/")))
            if not target.startswith(abs_root + os.sep) and target != abs_root:
                errors.append(f"path traversal detected for {rel_path}")
                continue

            if not os.path.exists(target):
                missing.append(rel_path)
                continue

            stat = os.stat(target)
            if stat.st_size != entry["size"]:
                modified.append({
                    "path": rel_path,
                    "expected_size": entry["size"],
                    "actual_size": stat.st_size,
                    "reason": "file size mismatch"
                })
                continue

            digest, _ = hash_file(target)
            if digest.lower() != entry["sha256"].lower():
                modified.append({
                    "path": rel_path,
                    "expected_sha256": entry["sha256"].lower(),
                    "actual_sha256": digest.lower(),
                    "reason": "sha256 digest mismatch"
                })
                continue

            passed += 1
        except Exception as e:
            errors.append(f"{rel_path}: {e}")

    valid = (len(missing) == 0 and len(modified) == 0 and len(errors) == 0)
    return {
        "valid": valid,
        "total": len(manifest.get("files", [])),
        "passed": passed,
        "missing": missing,
        "modified": modified,
        "errors": errors
    }


# ─── Command Line Interface ───

def parse_key_bytes(raw: str) -> bytes:
    raw = raw.strip()
    if os.path.isfile(raw):
        with open(raw, "rb") as f:
            raw = f.read().decode("utf-8", errors="ignore").strip()

    if len(raw) == 64:
        try:
            return bytes.fromhex(raw)
        except ValueError:
            pass

    return base64url_decode(raw)


def main():
    parser = argparse.ArgumentParser(description="Keygate Integrity Manifest Tool")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # create
    p_create = subparsers.add_parser("create", help="Generate and sign manifest")
    p_create.add_argument("--dir", required=True, help="Release directory to protect")
    p_create.add_argument("--product", required=True, help="Product identifier")
    p_create.add_argument("--version", required=True, help="Version string")
    p_create.add_argument("--release-id", default="", help="Optional release ID")
    p_create.add_argument("--channel", default="stable", help="Release channel")
    p_create.add_argument("--platform", default="", help="Platform identifier")
    p_create.add_argument("--arch", default="", help="Architecture identifier")
    p_create.add_argument("--key-id", required=True, help="Signing key ID")
    p_create.add_argument("--key", required=True, help="Signing key seed (hex or base64)")
    p_create.add_argument("--out", default="integrity.manifest.json", help="Output envelope file")

    # verify
    p_verify = subparsers.add_parser("verify", help="Verify signed manifest and installed files")
    p_verify.add_argument("--manifest", default="integrity.manifest.json", help="Manifest envelope file")
    p_verify.add_argument("--pubkey", required=True, help="Public verification key (hex or base64)")
    p_verify.add_argument("--key-id", default="", help="Expected key ID (optional)")
    p_verify.add_argument("--dir", required=True, help="Installation directory to verify")

    # inspect
    p_inspect = subparsers.add_parser("inspect", help="Inspect signed manifest envelope")
    p_inspect.add_argument("--manifest", default="integrity.manifest.json", help="Manifest envelope file")

    args = parser.parse_args()

    if args.command == "create":
        privkey = parse_key_bytes(args.key)
        print(f"Scanning directory: {args.dir}")
        entries = scan_directory(args.dir)
        print(f"Collected {len(entries)} protected file entries.")
        manifest = {
            "product_id": args.product,
            "version": args.version,
            "release_id": args.release_id,
            "channel": args.channel,
            "platform": args.platform,
            "arch": args.arch,
            "files": entries,
        }
        env = sign_manifest(manifest, privkey, args.key_id)
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(env, f, indent=2)
        print(f"Successfully generated signed manifest -> {args.out}")

    elif args.command == "verify":
        pubkey = parse_key_bytes(args.pubkey)
        with open(args.manifest, "r", encoding="utf-8") as f:
            env = json.load(f)
        target_kid = args.key_id or env.get("signing_key_id")
        trusted = {target_kid: pubkey}
        print("Step 1: Cryptographic signature verification...")
        try:
            m = verify_envelope(env, trusted)
        except Exception as e:
            print(f"FAILED: signature verification error: {e}", file=sys.stderr)
            sys.exit(2)
        print(f"  ✓ Signature valid for key {target_kid!r} ({m.get('product_id')} v{m.get('version')})")
        print("Step 2: Installed files verification...")
        res = verify_installed_files(m, args.dir)
        print(f"  Total files: {res['total']}, Passed: {res['passed']}")
        if res["missing"]:
            print(f"  Missing files ({len(res['missing'])}): {res['missing']}")
        if res["modified"]:
            print(f"  Modified files ({len(res['modified'])}): {res['modified']}")
        if res["errors"]:
            print(f"  Errors ({len(res['errors'])}): {res['errors']}")
        if not res["valid"]:
            print("\nINTEGRITY CHECK FAILED: Differences detected.")
            sys.exit(3)
        print("\nINTEGRITY VERIFICATION PASSED: All installed files match signed manifest.")

    elif args.command == "inspect":
        with open(args.manifest, "r", encoding="utf-8") as f:
            env = json.load(f)
        payload = json.loads(base64url_decode(env["payload"]).decode("utf-8"))
        print(f"=== Keygate Integrity Manifest ===")
        print(f"Format:         {env.get('format')}")
        print(f"Version:        {payload.get('version')}")
        print(f"Product:        {payload.get('product_id')}")
        print(f"Signing Key ID: {env.get('signing_key_id')}")
        print(f"Protected Files: {len(payload.get('files', []))}")
        for f in payload.get("files", []):
            print(f"  [{f['size']:8d} B] {f['path']} ({f['sha256'][:16]}...)")


if __name__ == "__main__":
    main()
