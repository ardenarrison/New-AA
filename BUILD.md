# Building and installing the New AA mod

The scripts run on Windows. They build the Windows mod and, with `build.py --linux`, the experimental Linux one. Python 3.8+
(standard library only). Three scripts do everything:

    python setup.py      check the machine, restore packages, save the game paths      (once per PC)
    python build.py      build the mod and assemble Package\
    python deploy.py     install Package\ into the game and enable the mod

## Requirements
- The **.NET 10 SDK** (the game targets net10.0 with C# 14): https://dotnet.microsoft.com/download/dotnet/10.0
- **Kitten Space Agency**, installed (default `C:\Program Files\Kitten Space Agency\`)
- **StarMap**, to play with the mod (not to build it): https://github.com/StarMapLoader/StarMap
- An **NVIDIA RTX GPU (RTX 20-series or newer)** to run DLAA. FSR Native AA runs on NVIDIA, AMD and Intel GPUs that AMD's FSR
  runtime supports. TAA, SMAA and FXAA work on any GPU.
- Only to rebuild the native plugin (`build.py --native`): Visual Studio with the C++ workload, and the Vulkan SDK (headers only).
- Only for the Linux build (`build.py --linux`): LLVM with `clang++` and `ld.lld` (https://github.com/llvm/llvm-project/releases),
  the Vulkan SDK headers, and a Linux sysroot that `Native\make_linux_sysroot.sh` makes (once, in WSL or on Ubuntu or Debian):

      bash Native/make_linux_sysroot.sh /mnt/c/Users/<you>/newaa-sysroot

  Pass it with `--linux-sysroot C:\Users\<you>\newaa-sysroot` or set `NEWAA_LINUX_SYSROOT`. It is glibc 2.31 and GCC 9's libstdc++
  from Ubuntu 20.04, downloaded by apt and checked against the Ubuntu archive's signatures. No root is needed.

- **NVIDIA's DLSS SDK**, for DLAA: download it from https://github.com/NVIDIA/DLSS and put it in a `DLSS_SDK\` folder in this
  project. NVIDIA's license does not allow redistributing the SDK, so it is not stored in the repository. Only these paths are
  used, so a trimmed copy is enough:

      DLSS_SDK\include\                                   NGX headers (to rebuild the native plugins)
      DLSS_SDK\lib\Windows_x86_64\x64\nvsdk_ngx_d.lib     static library (to rebuild NewAANgx.dll)
      DLSS_SDK\lib\Windows_x86_64\rel\nvngx_dlss.dll      the DLSS runtime, copied into the Windows mod
      DLSS_SDK\lib\Linux_x86_64\libnvsdk_ngx.a            static library (to rebuild libNewAANgx.so)
      DLSS_SDK\lib\Linux_x86_64\rel\libnvidia-ngx-dlss.so.<version>    the DLSS runtime, copied into the Linux mod

  Use the same SDK version for both (New AA uses 310.9.1). FSR, TAA, SMAA and FXAA do not need the SDK.

- **AMD's FSR runtime**, for FSR Native AA: `setup.py` (after asking) and `build.py` download AMD's two signed Vulkan DLLs into
  `FSR_SDK\` from the FSR SDK repository at tag `v2.4.0`, and check each against a pinned SHA-256 hash in `newaa_common.py`:

      FSR_SDK\amd_fidelityfx_loader_vk.dll       the FSR API loader the mod calls
      FSR_SDK\amd_fidelityfx_upscaler_vk.dll     the upscalers (FSR 3.1.6, FSR 4 on supported AMD GPUs), loaded by the loader

  They are MIT licensed (`ThirdParty\AMD_FSR_SDK_LICENSE.txt`, copied into the package), so they ship in the mod. They are not stored
  in the repository only because of their size (36 MB). `build.py --no-download` skips the download (FSR then will not start).
  To update FSR, change `FSR_RELEASE` and the hashes in `newaa_common.py`, and check `Source\FsrNative.cs` against the new headers.

## setup.py
    python setup.py
    python setup.py --ksa-dir "D:\Games\KSA"     if the game is not in the usual place
    python setup.py --yes                         never ask questions

Checks the .NET SDK, finds the game and confirms that every game DLL `NewAA.csproj` references is there, looks for StarMap,
checks the DLSS SDK and the native plugin, reports your GPU, runs `dotnet restore`, and saves the paths to
`newaa.local.json` (specific to your PC; do not share it). If a game update renames DLLs, setup lists the ones that are missing.

## build.py
    python build.py             build the C# mod, then assemble Package\
    python build.py --native    also rebuild Native\NewAANgx.dll
    python build.py --linux     also assemble Package-linux\ (with --native, also rebuild Native\libNewAANgx.so)
    python build.py --clean     delete bin\ and obj\ first

`Package\` is a complete, drop-in mod folder:

    mod.toml  newaa.dll  NewAAAssets.xml  NewAANgx.dll  nvngx_dlss.dll  Shaders\  Textures\
    amd_fidelityfx_loader_vk.dll  amd_fidelityfx_upscaler_vk.dll  AMD_FSR_SDK_LICENSE.txt

`Package-linux\` is the experimental Linux mod folder. It has the same `newaa.dll` and content, the Linux plugin and NVIDIA's Linux
DLSS runtime, and no FSR files (AMD ships its FSR runtime for Windows only):

    mod.toml  newaa.dll  NewAAAssets.xml  libNewAANgx.so  libnvidia-ngx-dlss.so.<version>  Shaders\  Textures\

`build.py --linux` builds `Native\libNewAANgx.so` when it is missing. `deploy.py` installs the Windows package only.

The build checks that every shader and texture named in `mod.toml` and `NewAAAssets.xml` is in the package. It never touches the
game folders (it builds with `-p:DeployMod=false`).

## deploy.py
    python deploy.py                 copy Package\ into the mod folder, enable the mod in manifest.toml
    python deploy.py --dry-run       show what would change, change nothing
    python deploy.py --clean         also delete files in the mod folder that are not in the package
    python deploy.py --mod-dir "D:\...\mods\newaa"

The mod folder is `Documents\My Games\Kitten Space Agency\mods\newaa`. Deploying copies only files that changed and checks that
the mod folder matches the package afterwards. The game loads a mod only if it is listed and enabled in `manifest.toml`
(next to the `mods` folder), so deploy adds this entry if it is missing and keeps a backup (`manifest.toml.newaa-backup`):

    [[mods]]
    id = "newaa"
    enabled = true

Close the game and StarMap first. They keep the DLLs locked, and deploy refuses to run while they are open (`--force` overrides).

## Playing
Start the game through `StarMap.exe`, then pick **DLAA**, **FSR Native AA**, **TAA**, **SMAA** or **FXAA** under Settings > Display > Anti-Aliasing.
Switching between MSAA and the other methods asks for a restart, because MSAA changes the render targets at startup.

## Plain dotnet
`dotnet build NewAA.csproj -c Release` also works. It builds and copies the mod into the mods folder in one step
(`-p:DeployMod=false` skips the copy, `-p:KsaInstallDir="D:\Games\KSA\"` and `-p:KsaModDir="..."` override the folders).
It does not touch `manifest.toml`.

## Publishing
The repository does not contain `DLSS_SDK\`, the DLSS runtimes, the built `Native\NewAANgx.dll` and `Native\libNewAANgx.so` or the
downloaded `FSR_SDK\` (see `.gitignore`). A release zip made from `Package\` already includes AMD's FSR DLLs and their license text.
Before you publish a build, read NVIDIA's license in the SDK's `LICENSE.txt` (summarised in `THIRD_PARTY_NOTICES.md`).
The built plugins and `Source\DlaaRenderer.cs` must stay in step: `NewAA_NgxInit` takes the project ID and engine version, so rebuild
the plugins (`python build.py --native --linux`) whenever its signature changes.

## Files and where they come from
- `Source\` the C# mod. `Content\` the shaders, textures and `NewAAAssets.xml`. `mod.toml` is the StarMap/KSA manifest.
- `Native\` the C++ plugin for NVIDIA NGX (see `Native\BUILD.md`). `DLSS_SDK\` NVIDIA's SDK (not in the repository, see Requirements).
- The game's own DLLs are referenced from the game folder with `Private=false`, so they are never copied into the mod.
- StarMap mods must not include Harmony in the mod folder. The `Lib.Harmony` package is only for compiling.
- All shaders and textures must be declared in `Content\NewAAAssets.xml`. The game registers only assets listed in an XML file
  named in `mod.toml`.
