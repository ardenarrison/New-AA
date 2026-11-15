# New AA: FXAA, SMAA and DLAA anti-aliasing for Kitten Space Agency (StarMap)

Adds FXAA, SMAA 1x (High preset) and DLAA to Settings > Display > Anti-Aliasing. TAA is kept in the code but is switched off (`TaaAntiAliasing.Enabled = false`).
Each runs after the game's tonemap composite, in place of its CMAA2 pass when selected.

## Install (Windows)

Download `newaa-1.0.0-windows.zip` from the Releases page. It contains the built mod, so nothing needs to be compiled. New AA supports Windows only for now.

1. Close the game and StarMap.
2. Extract the `newaa` folder from the zip into `Documents\My Games\Kitten Space Agency\mods\`, so that `mods\newaa\mod.toml` exists.
3. Open `Documents\My Games\Kitten Space Agency\manifest.toml` and make sure it contains:

       [[mods]]
       id = "newaa"
       enabled = true

4. Start the game through `StarMap.exe`, then pick **DLAA**, **SMAA** or **FXAA** under Settings > Display > Anti-Aliasing.

DLAA needs an NVIDIA RTX GPU (RTX 20-series or newer) and a current driver. SMAA and FXAA work on any GPU.

### Powered by NVIDIA DLSS
DLAA in New AA uses **NVIDIA DLSS** (via the NVIDIA NGX SDK). NVIDIA, DLSS and RTX are trademarks of NVIDIA Corporation. New AA is a free community mod and is not affiliated with or endorsed by NVIDIA or by the game's developers.
The release zip includes `nvngx_dlss.dll` and NVIDIA's NGX code in `NewAANgx.dll`, with NVIDIA's license text (`NVIDIA_DLSS_SDK_LICENSE.txt`). By using those files you agree to NVIDIA's license. In short: do not reverse engineer them or remove their notices, do not redistribute them as a stand-alone product, and DLSS is for systems with NVIDIA GPUs only.

## Build from source

    python setup.py     check the machine and save the game paths (once)
    python build.py     build the mod into Package\
    python deploy.py    install it into the game and enable it

Then start the game through StarMap. Details, options and requirements are in `BUILD.md`. DLAA needs an NVIDIA RTX GPU (20-series or newer),
and building it needs NVIDIA's DLSS SDK, which you download yourself (see `BUILD.md`). SMAA and FXAA do not need it.
Third-party code and licenses are listed in `THIRD_PARTY_NOTICES.md`.

## Layout

    mod.toml, NewAA.csproj, setup.py, build.py, deploy.py, newaa_common.py
    Content/            NewAAAssets.xml, Shaders/ (FXAA, SMAA, DLAA and TAA shaders), Textures/ (SMAA lookup tables, KTX2)
    Native/             C++ plugin for NVIDIA NGX (newaa_ngx.cpp, built into NewAANgx.dll)
    Source/
      FxaaStarMapMod.cs     StarMap entry points (BeforeMain and ImmediateLoad)
      FxaaHooks.cs          Harmony patches: dropdown options, labels, render routing
      FxaaRenderer.cs       FXAA pass
      SmaaRenderer.cs       SMAA passes and per-viewport state
      AaPasses.cs           helpers shared by the renderers
      FxaaData.cs           push-constant block shared by FXAA and SMAA
      TaaRenderer.cs        TAA resolve (switched off)
      TaaCamera.cs          camera jitter, jump detection, motion data (Harmony patches on Camera.OnFrame and Program)
      DlaaRenderer.cs       DLAA passes, NGX start-up and per-frame state
      DlaaBodies.cs         per-object motion vectors
      DlaaLines.cs          orbit-line overlay
      DlaaDeviceExtensions.cs, DlaaSupport.cs, DlaaNative.cs   Vulkan extensions, GPU check, native plugin imports

    Package/ (generated)   the finished mod folder that deploy.py installs

## How it hooks in

- `GameSettings.GetAvailableAntiAliasingMethods` (postfix) adds FXAA and SMAA to the dropdown.
- `AntiAliasingNames.Register` adds the extra values and labels to the game's `EnumCollections.AntiAliasingMethods` tables, so the dropdown never draws a null label.
- `Program.RenderAaPreparePasses` (prefix) runs the composite and the anti-aliasing passes.
- `Program.RenderFinalComposite` (prefix) runs the final blend pass.

Each prefix checks the selected option. If the option isn't FXAA or SMAA, or its resources are missing,
the original code runs. The prepare and final prefixes make the same decision, so they always agree.

## DLAA

- `DlaaDeviceExtensions` (StarMapBeforeMain) patches `HdrOutput.AddDeviceExtensions` so the game's Vulkan device enables the extensions NGX needs: `VK_NVX_binary_import`, `VK_NVX_image_view_handle`, `VK_KHR_push_descriptor`, `VK_EXT_buffer_device_address`. The game creates its device before mods load, so this has to run that early. Without them NGX reports DLAA unavailable.

- Selecting DLAA runs a GPU check once per session with `nvidia-smi`. Compatible means an RTX GPU with compute capability 7.5 or newer.
  Incompatible GPUs log `GPU incompatible with DLAA using no anti aliasing`.
- If the GPU is compatible, the mod loads `NewAANgx.dll` from the mod folder and starts NVIDIA NGX. Every failure logs a `[DLAA]` line and
  falls back to no anti-aliasing.
- Pipeline: tonemap composite -> camera motion vectors (`DlaaMotionFrag.frag`) -> DLAA (NGX, in `NewAANgx.dll`) -> copy to the final target.
- The camera is jittered for DLAA the same way as TAA. The jitter is passed to NGX in render pixels.
- Motion vectors (`DlaaMotionFrag.frag`) cover the camera: its rotation from the view-projection matrices, and its movement relative to the followed object from `TaaCamera.CameraTranslation`. The game keeps positions relative to the camera, so the matrices alone miss orbiting and zooming.
- Per-object motion (`DlaaBodies.cs`, `DlaaBodyMotionFrag.frag`): each frame the nearest celestial bodies and vehicles are recorded (position relative to the camera, orientation). The change since last frame is turned into `p_prev = p + B*p + T` for points fixed to the body, computed in double on the CPU. One extra pass per body overwrites the camera-only vectors inside its volume (a sphere for a vehicle, a shell for a celestial body), big bodies first. Celestial bodies that move less than a tenth of a pixel from the camera-only value are skipped. Vehicles are never skipped: a vehicle that is still in view often sits inside a planet's shell, and without its own pass the planet's motion would be written over it.
- Orbit lines (`DlaaLines.cs`): the game's `OrbitLinePass.Run` is skipped while DLAA runs, so the lines never enter DLSS. The same line data is drawn after DLSS as antialiased quads with the unjittered camera, depth-tested against the scene, tonemapped with the game's own composite pass and blended over the result by coverage. If the overlay is unavailable or DLAA cannot run on a frame, the game's pass runs as usual.
- Camera jumps (`TaaCamera.ResetFrameNumber`): changing the followed object or switching camera mode always makes DLSS start its history over and clears the per-object history. A very fast turn or moving more than half the distance to the followed object in one frame does the same, at most once every 30 frames. Each jump is logged with its reason.
- Jitter lives in the camera only while the game records GPU work (`JitterWindow`: from `Program.UpdateShaderData` to the end of `Program.Render`). Both the view-projection and the projection are jittered, because the atmosphere, cloud, ring and sky shaders rebuild their view rays from the inverse projection and must be jittered like the geometry. Everything before that (orbit markers and labels via `Camera.EgoToScreen`, hover tests, culling) sees the unjittered camera.
- DLAA runs on the main viewport only. The plugin holds one DLSS feature, and a second viewport (the game has a 128 x 128 one) made it rebuild the feature every frame.
- Native files are in `Native\`. See `Native\BUILD.md` for building `NewAANgx.dll`. The build copies it into the mod folder if it is in `Native\`.
- Adapter lines to check if the build fails: `renderer.Instance`, `renderer.PhysicalDevice`, `renderer.Device.Handle` in `DlaaRenderer.cs`, and
  `commandBuffer.Handle` in `DlaaRenderer.RenderPreparePasses`.
- NGX is started with New AA's own project ID (a GUID, `ProjectId` in `DlaaRenderer.cs`) and the CUSTOM engine type, because NVIDIA issued no application ID. If NVIDIA ever issues one, switch `newaa_ngx.cpp` back to `NVSDK_NGX_VULKAN_Init`.

## TAA notes (disabled)

- The camera's projection gets a Halton(2,3) sub-pixel jitter each frame. The jitter is applied only to the matrix used for rendering.
- Reprojection uses the depth buffer and the previous frame's unjittered view-projection. History is clamped to the 3x3 neighborhood in YCoCg.
- Limitation: reprojection assumes the scene is static relative to the camera. Moving objects and the vehicle moving through the world will
  ghost. The clamp keeps this from getting worse, but it can make the image look soft while moving.
- Matrix layout: the shader assumes the engine stores matrices row-major, so GLSL multiplies them as column vectors. If TAA looks distorted or
  shifted from the first frame, that assumption is the first thing to check.

## License

Copyright 2026 Arden Arrison. New AA's own code is licensed under the Apache License 2.0 (see `LICENSE` and `NOTICE`). Third-party code keeps its own terms: SMAA (MIT),
FXAA (public domain), and NVIDIA's DLSS SDK (not included; NVIDIA's license). See `THIRD_PARTY_NOTICES.md`.

## Notes

- The settings file stores the new options as numbers (5 for FXAA, 6 for SMAA), because the game's enum
  has no names for them.
- Switching between an MSAA option and FXAA or SMAA asks for a restart, because MSAA changes the render
  targets at startup.
- SMAA's vertex shaders generate the fullscreen quad themselves. The vertex order is counter-clockwise on
  screen, because the game culls clockwise triangles.
- Missing shaders or textures are logged once with the prefix `[FXAA]` or `[SMAA]`, and the game falls back
  to no anti-aliasing.
- Profiler GPU tags reuse the CMAA2 tags, so FXAA and SMAA time shows up under CMAA2.
