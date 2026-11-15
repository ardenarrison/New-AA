# NewAA DLAA plugin (native)

Builds `NewAANgx.dll`, which the New AA mod loads through P/Invoke. It starts NVIDIA NGX on the game's Vulkan device and
runs DLSS at native resolution (DLAA) for the main viewport.

## Requirements
- Windows x64, Visual Studio 2022 (C++ build tools) or the MSVC command line
- The Vulkan SDK, for the Vulkan headers (only the headers are used)
- An RTX GPU with a recent NVIDIA driver
- NVIDIA's DLSS SDK at `..\DLSS_SDK` (download it from https://github.com/NVIDIA/DLSS). NVIDIA's license does not allow
  redistributing it, so it is not stored in this project's repository. The build uses only `include\` and
  `lib\Windows_x86_64\x64\nvsdk_ngx_d.lib`, and the deploy step copies `lib\Windows_x86_64\rel\nvngx_dlss.dll`.

## Build

The easy way, from the project folder: `python build.py --native`.

By hand (Developer Command Prompt for VS), from this Native folder:

    cl /LD /EHsc /MD /O2 /std:c++17 /I ..\DLSS_SDK\include /I C:\VulkanSDK\<version>\Include newaa_ngx.cpp /Fe:NewAANgx.dll ^
       /link ..\DLSS_SDK\lib\Windows_x86_64\x64\nvsdk_ngx_d.lib advapi32.lib user32.lib

Adjust the Vulkan SDK path to your install. `nvsdk_ngx_d.lib` is the dynamic-CRT (/MD) NGX library.

`nvngx_dlss.dll` (the DLSS runtime NGX loads from the mod folder) is copied from `..\DLSS_SDK\lib\Windows_x86_64\rel\`
into the mod folder by the C# project's deploy step.

## Where the DLL goes
Copy `NewAANgx.dll` next to `newaa.dll` in the mod folder:

    Documents\My Games\Kitten Space Agency\mods\newaa\NewAANgx.dll

## Exports (C)
- `NewAA_NgxInit`, `NewAA_DlaaSupported`, `NewAA_DlaaCreate`, `NewAA_DlaaEvaluate`, `NewAA_DlaaRelease`

Errors and status are written to stderr with the prefix `[NewAA DLAA]`.
