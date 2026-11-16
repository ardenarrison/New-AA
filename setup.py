"""Set up the New AA project on a new machine.

    python setup.py                      check everything, ask where the game is if it is not found, restore packages
    python setup.py --ksa-dir "D:\\Games\\KSA"
    python setup.py --yes                never ask, use what was found (for scripts)

Checks the .NET SDK, the game install (and that every game DLL the project references is there), StarMap, NVIDIA's
DLSS SDK, the native plugin and its optional toolchain, AMD's FSR runtime (and offers to download it), and the GPU. Restores the
NuGet packages and saves the paths it
found to newaa.local.json, which build.py and deploy.py read. Safe to run again.

Then:  python build.py   and   python deploy.py
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path
from typing import List, Optional

import newaa_common as common
from newaa_common import fail, heading, note, ok, say, warn


def check_python() -> bool:
    heading("Python")
    version = sys.version_info
    if version < (3, 8):
        fail(f"Python {version.major}.{version.minor} is too old. Use 3.8 or newer.")
        return False
    ok(f"Python {version.major}.{version.minor}.{version.micro}")
    if os.name != "nt":
        warn("This project targets Windows. The game, the DLSS runtime and the MSVC toolchain are Windows only.")
    return True


def check_dotnet() -> bool:
    heading(".NET SDK")
    major = common.newest_dotnet_major()
    if major is None:
        fail(".NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0")
        return False
    if major < common.MIN_DOTNET_MAJOR:
        fail(f"The newest .NET SDK is {major}. The game and this project target net10.0 (C# 14), so SDK 10 or newer is needed:"
             " https://dotnet.microsoft.com/download/dotnet/10.0")
        return False
    ok("SDKs installed: " + ", ".join(common.dotnet_sdk_versions()))
    return True


def check_game(cli_value: Optional[str], config: dict, assume_yes: bool) -> Optional[Path]:
    """Find the game install and confirm every DLL the project references is in it. Returns the folder, or None."""
    heading("Kitten Space Agency")
    folder = common.resolve_ksa_dir(cli_value, config)
    if folder is not None and not common.is_ksa_install(folder):
        warn(f"No KSA.dll in {folder}")
        folder = None
    if folder is None and not assume_yes:
        say("The game folder (the one that contains KSA.dll) was not found in the usual places.")
        typed = common.ask_text("Path to the Kitten Space Agency install folder")
        if typed:
            candidate = Path(typed)
            if common.is_ksa_install(candidate):
                folder = candidate
            else:
                warn(f"No KSA.dll in {candidate}")
    if folder is None:
        fail("Game install not found. Run again with  --ksa-dir \"<folder that contains KSA.dll>\"")
        return None
    ok(f"Game install: {folder}")

    missing = [name for name in common.required_game_dlls() if not (folder / name).is_file()]
    if missing:
        fail(f"{len(missing)} DLL(s) the project references are not in the game folder:")
        for name in missing:
            say(f"         {name}")
        say("       A game update probably renamed or moved them. Compare with the DLLs in the game folder and update the")
        say("       <Reference> entries in NewAA.csproj. (The 0.x -> 2026.11 update renamed Brutal.Core.* to Brutal.*.)")
        return None
    ok(f"All {len(common.required_game_dlls())} game DLLs the project references are present")
    return folder


def find_starmap() -> List[Path]:
    """StarMap launches the game with mods. Look for a StarMap.exe in the places people unpack it."""
    roots = [Path.home()]
    for variable in ("ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"):
        if os.environ.get(variable):
            roots.append(Path(os.environ[variable]))
    env = os.environ.get("STARMAP_DIR")
    found: List[Path] = []
    if env and (Path(env) / "StarMap.exe").is_file():
        found.append(Path(env))
    for root in roots:
        if not root.is_dir():
            continue
        try:
            for child in root.iterdir():
                if child.is_dir() and child.name.lower().startswith("starmap") and (child / "StarMap.exe").is_file():
                    found.append(child)
        except OSError:
            continue
    return sorted(set(found), reverse=True)


def check_starmap() -> None:
    heading("StarMap")
    found = find_starmap()
    if found:
        ok("StarMap found: " + ", ".join(str(p) for p in found))
        note("Start the game through StarMap.exe so it loads the mod.")
    else:
        warn("StarMap was not found. The mod is loaded by StarMap, so it is needed to play with the mod, though not to build it.")
        say("       Download it and unpack it anywhere (for example your user folder): https://github.com/StarMapLoader/StarMap")


def check_dlss_sdk() -> bool:
    """NVIDIA's DLSS SDK. Returns True when everything the project uses from it is there."""
    heading("NVIDIA DLSS SDK")
    parts = [
        (common.DLSS_RUNTIME, "the DLSS runtime, copied into the mod"),
        (common.SDK_LIB, "static library, to rebuild the native plugin"),
        (common.SDK_INCLUDE / "nvsdk_ngx_vk.h", "NGX headers, to rebuild the native plugin"),
    ]
    complete = True
    for path, what in parts:
        if path.is_file():
            ok(f"{path.relative_to(common.ROOT)}  ({what})")
        else:
            complete = False
            warn(f"missing {path.relative_to(common.ROOT)}  ({what})")
    if not complete:
        say(f"       Download NVIDIA's DLSS SDK from {common.SDK_URL} and put it in {common.SDK_DIR.name}\\ here.")
        say("       NVIDIA's license does not allow redistributing the SDK, so it is not part of this project. Only the three paths")
        say("       above are used, so a trimmed copy is enough. SMAA and FXAA do not need it at all.")
    if not common.DLSS_RUNTIME.is_file():
        warn("Without nvngx_dlss.dll in the package, DLAA will not start in the game.")
    return complete


def check_fsr_runtime(assume_yes: bool) -> None:
    """AMD's FSR runtime (two signed DLLs, MIT licensed), downloaded from AMD's repository at the pinned release."""
    heading(f"AMD FSR runtime ({common.FSR_RELEASE})")
    problems = common.fsr_runtime_problems()
    if problems:
        for problem in problems:
            warn(problem)
        if common.ask_yes_no(f"Download them (about 36 MB) from {common.FSR_URL} ?", True, assume_yes):
            problems = common.download_fsr_runtime()
    if problems:
        for problem in problems:
            warn(problem)
        warn("Without these files FSR Native AA will not start. build.py tries the download again.")
        return
    for name in common.FSR_RUNTIME:
        ok(f"{(common.FSR_DIR / name).relative_to(common.ROOT)}  (SHA-256 matches {common.FSR_RELEASE})")


def check_native(sdk_complete: bool) -> None:
    heading("Native plugin (NewAANgx.dll)")
    if common.NATIVE_DLL.is_file():
        ok(f"Prebuilt plugin present: {common.NATIVE_DLL.relative_to(common.ROOT)} ({common.human(common.NATIVE_DLL.stat().st_size)})")
    else:
        warn("Native/NewAANgx.dll is missing, so build.py has to compile it (needs Visual Studio C++ tools and the Vulkan SDK).")
    vcvars = common.find_vcvars()
    vulkan = common.find_vulkan_include()
    if vcvars:
        ok(f"MSVC (cl.exe) found: {vcvars}")
    else:
        note("MSVC not found. Only needed for  python build.py --native  (rebuilding the plugin).")
        say("       Install Visual Studio with the 'Desktop development with C++' workload.")
    if vulkan:
        ok(f"Vulkan headers found: {vulkan}")
    else:
        note("Vulkan SDK not found. Only needed for  python build.py --native.  https://vulkan.lunarg.com/sdk/home")
    if not common.NATIVE_DLL.is_file() and not (vcvars and vulkan and sdk_complete):
        warn("The plugin cannot be built until MSVC, the Vulkan headers and the DLSS SDK are all in place. DLAA needs the plugin;"
             " SMAA and FXAA do not.")


def check_gpu() -> None:
    heading("GPU (to run DLAA, not to build)")
    import shutil
    if not shutil.which("nvidia-smi"):
        warn("nvidia-smi not found. DLAA needs an NVIDIA RTX GPU (RTX 20-series or newer) and its driver."
             " FSR Native AA, SMAA and FXAA work on other GPUs too.")
        return
    try:
        result = common.run(["nvidia-smi", "--query-gpu=name,compute_cap,driver_version", "--format=csv,noheader"], capture=True)
    except OSError:
        warn("nvidia-smi could not be run.")
        return
    lines = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    if not lines:
        warn("nvidia-smi reported no GPU.")
        return
    name, capability, driver = (part.strip() for part in (lines[0].split(",") + ["", "", ""])[:3])
    try:
        capable = "RTX" in name and float(capability) >= 7.5
    except ValueError:
        capable = False
    message = f"{name}, compute capability {capability}, driver {driver}"
    if capable:
        ok(message + "  (supports DLAA)")
    else:
        warn(message + "  (DLAA needs an RTX GPU with compute capability 7.5 or newer. The mod falls back to no anti-aliasing.)")
    if len(lines) > 1:
        note("More than one GPU listed. The mod checks only the first one that nvidia-smi lists.")


def restore_packages() -> bool:
    heading("NuGet packages")
    note("Restoring (Lib.Harmony and StarMap.API are downloaded on first use, so this needs an internet connection)...")
    result = common.run(["dotnet", "restore", str(common.CSPROJ), "-nologo", "-v:minimal"])
    if result.returncode != 0:
        fail("dotnet restore failed. Check the output above and your internet connection.")
        return False
    ok("Packages restored")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Set up the New AA project: check prerequisites, restore packages, save paths.")
    parser.add_argument("--ksa-dir", help="the Kitten Space Agency install folder (the one that contains KSA.dll)")
    parser.add_argument("--mod-dir", help="where the mod is installed (default: Documents\\My Games\\Kitten Space Agency\\mods\\newaa)")
    parser.add_argument("--yes", "-y", action="store_true", help="never ask questions; use what was found")
    parser.add_argument("--skip-restore", action="store_true", help="do not run dotnet restore")
    args = parser.parse_args()

    say("New AA setup")
    say(f"Project folder: {common.ROOT}")

    blocking = []
    config = common.load_config()

    if not check_python():
        blocking.append("python")
    dotnet_ok = check_dotnet()
    if not dotnet_ok:
        blocking.append(".NET SDK")

    game = check_game(args.ksa_dir, config, args.yes)
    if game is None:
        blocking.append("game install")

    check_starmap()
    sdk_complete = check_dlss_sdk()
    check_native(sdk_complete)
    check_fsr_runtime(args.yes)
    check_gpu()

    mod_dir = common.resolve_mod_dir(args.mod_dir, config)
    heading("Install location")
    ok(f"The mod will be deployed to: {mod_dir}")
    if mod_dir.is_dir():
        note("That folder already exists (a previous install).")

    if dotnet_ok and not args.skip_restore:
        if not restore_packages():
            blocking.append("package restore")

    if game is not None:
        common.save_config({"ksa_install_dir": str(game), "mod_dir": str(mod_dir)})
        heading("Saved")
        ok(f"Paths written to {common.CONFIG_PATH.name} (specific to this PC. Do not share it).")

    say()
    if blocking:
        fail("Setup is not complete. Fix: " + ", ".join(blocking))
        return 1
    ok("Setup complete.")
    say()
    say("Next:")
    say("  python build.py     build the mod and assemble the Package folder")
    say("  python deploy.py    install it into the game and enable it")
    return 0


if __name__ == "__main__":
    sys.exit(main())
