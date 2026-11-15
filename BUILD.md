# Building and installing the New AA mod

Windows only. Python 3.8+ (standard library only). Three scripts do everything:

    python setup.py      check the machine, restore packages, save the game paths      (once per PC)
    python build.py      build the mod and assemble Package\
    python deploy.py     install Package\ into the game and enable the mod

## Requirements
- The **.NET 10 SDK** (the game targets net10.0 with C# 14): https://dotnet.microsoft.com/download/dotnet/10.0
- **Kitten Space Agency**, installed (default `C:\Program Files\Kitten Space Agency\`)
- **StarMap**, to play with the mod (not to build it): https://github.com/StarMapLoader/StarMap
- An **NVIDIA RTX GPU (RTX 20-series or newer)** to run DLAA. SMAA and FXAA work on any GPU.
- Only to rebuild the native plugin (`build.py --native`): Visual Studio with the C++ workload, and the Vulkan SDK (headers only).

- **NVIDIA's DLSS SDK**, for DLAA: download it from https://github.com/NVIDIA/DLSS and put it in a `DLSS_SDK\` folder in this
  project. NVIDIA's license does not allow redistributing the SDK, so it is not stored in the repository. Only three paths are
  used, so a trimmed copy is enough:

      DLSS_SDK\include\                                   NGX headers (to rebuild the native plugin)
      DLSS_SDK\lib\Windows_x86_64\x64\nvsdk_ngx_d.lib     static library (to rebuild the native plugin)
      DLSS_SDK\lib\Windows_x86_64\rel\nvngx_dlss.dll      the DLSS runtime, copied into the mod

  SMAA and FXAA do not need the SDK.

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
    python build.py --clean     delete bin\ and obj\ first

`Package\` is a complete, drop-in mod folder:

    mod.toml  newaa.dll  NewAAAssets.xml  NewAANgx.dll  nvngx_dlss.dll  Shaders\  Textures\

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
Start the game through `StarMap.exe`, then pick **DLAA**, **SMAA** or **FXAA** under Settings > Display > Anti-Aliasing.
Switching between MSAA and the other methods asks for a restart, because MSAA changes the render targets at startup.

## Plain dotnet
`dotnet build NewAA.csproj -c Release` also works. It builds and copies the mod into the mods folder in one step
(`-p:DeployMod=false` skips the copy, `-p:KsaInstallDir="D:\Games\KSA\"` and `-p:KsaModDir="..."` override the folders).
It does not touch `manifest.toml`.

## Publishing
The repository does not contain `DLSS_SDK\`, the DLSS runtime DLLs, or the built `Native\NewAANgx.dll` (see `.gitignore`).
Before you publish a build, read NVIDIA's license in the SDK's `LICENSE.txt` (summarised in `THIRD_PARTY_NOTICES.md`).
The built plugin and `Source\DlaaRenderer.cs` must stay in step: `NewAA_NgxInit` takes the project ID and engine version, so rebuild
the plugin (`python build.py --native`) whenever its signature changes.

## Files and where they come from
- `Source\` the C# mod. `Content\` the shaders, textures and `NewAAAssets.xml`. `mod.toml` is the StarMap/KSA manifest.
- `Native\` the C++ plugin for NVIDIA NGX (see `Native\BUILD.md`). `DLSS_SDK\` NVIDIA's SDK (not in the repository, see Requirements).
- The game's own DLLs are referenced from the game folder with `Private=false`, so they are never copied into the mod.
- StarMap mods must not include Harmony in the mod folder. The `Lib.Harmony` package is only for compiling.
- All shaders and textures must be declared in `Content\NewAAAssets.xml`. The game registers only assets listed in an XML file
  named in `mod.toml`.
