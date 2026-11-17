# New AA: FXAA, SMAA, TAA, DLAA and FSR anti-aliasing for Kitten Space Agency (StarMap)

Adds FXAA, SMAA 1x (High preset), TAA, DLAA and FSR Native AA to Settings > Display > Anti-Aliasing.
Each runs after the game's tonemap composite, in place of its CMAA2 pass when selected.

## Install (Windows)

Download `newaa-1.2.0.zip` from the Releases page. It contains the built mod, so nothing needs to be compiled.

1. Close the game and StarMap.
2. Extract the `newaa` folder from the zip into `Documents\My Games\Kitten Space Agency\mods\`, so that `mods\newaa\mod.toml` exists.
3. Open `Documents\My Games\Kitten Space Agency\manifest.toml` and make sure it contains:

       [[mods]]
       id = "newaa"
       enabled = true

4. Start the game through `StarMap.exe`, then pick **DLAA**, **FSR Native AA**, **TAA**, **SMAA** or **FXAA** under Settings > Display > Anti-Aliasing.

DLAA needs an NVIDIA RTX GPU (RTX 20-series or newer) and a current driver. FSR Native AA works on NVIDIA, AMD and Intel GPUs that AMD's
FSR runtime supports. TAA, SMAA and FXAA work on any GPU.

## Install (Linux, experimental)

For the game's native Linux build. If you play the Windows build of the game through Proton or Wine, use the Windows zip instead.
Download `newaa-1.2.0-linux-x64-experimental.zip` from the Releases page. It has not been tested in the game on Linux yet, so please
report what you find.

1. Close the game.
2. Extract the `newaa` folder from the zip into `~/Documents/My Games/Kitten Space Agency/mods/`, so that `mods/newaa/mod.toml` exists.
   (The game uses your Documents folder as XDG names it, which can be translated, such as `~/Dokumente`.)
3. Make sure `~/Documents/My Games/Kitten Space Agency/manifest.toml` contains the same `[[mods]]` entry as on Windows.
4. Start the game through StarMap (Borea runs it with `dotnet StarMap.dll` on Linux), then pick **DLAA**, **TAA**, **SMAA** or **FXAA**
   under Settings > Display > Anti-Aliasing.

TAA, SMAA and FXAA work on any GPU. DLAA needs an NVIDIA RTX GPU and NVIDIA's own driver, which provides NGX (`libnvidia-ngx.so.1`) and
`nvidia-smi`. FSR Native AA is not offered on Linux, because AMD ships its FSR runtime for Windows only.

If something does not work, please open an issue with your distribution, GPU, driver version and the newest game log from
`~/Documents/My Games/Kitten Space Agency/logs/` (for DLAA also `~/Documents/My Games/Kitten Space Agency/NewAA/ngx/ngx_log.txt`).

### Powered by NVIDIA DLSS
DLAA in New AA uses **NVIDIA DLSS** (via the NVIDIA NGX SDK). NVIDIA, DLSS and RTX are trademarks of NVIDIA Corporation. New AA is a free community mod and is not affiliated with or endorsed by NVIDIA or by the game's developers.
The release zips include NVIDIA's DLSS runtime (`nvngx_dlss.dll` on Windows, `libnvidia-ngx-dlss.so.310.9.1` on Linux) and NVIDIA's NGX code in `NewAANgx.dll` / `libNewAANgx.so`, with NVIDIA's license text (`NVIDIA_DLSS_SDK_LICENSE.txt`). By using those files you agree to NVIDIA's license. In short: do not reverse engineer them or remove their notices, do not redistribute them as a stand-alone product, and DLSS is for systems with NVIDIA GPUs only.

### Powered by AMD FSR
FSR Native AA in New AA uses **AMD FSR** upscaling at a 1.0x ratio (from AMD's FSR SDK 2.4.0). The Windows release zip includes AMD's signed
`amd_fidelityfx_loader_vk.dll` and `amd_fidelityfx_upscaler_vk.dll`, unmodified, with their MIT license text (`AMD_FSR_SDK_LICENSE.txt`).
AMD, FidelityFX, FSR and Radeon are trademarks of Advanced Micro Devices, Inc. New AA is not affiliated with or endorsed by AMD.

## Build from source

    python setup.py     check the machine and save the game paths (once)
    python build.py     build the mod into Package\
    python deploy.py    install it into the game and enable it

Then start the game through StarMap. Details, options and requirements are in `BUILD.md`. DLAA needs an NVIDIA RTX GPU (20-series or newer),
and building it needs NVIDIA's DLSS SDK, which you download yourself (see `BUILD.md`). FSR, TAA, SMAA and FXAA do not need it. AMD's FSR
runtime is downloaded by `setup.py` / `build.py` from AMD's repository at a fixed release and checked against pinned SHA-256 hashes.
`python build.py --linux` also assembles `Package-linux\`, the experimental Linux build, cross-compiling its native plugin with LLVM
(see `BUILD.md`). Third-party code and licenses are listed in `THIRD_PARTY_NOTICES.md`.

## Layout

    mod.toml, NewAA.csproj, setup.py, build.py, deploy.py, newaa_common.py
    Content/            NewAAAssets.xml, Shaders/ (FXAA, SMAA, DLAA and TAA shaders), Textures/ (SMAA lookup tables, KTX2)
    Native/             C++ plugin for NVIDIA NGX (newaa_ngx.cpp, built into NewAANgx.dll and, for Linux, libNewAANgx.so),
                        make_linux_sysroot.sh (the Linux sysroot the Linux plugin is built against)
    ThirdParty/         AMD_FSR_SDK_LICENSE.txt (shipped with the FSR runtime)
    FSR_SDK/ (downloaded) AMD's signed FSR runtime DLLs
    Source/
      FxaaStarMapMod.cs     StarMap entry points (BeforeMain and ImmediateLoad)
      FxaaHooks.cs          Harmony patches: dropdown options, labels, render routing
      FxaaRenderer.cs       FXAA pass
      SmaaRenderer.cs       SMAA passes and per-viewport state
      AaPasses.cs           helpers shared by the renderers
      FxaaData.cs           push-constant block shared by FXAA and SMAA
      TaaRenderer.cs        TAA menu entry and resolve (TaaResolver)
      TaaCamera.cs          camera jitter, jump detection, motion data (Harmony patches on Camera.OnFrame and Program)
      DlaaRenderer.cs       DLAA passes, NGX start-up and per-frame state
      DlaaBodies.cs         per-object motion vectors
      DlaaLines.cs          orbit-line overlay
      DlaaDeviceExtensions.cs, DlaaSupport.cs, DlaaNative.cs   Vulkan extensions, GPU check, native plugin imports
      TemporalAa.cs         which temporal method (DLAA, FSR or TAA) is selected and ready
      FsrRuntime.cs         FSR menu entry, runtime start-up, the upscaler context (FsrUpscaler)
      FsrNative.cs          bindings for AMD's FSR API (FsrNative, FsrApi)
      FsrDeviceFeatures.cs  Vulkan features and extensions FSR needs, added at device creation

    Package/ (generated)   the finished mod folder that deploy.py installs
    Package-linux/ (generated)   the same for Linux (build.py --linux)

## How it hooks in

- `GameSettings.GetAvailableAntiAliasingMethods` (postfix) adds FXAA, SMAA, TAA, DLAA and FSR Native AA to the dropdown.
- `AntiAliasingNames.Register` adds the extra values and labels to the game's `EnumCollections.AntiAliasingMethods` tables, so the dropdown never draws a null label.
- `Program.RenderAaPreparePasses` (prefix) runs the composite and the anti-aliasing passes.
- `Program.RenderFinalComposite` (prefix) runs the final blend pass.

Each prefix checks the selected option. If the option isn't one of New AA's, or it cannot run,
the original code runs. The prepare and final prefixes make the same decision, so they always agree.

## DLAA

- `DlaaDeviceExtensions` (StarMapBeforeMain) patches `HdrOutput.AddDeviceExtensions` so the game's Vulkan device enables the extensions NGX needs: `VK_NVX_binary_import`, `VK_NVX_image_view_handle`, `VK_KHR_push_descriptor`, `VK_EXT_buffer_device_address`. The game creates its device before mods load, so this has to run that early. Without them NGX reports DLAA unavailable.

- Selecting DLAA runs a GPU check once per session with `nvidia-smi`. Compatible means an RTX GPU with compute capability 7.5 or newer.
  Incompatible GPUs log `GPU incompatible with DLAA using no anti aliasing`.
- If the GPU is compatible, the mod loads `NewAANgx.dll` (Windows) or `libNewAANgx.so` (Linux) from the mod folder and starts NVIDIA
  NGX. Every failure logs a `[DLAA]` line and falls back to no anti-aliasing. NGX writes its messages to `NewAA/ngx/ngx_log.txt` in the
  game's profile folder (`Constants.DocumentsFolderPath`, which StarMap moves into the instance folder when Borea starts the game).
- Linux: NGX turns its paths into file names in the "C" locale and resolves symbolic links first, so it cannot open a mod folder whose
  path has a non-ASCII character (a translated Documents folder such as `~/Документы`). For such a folder the mod copies the DLSS runtime
  to `~/.local/share/newaa/dlss-runtime/` (again only when the mod's copy changes) and points NGX there.
- Pipeline: tonemap composite -> camera motion vectors (`DlaaMotionFrag.frag`) -> DLAA (NGX, in `NewAANgx.dll`) -> copy to the final target.
- The camera is jittered for DLAA the same way as TAA. The jitter is passed to NGX in render pixels.
- Motion vectors (`DlaaMotionFrag.frag`) cover the camera: its rotation from the view-projection matrices, and its movement relative to the followed object from `TaaCamera.CameraTranslation`. The game keeps positions relative to the camera, so the matrices alone miss orbiting and zooming.
- Per-object motion (`DlaaBodies.cs`, `DlaaBodyMotionFrag.frag`): each frame the nearest celestial bodies and vehicles are recorded (position relative to the camera, orientation). The change since last frame is turned into `p_prev = p + B*p + T` for points fixed to the body, computed in double on the CPU. One extra pass per body overwrites the camera-only vectors inside its volume (a sphere for a vehicle, a shell for a celestial body), big bodies first. Celestial bodies that move less than a tenth of a pixel from the camera-only value are skipped. Vehicles are never skipped: a vehicle that is still in view often sits inside a planet's shell, and without its own pass the planet's motion would be written over it.
- Orbit lines (`DlaaLines.cs`): the game's `OrbitLinePass.Run` is skipped while DLAA runs, so the lines never enter DLSS. The same line data is drawn after DLSS as antialiased quads with the unjittered camera, depth-tested against the scene, tonemapped with the game's own composite pass and blended over the result by coverage. If the overlay is unavailable or DLAA cannot run on a frame, the game's pass runs as usual.
- Camera jumps (`TaaCamera.ResetFrameNumber`): changing the followed object or switching camera mode always makes DLSS start its history over and clears the per-object history. A very fast turn or moving more than half the distance to the followed object in one frame does the same, at most once every 30 frames. Each jump is logged with its reason.
- Jitter lives in the camera only while the game records GPU work (`JitterWindow`: from `Program.UpdateShaderData` to the end of `Program.Render`). Both the view-projection and the projection are jittered, because the atmosphere, cloud, ring and sky shaders rebuild their view rays from the inverse projection and must be jittered like the geometry. Everything before that (orbit markers and labels via `Camera.EgoToScreen`, hover tests, culling) sees the unjittered camera.
- DLAA runs on the main viewport only. The plugin holds one DLSS feature, and a second viewport (the game has a 128 x 128 one) made it rebuild the feature every frame.
- Native files are in `Native\`. See `Native\BUILD.md` for building `NewAANgx.dll` and `libNewAANgx.so`. The build copies them into the
  packages if they are in `Native\`.
- Adapter lines to check if the build fails: `renderer.Instance`, `renderer.PhysicalDevice`, `renderer.Device.Handle` in `DlaaRenderer.cs`, and
  `commandBuffer.Handle` in `DlaaRenderer.RenderPreparePasses`.
- NGX is started with New AA's own project ID (a GUID, `ProjectId` in `DlaaRenderer.cs`) and the CUSTOM engine type, because NVIDIA issued no application ID. If NVIDIA ever issues one, switch `newaa_ngx.cpp` back to `NVSDK_NGX_VULKAN_Init`.

## FSR Native AA

FSR Native AA is AMD's FSR upscaler with render size equal to output size, so it only anti-aliases. It runs through the DLAA pipeline
above: the same jittered camera, camera and per-object motion vectors, camera-jump resets, orbit-line overlay and main-viewport-only rule.
Only the resolve step differs: `DlaaRenderer.EvaluateFsr` calls FSR where DLAA calls NGX.

- Windows only: AMD ships the FSR runtime for Windows only, so on other systems FSR Native AA is left out of the dropdown and
  `FsrDeviceFeatures` leaves the game's device as it is.

- No native plugin: `FsrNative.cs` calls AMD's C API (`ffxCreateContext`, `ffxQuery`, `ffxDispatch`, `ffxDestroyContext`) in
  `amd_fidelityfx_loader_vk.dll` directly through function pointers. The loader finds `amd_fidelityfx_upscaler_vk.dll` in its own folder
  (the mod folder). The struct layouts were read from the SDK headers with MSVC; `FsrNative.CheckLayout` checks the sizes at start-up.
- `FsrDeviceFeatures` (StarMapBeforeMain) patches `Core.PhysicalDeviceExtensions.CreateDevice` and adds, where the GPU supports them,
  the features FSR's shaders use but the game's device does not enable: compute shader derivatives (`VK_KHR_compute_shader_derivatives`,
  needed by FSR 3 on every GPU), fp16/int8, 8- and 16-bit storage, the Vulkan memory model, and for FSR 4 on AMD cooperative matrices
  (`VK_KHR_cooperative_matrix`) and FP8 (`VK_EXT_shader_float8`). The FSR runtime picks its shaders from what the GPU supports, not from
  what the device enabled, so without these Vulkan validation reports invalid shader modules.
- Selecting FSR loads the runtime and logs the versions it offers for the GPU (`[FSR] FSR runtime loaded. Upscaler versions for this GPU:
  ...`). The runtime picks the newest one (FSR 3.1.6 on NVIDIA; on supported AMD Radeon GPUs it can pick FSR 4). If that version cannot
  create a context, the others are tried newest first. If none works, FSR is turned off for the session and the game falls back to no
  anti-aliasing.
- Inputs: the tonemapped LDR scene (gamma-encoded, so the non-linear colour flags are set), reverse-Z depth (`DEPTH_INVERTED`), the
  motion vectors in UV units scaled by the render size, and the jitter in render pixels, all as for DLSS. FSR also gets the camera's near
  and far planes and vertical field of view, and the frame time. FSR reads every input in `SHADER_READ_ONLY_OPTIMAL`, so the depth image
  is moved there around the FSR call and back to `DEPTH_STENCIL_READ_ONLY_OPTIMAL` afterwards.
- One FSR context at native size. A resize creates a new context and destroys the old one a few frames later, once the GPU can no
  longer be using it. Switching between DLAA and FSR restarts the temporal history.
- Every FSR result change is logged with the prefix `[FSR]`, as are the runtime's own warnings (at most 40 per session).

## TAA

TAA is New AA's own temporal anti-aliasing. It runs through the DLAA pipeline like FSR Native AA: the same jittered camera, camera
and per-object motion vectors, camera-jump resets, orbit-line overlay and main-viewport-only rule. Only the resolve is its own. It
needs no runtime or SDK and works on any GPU.

- `TaaResolver` (`TaaRenderer.cs`) records three passes after the motion vectors: the resolve (`TaaResolveFrag.frag`), a light
  sharpen into the image the final pass shows (`TaaSharpenFrag.frag`), and a pass that keeps the nearest depth around each pixel
  for the next frame (`TaaDepthFrag.frag`). Its buffers (two 16-bit float history buffers, the output and the kept depth) are made
  the first time TAA runs, so DLAA and FSR users never allocate them.
- The resolve, per pixel: rebuilds this frame's colour at the pixel centre from the 3x3 neighbourhood, weighting each sample by its
  distance once the jitter is taken out; takes the motion vector of the closest surface in the 3x3 neighbourhood; reads the history
  there with a 5-tap Catmull-Rom filter; clips it to this frame's colours around the pixel (a YCoCg variance box inside the min/max
  box); then blends. After a reset it averages the frames so far (the history's alpha counts them), then takes 10% of each new frame,
  up to 25% for fast motion.
- Stars: dim stars are smaller than a pixel and fall between the samples in some frames of the jitter cycle, where clipping would
  erase them. A still pixel with only sky around it this frame and last (depth 0 in its 3x3 neighbourhood both times), whose history
  differs from this frame only in a spot a couple of pixels wide, keeps its history unclipped. Engine plumes and smoke write no depth
  either, but they leave a larger area when they move, so they are still clipped.
- Limits: geometry thinner than a pixel (distant antennas, thin struts) can look broken up or flicker, as with most TAA. The image is
  a little softer than DLAA or FSR Native AA.

## License

Copyright 2026 Arden Arrison. New AA's own code is licensed under the Apache License 2.0 (see `LICENSE` and `NOTICE`). Third-party code keeps its own terms: SMAA (MIT),
FXAA (public domain), NVIDIA's DLSS SDK (not included; NVIDIA's license) and AMD's FSR SDK runtime (MIT, `ThirdParty/AMD_FSR_SDK_LICENSE.txt`). See `THIRD_PARTY_NOTICES.md`.

## Notes

- The settings file stores the new options as numbers (5 for FXAA, 6 for SMAA, 7 for TAA, 8 for DLAA, 9 for FSR Native AA), because the game's enum
  has no names for them.
- Switching between an MSAA option and FXAA or SMAA asks for a restart, because MSAA changes the render
  targets at startup.
- SMAA's vertex shaders generate the fullscreen quad themselves. The vertex order is counter-clockwise on
  screen, because the game culls clockwise triangles.
- Missing shaders or textures are logged once with the prefix `[FXAA]` or `[SMAA]`, and the game falls back
  to no anti-aliasing.
- Profiler GPU tags reuse the CMAA2 tags, so FXAA and SMAA time shows up under CMAA2.
