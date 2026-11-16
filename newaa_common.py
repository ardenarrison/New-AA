"""Shared helpers for setup.py, build.py and deploy.py. Not meant to be run directly.

Windows only (the game, the DLSS runtime and the MSVC toolchain are Windows only). Standard library only.
"""
from __future__ import annotations

import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Iterable, List, Optional

ROOT = Path(__file__).resolve().parent

MOD_ID = "newaa"
CSPROJ = ROOT / "NewAA.csproj"
CONFIG_PATH = ROOT / "newaa.local.json"
PACKAGE_DIR = ROOT / "Package"
NATIVE_DIR = ROOT / "Native"
NATIVE_DLL = NATIVE_DIR / "NewAANgx.dll"
# NVIDIA's DLSS SDK. Its license does not allow redistributing it, so it is not part of the repository: each person downloads it
# from NVIDIA and puts it here. Only the three paths below are used, so a trimmed copy works.
SDK_DIR = ROOT / "DLSS_SDK"
SDK_URL = "https://github.com/NVIDIA/DLSS"
SDK_INCLUDE = SDK_DIR / "include"
SDK_LIB = SDK_DIR / "lib" / "Windows_x86_64" / "x64" / "nvsdk_ngx_d.lib"
DLSS_RUNTIME = SDK_DIR / "lib" / "Windows_x86_64" / "rel" / "nvngx_dlss.dll"
BUILT_DLL = ROOT / "bin" / "Release" / "net10.0" / "newaa.dll"
# AMD's FSR runtime for FSR Native AA: the two signed Vulkan DLLs from the FSR SDK. They are MIT licensed and may be shipped, but at
# 36 MB they are downloaded from AMD's repository at a fixed release (not stored in this one) and checked against these hashes.
FSR_DIR = ROOT / "FSR_SDK"
FSR_RELEASE = "v2.4.0"
FSR_URL = f"https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/{FSR_RELEASE}/Kits/FidelityFX/signedbin/"
FSR_RUNTIME = {
    "amd_fidelityfx_loader_vk.dll": "38a56a01fb3565c0757d9f5c1e4e69de4b65e5f2e5769b323b200dc1f142bab9",
    "amd_fidelityfx_upscaler_vk.dll": "4a7e895af6e0b615d40a84d59eb8c2b1054ad91cdd0c739567940270d51bdab7",
}
FSR_LICENSE = ROOT / "ThirdParty" / "AMD_FSR_SDK_LICENSE.txt"

# The game processes that keep the mod's DLLs loaded. Deploying while one is running fails on locked files.
GAME_PROCESSES = ("ksa.exe", "starmap.exe")

MIN_DOTNET_MAJOR = 10


# --------------------------------------------------------------------------------------------------
# Console output. Plain ASCII, so it works in any Windows console code page.
# --------------------------------------------------------------------------------------------------

def _configure_console() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(errors="replace")  # type: ignore[attr-defined]
        except Exception:
            pass


_configure_console()


def say(text: str = "") -> None:
    print(text, flush=True)


def ok(text: str) -> None:
    say(f"[ ok ] {text}")


def note(text: str) -> None:
    say(f"[ .. ] {text}")


def warn(text: str) -> None:
    say(f"[warn] {text}")


def fail(text: str) -> None:
    say(f"[FAIL] {text}")


def die(text: str, code: int = 1) -> "NoReturn":  # type: ignore[name-defined]
    fail(text)
    sys.exit(code)


def heading(text: str) -> None:
    say()
    say(text)
    say("-" * len(text))


def human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.0f} {unit}" if unit == "B" else f"{value:.1f} {unit}"
        value /= 1024
    return f"{size} B"


def is_interactive() -> bool:
    return sys.stdin is not None and sys.stdin.isatty()


def ask_yes_no(question: str, default: bool, assume_yes: bool) -> bool:
    """Ask a yes/no question. --yes answers yes. With no terminal to ask on, the default is used."""
    if assume_yes:
        return True
    if not is_interactive():
        return default
    suffix = "[Y/n]" if default else "[y/N]"
    answer = input(f"{question} {suffix} ").strip().lower()
    if not answer:
        return default
    return answer in ("y", "yes")


def ask_text(question: str, default: str = "") -> str:
    if not is_interactive():
        return default
    suffix = f" [{default}]" if default else ""
    answer = input(f"{question}{suffix}: ").strip().strip('"')
    return answer or default


# --------------------------------------------------------------------------------------------------
# Folders
# --------------------------------------------------------------------------------------------------

def documents_dir() -> Path:
    """The real Documents folder, which OneDrive may have moved. The csproj uses the same one."""
    if os.name == "nt":
        try:
            class GUID(ctypes.Structure):
                _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort), ("Data3", ctypes.c_ushort),
                            ("Data4", ctypes.c_ubyte * 8)]

            folder_id_documents = GUID(0xFDD39AD0, 0x238F, 0x46AF,
                                       (ctypes.c_ubyte * 8)(0xAD, 0xB4, 0x6C, 0x85, 0x48, 0x03, 0x69, 0xC7))
            path_ptr = ctypes.c_wchar_p()
            result = ctypes.windll.shell32.SHGetKnownFolderPath(ctypes.byref(folder_id_documents), 0, None,
                                                                ctypes.byref(path_ptr))  # type: ignore[attr-defined]
            if result == 0 and path_ptr.value:
                value = path_ptr.value
                ctypes.windll.ole32.CoTaskMemFree(path_ptr)  # type: ignore[attr-defined]
                return Path(value)
        except Exception:
            pass
    return Path.home() / "Documents"


def default_mod_dir() -> Path:
    return documents_dir() / "My Games" / "Kitten Space Agency" / "mods" / MOD_ID


def game_data_dir(mod_dir: Path) -> Path:
    """The game's data folder (holds manifest.toml and the logs). The mod folder is <data>/mods/<id>."""
    return mod_dir.parent.parent


def ksa_install_candidates() -> List[Path]:
    candidates: List[Path] = []
    env = os.environ.get("KSA_INSTALL_DIR")
    if env:
        candidates.append(Path(env))
    for variable in ("ProgramFiles", "ProgramFiles(x86)", "ProgramW6432"):
        base = os.environ.get(variable)
        if base:
            candidates.append(Path(base) / "Kitten Space Agency")
    candidates.append(Path(r"C:\Program Files\Kitten Space Agency"))
    seen = set()
    unique = []
    for candidate in candidates:
        key = str(candidate).lower()
        if key not in seen:
            seen.add(key)
            unique.append(candidate)
    return unique


def is_ksa_install(path: Path) -> bool:
    return (path / "KSA.dll").is_file()


def find_ksa_install() -> Optional[Path]:
    for candidate in ksa_install_candidates():
        if is_ksa_install(candidate):
            return candidate
    return None


def msbuild_dir(path: Path) -> str:
    """The csproj appends file names to KsaInstallDir and KsaModDir directly, so they must end with a backslash."""
    text = str(path)
    return text if text.endswith("\\") else text + "\\"


def required_game_dlls() -> List[str]:
    """Every game DLL NewAA.csproj references. When a game update renames one, the build breaks, so setup checks them."""
    text = CSPROJ.read_text(encoding="utf-8")
    return sorted(set(re.findall(r"<HintPath>\$\(KsaInstallDir\)([^<]+)</HintPath>", text)))


# --------------------------------------------------------------------------------------------------
# Local configuration, written by setup.py
# --------------------------------------------------------------------------------------------------

def load_config() -> dict:
    if CONFIG_PATH.is_file():
        try:
            return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            warn(f"{CONFIG_PATH.name} could not be read. Ignoring it. Run setup.py to write it again.")
    return {}


def save_config(config: dict) -> None:
    CONFIG_PATH.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")


def resolve_ksa_dir(cli_value: Optional[str], config: dict) -> Optional[Path]:
    """--ksa-dir, then the saved config, then the usual install locations."""
    if cli_value:
        return Path(cli_value.strip('"'))
    if config.get("ksa_install_dir"):
        return Path(config["ksa_install_dir"])
    return find_ksa_install()


def resolve_mod_dir(cli_value: Optional[str], config: dict) -> Path:
    if cli_value:
        return Path(cli_value.strip('"'))
    if config.get("mod_dir"):
        return Path(config["mod_dir"])
    return default_mod_dir()


# --------------------------------------------------------------------------------------------------
# Running tools
# --------------------------------------------------------------------------------------------------

def run(command: List[str], cwd: Optional[Path] = None, capture: bool = False,
        check: bool = False) -> subprocess.CompletedProcess:
    return subprocess.run(command, cwd=str(cwd or ROOT), capture_output=capture, text=True, check=check)


def dotnet_sdk_versions() -> List[str]:
    if not shutil.which("dotnet"):
        return []
    try:
        result = run(["dotnet", "--list-sdks"], capture=True)
    except OSError:
        return []
    return [line.split()[0] for line in result.stdout.splitlines() if line.strip()]


def newest_dotnet_major() -> Optional[int]:
    majors = []
    for version in dotnet_sdk_versions():
        match = re.match(r"(\d+)\.", version)
        if match:
            majors.append(int(match.group(1)))
    return max(majors) if majors else None


def running_game_processes() -> List[str]:
    """Names of KSA / StarMap processes that are running, which lock the mod's DLLs."""
    try:
        result = run(["tasklist", "/FO", "CSV", "/NH"], capture=True)
    except OSError:
        return []
    running = set()
    for line in result.stdout.splitlines():
        parts = line.split('","')
        if parts:
            name = parts[0].strip('"').lower()
            if name in GAME_PROCESSES:
                running.add(name)
    return sorted(running)


# --------------------------------------------------------------------------------------------------
# Native toolchain (only needed to rebuild Native/NewAANgx.dll)
# --------------------------------------------------------------------------------------------------

def find_vcvars() -> Optional[Path]:
    """vcvars64.bat from a Visual Studio with the C++ tools, which sets up the cl.exe environment."""
    vswhere_candidates = []
    for variable in ("ProgramFiles(x86)", "ProgramFiles"):
        base = os.environ.get(variable)
        if base:
            vswhere_candidates.append(Path(base) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe")
    for vswhere in vswhere_candidates:
        if vswhere.is_file():
            try:
                result = run([str(vswhere), "-latest", "-products", "*", "-requires",
                              "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                             capture=True)
            except OSError:
                continue
            install = result.stdout.strip().splitlines()
            if install:
                vcvars = Path(install[0].strip()) / "VC" / "Auxiliary" / "Build" / "vcvars64.bat"
                if vcvars.is_file():
                    return vcvars
    # No vswhere (or it found nothing): look in the usual places, newest version first.
    for variable in ("ProgramFiles", "ProgramFiles(x86)"):
        base = os.environ.get(variable)
        if not base:
            continue
        pattern = Path(base) / "Microsoft Visual Studio"
        if pattern.is_dir():
            matches = sorted(pattern.glob("*/*/VC/Auxiliary/Build/vcvars64.bat"), reverse=True)
            if matches:
                return matches[0]
    return None


def find_vulkan_include() -> Optional[Path]:
    """The Vulkan SDK Include folder (only the headers are used)."""
    sdk = os.environ.get("VULKAN_SDK")
    if sdk and (Path(sdk) / "Include" / "vulkan" / "vulkan.h").is_file():
        return Path(sdk) / "Include"
    base = Path(r"C:\VulkanSDK")
    if base.is_dir():
        for version in sorted((p for p in base.iterdir() if p.is_dir()), reverse=True):
            if (version / "Include" / "vulkan" / "vulkan.h").is_file():
                return version / "Include"
    return None


# --------------------------------------------------------------------------------------------------
# Files
# --------------------------------------------------------------------------------------------------

def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def same_file(a: Path, b: Path) -> bool:
    if not (a.is_file() and b.is_file()):
        return False
    if a.stat().st_size != b.stat().st_size:
        return False
    return sha256(a) == sha256(b)


def fsr_runtime_problems() -> List[str]:
    """The FSR runtime DLLs in FSR_SDK/ that are missing or do not match the pinned hashes."""
    problems: List[str] = []
    for name, digest in FSR_RUNTIME.items():
        path = FSR_DIR / name
        if not path.is_file():
            problems.append(f"{name} is missing")
        elif sha256(path) != digest:
            problems.append(f"{name} does not match the {FSR_RELEASE} release (SHA-256 differs)")
    return problems


def download_fsr_runtime() -> List[str]:
    """Download the FSR runtime DLLs from AMD's repository into FSR_SDK/. Each file is checked against its pinned hash before it
    replaces anything. Returns the problems (empty when both files are in place)."""
    import urllib.request
    FSR_DIR.mkdir(exist_ok=True)
    problems: List[str] = []
    for name, digest in FSR_RUNTIME.items():
        target = FSR_DIR / name
        if target.is_file() and sha256(target) == digest:
            continue
        partial = target.with_name(name + ".download")
        try:
            note(f"Downloading {name} ({FSR_RELEASE}) ...")
            request = urllib.request.Request(FSR_URL + name, headers={"User-Agent": "newaa-build"})
            with urllib.request.urlopen(request, timeout=120) as response, partial.open("wb") as handle:
                shutil.copyfileobj(response, handle)
        except OSError as error:
            partial.unlink(missing_ok=True)
            problems.append(f"{name}: download failed ({error})")
            continue
        if sha256(partial) != digest:
            partial.unlink(missing_ok=True)
            problems.append(f"{name}: the download does not match the expected SHA-256, so it was discarded")
            continue
        partial.replace(target)
        ok(f"{target.relative_to(ROOT)} ({human(target.stat().st_size)}, SHA-256 checked)")
    return problems


def files_under(folder: Path) -> List[Path]:
    """All files below a folder, as paths relative to it, sorted."""
    return sorted(p.relative_to(folder) for p in folder.rglob("*") if p.is_file())


def asset_paths_in_xml(xml_path: Path) -> List[str]:
    """The Path attributes in an asset XML file, which are relative to the mod folder."""
    return re.findall(r'Path="([^"]+)"', xml_path.read_text(encoding="utf-8"))


def asset_files_in_mod_toml(toml_path: Path) -> List[str]:
    """The XML files listed under assets = [ ... ] in mod.toml."""
    text = toml_path.read_text(encoding="utf-8")
    match = re.search(r"(?ms)^\s*assets\s*=\s*\[(.*?)\]", text)
    return re.findall(r'"([^"]+)"', match.group(1)) if match else []


def check_package(package: Path) -> List[str]:
    """Problems with a package folder: missing required files, or assets that mod.toml and the XML name but are not there."""
    problems: List[str] = []
    for required in ("newaa.dll", "mod.toml"):
        if not (package / required).is_file():
            problems.append(f"missing {required}")
    toml_path = package / "mod.toml"
    if toml_path.is_file():
        for xml_name in asset_files_in_mod_toml(toml_path):
            xml_path = package / xml_name
            if not xml_path.is_file():
                problems.append(f"mod.toml lists {xml_name}, which is not in the package")
                continue
            for asset in asset_paths_in_xml(xml_path):
                if not (package / asset).is_file():
                    problems.append(f"{xml_name} names {asset}, which is not in the package")
    return problems
