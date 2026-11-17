"""Build the New AA mod and assemble the Package folder.

    python build.py             build the C# mod and assemble Package\\
    python build.py --native    also rebuild Native\\NewAANgx.dll (needs Visual Studio C++ tools and the Vulkan SDK)
    python build.py --linux     also assemble Package-linux\\, the experimental Linux build (see below)
    python build.py --clean     delete bin\\ and obj\\ first
    python build.py --no-download   do not download AMD's FSR runtime when FSR_SDK\\ lacks it

Package\\ is a complete, drop-in mod folder: newaa.dll, mod.toml, NewAAAssets.xml, Shaders\\, Textures\\, NewAANgx.dll, the
DLSS runtime nvngx_dlss.dll, and AMD's FSR runtime (amd_fidelityfx_loader_vk.dll, amd_fidelityfx_upscaler_vk.dll) with its
license. python deploy.py installs it into the game. This script never touches the game folders.

Package-linux\\ is the same mod for the game's native Linux build: the same newaa.dll and content, Native/libNewAANgx.so and
NVIDIA's Linux DLSS runtime (libnvidia-ngx-dlss.so.<version>), and no FSR runtime (AMD ships it for Windows only).
libNewAANgx.so is cross-compiled with LLVM (clang++ and ld.lld) against a Linux sysroot that Native/make_linux_sysroot.sh makes;
pass it with --linux-sysroot or set NEWAA_LINUX_SYSROOT. --native rebuilds it too.

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


def build_native_linux(sysroot: Optional[Path]) -> None:
    """Cross-compile Native/newaa_ngx.cpp to libNewAANgx.so for x86-64 Linux with clang++ and ld.lld, against NVIDIA's DLSS SDK.
    libstdc++ and libgcc are linked in and their symbols hidden, so the plugin only needs glibc and libdl from the system."""
    heading("Native plugin for Linux (libNewAANgx.so)")
    clang = common.find_clang()
    vulkan = common.find_vulkan_include()
    problems = []
    if not clang:
        problems.append("LLVM (clang++ and ld.lld): https://github.com/llvm/llvm-project/releases")
    if not vulkan:
        problems.append("the Vulkan SDK, for the Vulkan headers: https://vulkan.lunarg.com/sdk/home")
    if sysroot is None or not common.is_linux_sysroot(sysroot):
        problems.append("a Linux sysroot: make one with Native/make_linux_sysroot.sh and pass it with --linux-sysroot or "
                        f"{common.LINUX_SYSROOT_ENV}" + (f" ({sysroot} is not one)" if sysroot else ""))
    if not common.SDK_LIB_LINUX.is_file() or not (common.SDK_INCLUDE / "nvsdk_ngx_vk.h").is_file():
        problems.append(f"NVIDIA's DLSS SDK in {common.SDK_DIR.relative_to(common.ROOT)}\\ (download it from {common.SDK_URL};"
                        " it needs include\\ and lib\\Linux_x86_64\\)")
    if problems:
        die("Cannot build the Linux plugin. Missing: " + "; ".join(problems))

    work = Path(tempfile.mkdtemp(prefix="newaa_native_linux_"))
    try:
        built = work / common.NATIVE_SO.name
        command = [
            str(clang), "--target=x86_64-linux-gnu", f"--sysroot={sysroot}", "-fuse-ld=lld",
            "-shared", "-fPIC", "-O2", "-std=c++17", "-fvisibility=hidden", "-Wno-missing-braces",
            "-I", str(common.SDK_INCLUDE), "-I", str(vulkan),
            str(common.NATIVE_DIR / "newaa_ngx.cpp"), str(common.SDK_LIB_LINUX),
            "-static-libstdc++", "-static-libgcc", "-ldl",
            "-Wl,--exclude-libs,ALL", "-Wl,--no-undefined", f"-Wl,-soname,{common.NATIVE_SO.name}",
            "-o", str(built),
        ]
        note("Compiling with clang++ --target=x86_64-linux-gnu ...")
        result = common.run(command, capture=True)
        if result.returncode != 0 or not built.is_file():
            say(result.stdout)
            say(result.stderr)
            die("The Linux plugin did not build. The compiler output is above.")
        data = built.read_bytes()
        if data[:4] != b"\x7fELF":
            die("The Linux plugin built, but it is not an ELF file.")
        missing = [name.decode() for name in NATIVE_EXPORTS if name not in data]
        if missing:
            die("The Linux plugin built but is missing exports the mod calls: " + ", ".join(missing))
        shutil.copy2(built, common.NATIVE_SO)
    finally:
        shutil.rmtree(work, ignore_errors=True)
    ok(f"Built {common.NATIVE_SO.relative_to(common.ROOT)} ({common.human(common.NATIVE_SO.stat().st_size)})")


def ensure_fsr_runtime(download: bool) -> None:
    """AMD's FSR runtime in FSR_SDK\\, downloaded from AMD's repository at the pinned release when it is missing."""
    heading(f"AMD FSR runtime ({common.FSR_RELEASE})")
    problems = common.fsr_runtime_problems()
    if problems and download:
        problems = common.download_fsr_runtime()
    if problems:
        for problem in problems:
            warn(problem)
        warn("FSR Native AA will not work in this build. Run  python build.py  again with an internet connection.")
        return
    ok(f"{', '.join(common.FSR_RUNTIME)} in {common.FSR_DIR.relative_to(common.ROOT)}\\ (SHA-256 checked)")


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


def assemble_package(linux: bool = False) -> None:
    package = common.PACKAGE_LINUX_DIR if linux else common.PACKAGE_DIR
    heading(f"Package ({'Linux, experimental' if linux else 'Windows'})")
    if package.parent.resolve() != common.ROOT:  # never delete anything but the project's own package folders
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

    if linux:
        if common.NATIVE_SO.is_file():
            shutil.copy2(common.NATIVE_SO, package / common.NATIVE_SO.name)
        else:
            warn("Native/libNewAANgx.so is missing, so DLAA will not work. Run  python build.py --linux --native")
        runtime = common.dlss_runtime_linux()
        if runtime:
            shutil.copy2(runtime, package / runtime.name)
        else:
            warn("DLSS_SDK/lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.<version> is missing, so DLAA will not start.")
    else:
        if common.NATIVE_DLL.is_file():
            shutil.copy2(common.NATIVE_DLL, package / common.NATIVE_DLL.name)
        else:
            warn("Native/NewAANgx.dll is missing, so DLAA will not work. Run  python build.py --native")
        if common.DLSS_RUNTIME.is_file():
            shutil.copy2(common.DLSS_RUNTIME, package / common.DLSS_RUNTIME.name)
        else:
            warn("DLSS_SDK/.../nvngx_dlss.dll is missing, so DLAA will not start. Copy it from the DLSS SDK into the mod folder.")
        if not common.fsr_runtime_problems():
            for name in common.FSR_RUNTIME:
                shutil.copy2(common.FSR_DIR / name, package / name)
            shutil.copy2(common.FSR_LICENSE, package / common.FSR_LICENSE.name)
        else:
            warn("AMD's FSR runtime is missing from FSR_SDK/, so FSR Native AA will not start.")

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
    parser.add_argument("--native", action="store_true", help="rebuild Native/NewAANgx.dll too (needs MSVC and the Vulkan SDK);"
                        " with --linux also Native/libNewAANgx.so")
    parser.add_argument("--linux", action="store_true", help="also assemble Package-linux/, the experimental Linux build")
    parser.add_argument("--linux-sysroot", help=f"the Linux sysroot for libNewAANgx.so (default: {common.LINUX_SYSROOT_ENV} or the"
                        " saved config)")
    parser.add_argument("--clean", action="store_true", help="delete bin/ and obj/ before building")
    parser.add_argument("--no-download", action="store_true", help="do not download AMD's FSR runtime when FSR_SDK/ lacks it")
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
    if args.linux and (args.native or not common.NATIVE_SO.is_file()):
        if not args.native:
            note("Native/libNewAANgx.so is missing. Building it.")
        build_native_linux(common.resolve_linux_sysroot(args.linux_sysroot, config))
    ensure_fsr_runtime(not args.no_download)
    build_mod(ksa_dir)
    assemble_package()
    if args.linux:
        assemble_package(linux=True)

    say()
    ok("Build complete. Install it with:  python deploy.py")
    if args.linux:
        note(f"{common.PACKAGE_LINUX_DIR.name}\\ is the experimental Linux build. deploy.py installs the Windows one only.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
