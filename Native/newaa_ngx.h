// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
#pragma once
// C interface for the New AA DLAA plugin. The C# mod calls these through P/Invoke.
// All functions return an NVSDK_NGX_Result value (0 = success, see nvsdk_ngx_defs.h).
#include <cstdint>
#include <vulkan/vulkan.h>

#ifdef _WIN32
#define NEWAA_API extern "C" __declspec(dllexport)
// The C# side passes paths as UTF-16 (CharSet.Unicode), which is wchar_t on Windows.
typedef wchar_t NewAA_PathChar;
#else
#define NEWAA_API extern "C" __attribute__((visibility("default")))
// wchar_t is 32-bit on Linux, but the C# side still passes UTF-16. The plugin converts the paths before it hands them to NGX.
typedef char16_t NewAA_PathChar;
#endif

// Starts NGX for this device. Call once, with the engine's Vulkan handles.
// getInstanceProcAddr must be the engine's own vkGetInstanceProcAddr, so NGX uses the same Vulkan loader as the device.
// projectId: GUID-like string identifying the application to NGX (NVIDIA's rule when no application ID was issued). engineVersion: any version string.
// featureSearchPath: folder NGX also searches for the DLSS runtime (the mod folder): nvngx_dlss.dll on Windows,
// libnvidia-ngx-dlss.so.<version> on Linux. NGX messages go to ngx_log.txt in applicationDataPath.
NEWAA_API int NewAA_NgxInit(const char* projectId, const char* engineVersion, const NewAA_PathChar* applicationDataPath, const NewAA_PathChar* featureSearchPath, VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device, PFN_vkGetInstanceProcAddr getInstanceProcAddr);

// Returns 1 if the NGX runtime reports DLAA as available on this device, 0 otherwise.
NEWAA_API int NewAA_DlaaSupported();

// Reports why DLAA is or isn't available. Each out value is set if NGX provides it, otherwise it is -1.
// Returns the NVSDK_NGX_Result of the query.
NEWAA_API int NewAA_DlaaStatus(int* available, int* needsUpdatedDriver, int* minDriverMajor, int* minDriverMinor, int* featureInitResult);

// Creates the DLAA feature at the given render size. Records into the command buffer.
NEWAA_API int NewAA_DlaaCreate(VkCommandBuffer commandBuffer, uint32_t renderWidth, uint32_t renderHeight);

// Runs DLAA for one frame. jitterX/jitterY are in render pixels. Set reset to 1 on camera cuts.
NEWAA_API int NewAA_DlaaEvaluate(VkCommandBuffer commandBuffer,
                                 VkImage color, VkImageView colorView, VkFormat colorFormat,
                                 VkImage depth, VkImageView depthView, VkFormat depthFormat,
                                 VkImage motion, VkImageView motionView, VkFormat motionFormat,
                                 VkImage output, VkImageView outputView, VkFormat outputFormat,
                                 uint32_t width, uint32_t height,
                                 float jitterX, float jitterY,
                                 float motionScaleX, float motionScaleY,
                                 int reset);

// Releases the DLAA feature and the NGX runtime.
NEWAA_API int NewAA_DlaaRelease();
