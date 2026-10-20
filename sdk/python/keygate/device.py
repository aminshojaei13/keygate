"""
Keygate Platform-Aware Device Identification Provider.
Implements the versioned Keygate Device Identity Protocol (v1).
"""

import hashlib
import os
import platform
import subprocess
import sys
from typing import Optional
import uuid


class DeviceIdentifier:
    """
    Cross-platform device identity generator and local persistence provider.
    Produces canonical identifiers adhering to the Keygate Device Identity Protocol v1:
      kgid:v1:<platform>:<64hex>
    """

    SALT_PREFIX = "keygate:device:v1:"

    @classmethod
    def get_canonical_platform(cls) -> str:
        """Returns the canonical platform string: darwin, windows, linux, or other."""
        system = platform.system().lower()
        if "darwin" in system:
            return "darwin"
        elif "windows" in system:
            return "windows"
        elif "linux" in system:
            return "linux"
        return "other"

    @classmethod
    def compute_fingerprint(cls, identifier: str, product_id: str) -> str:
        """
        Computes the 16-hex character token binding fingerprint:
        hex(sha256(identifier + ":" + product_id)[:8])
        """
        h = hashlib.sha256(f"{identifier}:{product_id}".encode("utf-8")).digest()
        return h[:8].hex()

    @classmethod
    def collect_raw_identifiers(cls) -> list[tuple[str, str]]:
        """
        Collects stable operating system identifiers subject to platform availability and permissions.
        Returns a list of (component_tag, raw_value) tuples.
        """
        plat = cls.get_canonical_platform()
        components = []

        if plat == "darwin":
            # macOS: IORegistry IOPlatformUUID
            try:
                out = subprocess.check_output(
                    ["ioreg", "-rd1", "-c", "IOPlatformExpertDevice"],
                    stderr=subprocess.DEVNULL,
                    timeout=2,
                ).decode("utf-8", errors="ignore")
                for line in out.splitlines():
                    if "IOPlatformUUID" in line:
                        parts = line.split("=")
                        if len(parts) >= 2:
                            val = parts[1].strip().strip('"')
                            if val:
                                components.append(("darwin_ioreg_uuid", val))
                                break
            except Exception:
                pass

        elif plat == "windows":
            # Windows: MachineGuid from Registry or WMIC/PowerShell UUID
            try:
                import winreg  # type: ignore
                with winreg.OpenKey(
                    winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Cryptography"
                ) as key:
                    guid, _ = winreg.QueryValueEx(key, "MachineGuid")
                    if guid:
                        components.append(("win_machine_guid", str(guid).strip()))
            except Exception:
                pass

        elif plat == "linux":
            # Linux: /etc/machine-id or /var/lib/dbus/machine-id
            for path in ("/etc/machine-id", "/var/lib/dbus/machine-id"):
                if os.path.exists(path) and os.path.isfile(path):
                    try:
                        with open(path, "r", encoding="utf-8") as f:
                            val = f.read().strip()
                            if val and len(val) >= 16:
                                components.append(("linux_machine_id", val))
                                break
                    except Exception:
                        pass

        # Stable machine architecture & node name as fallback entropy
        try:
            node = platform.node().strip()
            if node:
                components.append(("hostname", node))
        except Exception:
            pass

        return components

    @classmethod
    def get_or_create_device_id(cls, storage_dir: Optional[str] = None) -> str:
        """
        Generates or retrieves a stable, canonical device identifier.
        If stable OS components are available, returns `kgid:v1:<platform>:<64hex>`.
        Otherwise, generates and persists a synthetic fallback UUIDv4 in `storage_dir`.
        """
        plat = cls.get_canonical_platform()
        components = cls.collect_raw_identifiers()

        if components:
            # Sort components lexicographically for determinism
            components.sort(key=lambda x: x[0])
            h = hashlib.sha256()
            h.update(f"{cls.SALT_PREFIX}{plat}:".encode("utf-8"))
            for tag, val in components:
                h.update(f"{tag}={val.strip()};".encode("utf-8"))
            digest = h.hexdigest()
            return f"kgid:v1:{plat}:{digest}"

        # Fallback to local persisted UUID
        if not storage_dir:
            if plat == "windows":
                storage_dir = os.environ.get("APPDATA", os.path.expanduser("~"))
            elif plat == "darwin":
                storage_dir = os.path.expanduser("~/Library/Application Support/Keygate")
            else:
                storage_dir = os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share/keygate"))

        os.makedirs(storage_dir, exist_ok=True)
        id_file = os.path.join(storage_dir, ".keygate_device_id")

        if os.path.exists(id_file):
            try:
                with open(id_file, "r", encoding="utf-8") as f:
                    cached = f.read().strip()
                    if cached:
                        return cached
            except Exception:
                pass

        new_id = str(uuid.uuid4())
        try:
            with open(id_file, "w", encoding="utf-8") as f:
                f.write(new_id)
        except Exception:
            pass

        return new_id
