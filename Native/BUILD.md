# NewAA DLAA plugin (native)

Builds `NewAANgx.dll` (and, for the experimental Linux build, `libNewAANgx.so`), which the New AA mod loads through P/Invoke. It starts NVIDIA NGX on the game's Vulkan device and
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

## Linux (experimental)

`libNewAANgx.so` is the same source built for x86-64 Linux. On Linux, `wchar_t` is 32-bit, so the plugin takes the paths as UTF-16
(`char16_t`, which is what the C# side passes on every system) and converts them for NGX.

The easy way, on Windows: make a sysroot once with `make_linux_sysroot.sh` (in WSL, or on Ubuntu or Debian), then

    python build.py --linux --native --linux-sysroot C:\Users\<you>\newaa-sysroot

That cross-compiles with LLVM. By hand, from this Native folder:

    clang++ --target=x86_64-linux-gnu --sysroot=<sysroot> -fuse-ld=lld -shared -fPIC -O2 -std=c++17 -fvisibility=hidden \
        -I ../DLSS_SDK/include -I <Vulkan SDK>/Include newaa_ngx.cpp ../DLSS_SDK/lib/Linux_x86_64/libnvsdk_ngx.a \
        -static-libstdc++ -static-libgcc -ldl -Wl,--exclude-libs,ALL -Wl,--no-undefined -Wl,-soname,libNewAANgx.so \
        -o libNewAANgx.so

On Linux itself the same command works with `g++` (leave out `--target`, `--sysroot` and `-fuse-ld=lld`, and take the Vulkan headers
from your distribution). Build on an old distribution, or against the sysroot, so the plugin runs on old ones too: built against the
Ubuntu 20.04 sysroot it needs only glibc 2.14 symbols. libstdc++ and libgcc are linked in with their symbols hidden, so the plugin
depends only on glibc and libdl.

The DLSS runtime NGX loads from the mod folder is `lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.<version>` from the same SDK. NGX's
core (`libnvidia-ngx.so.1`) comes with NVIDIA's Linux driver. NGX on Linux cannot open paths with non-ASCII characters, so
`Source/DlaaRenderer.cs` copies the runtime to `~/.local/share/newaa/dlss-runtime/` when the mod folder's path has any.

## Exports (C)
- `NewAA_NgxInit`, `NewAA_DlaaSupported`, `NewAA_DlaaStatus`, `NewAA_DlaaCreate`, `NewAA_DlaaEvaluate`, `NewAA_DlaaRelease`

Errors and status are written to stderr with the prefix `[NewAA DLAA]`.
