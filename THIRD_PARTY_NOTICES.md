# Third-party notices

New AA's own code is licensed under the Apache License 2.0 (see `LICENSE`). That license covers only New AA's own code; the
work below keeps its own terms. This file lists what it is, where it is used, and under what terms.

## SMAA (Enhanced Subpixel Morphological Antialiasing)

- Used in: `Content/Shaders/Smaa*.frag`, `Content/Shaders/Smaa*.vert` (generated from `SMAA.hlsl`) and the lookup textures
  `Content/Textures/AreaTex.ktx2` and `Content/Textures/SearchTex.ktx2` (the SMAA area and search tables).
- Project: https://github.com/iryoku/smaa
- License: MIT. The notice is also kept at the top of each SMAA shader file.

```
Copyright (C) 2013 Jorge Jimenez (jorge@iryoku.com)
Copyright (C) 2013 Jose I. Echevarria (joseignacioechevarria@gmail.com)
Copyright (C) 2013 Belen Masia (bmasia@unizar.es)
Copyright (C) 2013 Fernando Navarro (fernandn@microsoft.com)
Copyright (C) 2013 Diego Gutierrez (diegog@unizar.es)

Permission is hereby granted, free of charge, to any person obtaining a copy
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to
do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software. As clarification, there
is no requirement that the copyright notice and permission be included in
binary distributions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## FXAA 3.11

- Used in: `Content/Shaders/FxaaFrag.frag`, adapted from Timothy Lottes' FXAA reference implementation (NVIDIA), which
  Lottes released into the public domain.

## NVIDIA DLSS / NGX SDK (not included in this repository)

- Used by: `Native/newaa_ngx.cpp`, which is compiled against the SDK's headers and linked with its static library
  (`nvsdk_ngx_d.lib`) to produce `NewAANgx.dll`. The DLSS runtime `nvngx_dlss.dll` is loaded from the mod folder.
- Source: https://github.com/NVIDIA/DLSS (the version used in development is DLSS 310.9.1).
- The SDK is licensed by NVIDIA under the NVIDIA RTX SDKs license, which is in the SDK's `LICENSE.txt`. That license does not
  allow the SDK to be redistributed as a stand-alone product, so this repository does not contain it. Download it yourself and
  put it in `DLSS_SDK/` (see `BUILD.md`). Anyone who distributes a build that includes NVIDIA's object code or `nvngx_dlss.dll`
  must meet the distribution requirements in that license.
- NVIDIA did not issue New AA an application ID, so it starts NGX with its own project ID (a GUID in `Source/DlaaRenderer.cs`) and
  the CUSTOM engine type, which is what the SDK headers prescribe for that case.
- Release builds of New AA ship `nvngx_dlss.dll` and `NewAANgx.dll` (which contains NVIDIA NGX code) together with NVIDIA's license text.
  Using those files is subject to NVIDIA's license: among other things, you may not reverse engineer or decompile them, remove their
  notices, or redistribute them as a stand-alone product, and DLSS is licensed only for use on systems with NVIDIA GPUs. NVIDIA may
  also update the DLSS runtime over the air.
- NVIDIA, DLSS, RTX and GeForce are trademarks of NVIDIA Corporation. New AA is not affiliated with or endorsed by NVIDIA.

## Harmony

- Used as: a compile-time reference (NuGet `Lib.Harmony` 2.3.3). At runtime StarMap supplies its own copy, and the mod does not
  ship Harmony.
- Project: https://github.com/pardeike/Harmony. License: MIT, Copyright (c) Andreas Pardeike.

## StarMap

- Used as: a compile-time reference (NuGet `StarMap.API`) and the loader that runs the mod. The mod does not ship StarMap.
- Project: https://github.com/StarMapLoader/StarMap. License: MIT, Copyright (c) 2025 StarMap.

## Kitten Space Agency

- The game's DLLs are referenced at build time from your own install and are never copied into this project or the mod.
- `Content/Shaders/DlaaLineVert.vert` colours orbit lines the same way the game does (saturation and lightness scaled by alpha),
  using its own code written for New AA, not code copied from the game.
- New AA is a community mod and is not affiliated with or endorsed by the game's developers.
