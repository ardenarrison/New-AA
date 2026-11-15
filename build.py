"""Build the New AA mod and assemble the Package folder.

    python build.py             build the C# mod and assemble Package\\
    python build.py --native    also rebuild Native\\NewAANgx.dll (needs Visual Studio C++ tools and the Vulkan SDK)
    python build.py --clean     delete bin\\ and obj\\ first

Package\\ is a complete, drop-in mod folder: newaa.dll, mod.toml, NewAAAssets.xml, Shaders\\, Textures\\, NewAANgx.dll and the
DLSS runtime nvngx_dlss.dll. python deploy.py installs it into the game. This script never touches the game folders.

Run python setup.py first on a new machine.
"""
from __future__ import annotations

import argparse
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Optional

import newaa_common as common
from newaa_common import die, fail, heading, note, ok, say, warn

NATIVE_EXPORTS = (b"NewAA_NgxInit", b"NewAA_DlaaSupported", b"NewAA_DlaaStatus", b"NewAA_DlaaCreate",
                  b"NewAA_DlaaEvaluate", b"NewAA_DlaaRelease")


def clean() -> None:
    heading("Clean")
    for name in ("bin", "obj"):
        folder = common.ROOT / name
        if folder.is_dir():
            shutil.rmtree(folder)
            ok(f"removed {name}\\")


def build_native() -> None:
    """Compile Native/newaa_ngx.cpp to NewAANgx.dll with MSVC, against NVIDIA's DLSS SDK."""
    heading("Native plugin (NewAANgx.dll)")
    vcvars = common.find_vcvars()
    vulkan = common.find_vulkan_include()
    problems = []
    if not vcvars:
        problems.append("Visual Studio with the 'Desktop development with C++' workload (for cl.exe)")
    if not vulkan:
        problems.append("the Vulkan SDK, for the Vulkan headers: https://vulkan.lunarg.com/sdk/home")
    if not common.SDK_LIB.is_file() or not (common.SDK_INCLUDE / "nvsdk_ngx_vk.h").is_file():
        problems.append(f"NVIDIA's DLSS SDK in {common.SDK_DIR.relative_to(common.ROOT)}\\ (download it from {common.SDK_URL};"
                        " it needs include\\ and lib\\Windows_x86_64\\x64\\)")
    if problems:
        die("Cannot build the native plugin. Missing: " + "; ".join(problems))

    source = common.NATIVE_DIR / "newaa_ngx.cpp"
    work = Path(tempfile.mkdtemp(prefix="newaa_native_"))
    try:
        # A batch file, so the Visual Studio environment and the compiler run in one cmd session without quoting trouble.
        batch = work / "build.bat"
        batch.write_text(
            "@echo off\r\n"
            f'call "{vcvars}" >nul\r\n'
            "if errorlevel 1 exit /b 1\r\n"
            f'cd /d "{work}"\r\n'
            f'cl /nologo /LD /EHsc /MD /O2 /std:c++17 /I "{common.SDK_INCLUDE}" /I "{vulkan}" '
            f'"{source}" /Fe:NewAANgx.dll /link "{common.SDK_LIB}" advapi32.lib user32.lib\r\n'
            "exit /b %errorlevel%\r\n",
            encoding="ascii")
        note("Compiling with cl.exe ...")
        result = common.run(["cmd", "/c", str(batch)], capture=True)
        built = work / "NewAANgx.dll"
        if result.returncode != 0 or not built.is_file():
            say(result.stdout)
            say(result.stderr)
            die("The native plugin did not build. The compiler output is above.")
        data = built.read_bytes()
        missing = [name.decode() for name in NATIVE_EXPORTS if name not in data]
        if missing:
            die("The plugin built but is missing exports the mod calls: " + ", ".join(missing))
        shutil.copy2(built, common.NATIVE_DLL)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    ok(f"Built {common.NATIVE_DLL.relative_to(common.ROOT)} ({common.human(common.NATIVE_DLL.stat().st_size)})")


def build_mod(ksa_dir: Path) -> None:
    heading("C# mod (newaa.dll)")
    command = [
        "dotnet", "build", str(common.CSPROJ), "-c", "Release", "-nologo", "-v:minimal",
        f"-p:KsaInstallDir={common.msbuild_dir(ksa_dir)}",
        "-p:DeployMod=false",  # deploying is deploy.py's job
    ]
    note("dotnet build -c Release ...")
    result = common.run(command)
    if result.returncode != 0:
        die("dotnet build failed. The errors are above. If they name missing game types or DLLs, run python setup.py to check the"
            " game install against the project's references.")
    if not common.BUILT_DLL.is_file():
        die(f"The build reported success but {common.BUILT_DLL.relative_to(common.ROOT)} is not there.")
    ok(f"Built {common.BUILT_DLL.relative_to(common.ROOT)} ({common.human(common.BUILT_DLL.stat().st_size)})")


def assemble_package() -> None:
    heading("Package")
    package = common.PACKAGE_DIR
    if package.parent.resolve() != common.ROOT:  # never delete anything but the project's own Package folder
        die(f"Refusing to clear {package}")
    if package.exists():
        shutil.rmtree(package)
    package.mkdir()

    shutil.copy2(common.BUILT_DLL, package / "newaa.dll")
    shutil.copy2(common.ROOT / "mod.toml", package / "mod.toml")
    content = common.ROOT / "Content"
    for relative in common.files_under(content):  # Shaders\, Textures\ and the asset XML, at the package root
        target = package / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(content / relative, target)

    if common.NATIVE_DLL.is_file():
        shutil.copy2(common.NATIVE_DLL, package / common.NATIVE_DLL.name)
    else:
        warn("Native/NewAANgx.dll is missing, so DLAA will not work. Run  python build.py --native")
    if common.DLSS_RUNTIME.is_file():
        shutil.copy2(common.DLSS_RUNTIME, package / common.DLSS_RUNTIME.name)
    else:
        warn("DLSS_SDK/.../nvngx_dlss.dll is missing, so DLAA will not start. Copy it from the DLSS SDK into the mod folder.")

    problems = common.check_package(package)
    if problems:
        for problem in problems:
            fail(problem)
        die("The package is incomplete.")

    files = common.files_under(package)
    total = sum((package / f).stat().st_size for f in files)
    ok(f"{len(files)} files, {common.human(total)} in {package.relative_to(common.ROOT)}\\")
    for relative in files:
        if relative.parent == Path("."):
            say(f"         {relative}  ({common.human((package / relative).stat().st_size)})")
    shaders = sum(1 for f in files if f.parts[0] == "Shaders")
    textures = sum(1 for f in files if f.parts[0] == "Textures")
    say(f"         Shaders\\ ({shaders} files), Textures\\ ({textures} files)")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the New AA mod and assemble the Package folder.")
    parser.add_argument("--native", action="store_true", help="rebuild Native/NewAANgx.dll too (needs MSVC and the Vulkan SDK)")
    parser.add_argument("--clean", action="store_true", help="delete bin/ and obj/ before building")
    parser.add_argument("--ksa-dir", help="the game install folder (default: the one setup.py saved, or the usual locations)")
    args = parser.parse_args()

    config = common.load_config()
    heading("Checks")
    major = common.newest_dotnet_major()
    if major is None or major < common.MIN_DOTNET_MAJOR:
        die(f"The .NET {common.MIN_DOTNET_MAJOR} SDK is required. Run python setup.py for details.")
    ok(f".NET SDK {major}")

    ksa_dir: Optional[Path] = common.resolve_ksa_dir(args.ksa_dir, config)
    if ksa_dir is None or not common.is_ksa_install(ksa_dir):
        die("The game install was not found. Run python setup.py, or pass  --ksa-dir \"<folder that contains KSA.dll>\"")
    ok(f"Game install: {ksa_dir}")
    missing = [name for name in common.required_game_dlls() if not (ksa_dir / name).is_file()]
    if missing:
        die("The game folder is missing DLLs the project references: " + ", ".join(missing[:6]) +
            (" ..." if len(missing) > 6 else "") + "  (a game update may have renamed them; run python setup.py)")

    if args.clean:
        clean()
    if args.native or not common.NATIVE_DLL.is_file():
        if not args.native:
            note("Native/NewAANgx.dll is missing. Building it.")
        build_native()
    build_mod(ksa_dir)
    assemble_package()

    say()
    ok("Build complete. Install it with:  python deploy.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
