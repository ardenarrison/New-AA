// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
#include "newaa_ngx.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#ifdef _WIN32
#include <wchar.h>
#endif

// From NVIDIA's DLSS SDK (include/). The build adds that folder to the include path.
#include "nvsdk_ngx_helpers_vk.h"

// NGX state for one DLAA feature. Recreated when the render size changes.
static NVSDK_NGX_Handle* g_dlaaHandle = nullptr;
static NVSDK_NGX_Parameter* g_parameters = nullptr;
static VkDevice g_device = VK_NULL_HANDLE;
static uint32_t g_createdWidth = 0;
static uint32_t g_createdHeight = 0;

// NGX log file (ngx_log.txt in the data path). NGX writes its failure reasons here.
static FILE* g_ngxLog = nullptr;

static void NVSDK_CONV NgxLogCallback(const char* message, NVSDK_NGX_Logging_Level level, NVSDK_NGX_Feature component)
{
    if (g_ngxLog != nullptr)
    {
        std::fprintf(g_ngxLog, "[NGX level %d, feature %d] %s\n", (int)level, (int)component, message);
        std::fflush(g_ngxLog);
    }
}

static void OpenNgxLog(const wchar_t* dataPath)
{
#ifdef _WIN32
    std::wstring logPath = std::wstring(dataPath) + L"\\ngx_log.txt";
    g_ngxLog = _wfopen(logPath.c_str(), L"a");
#else
    (void)dataPath;
#endif
}

static void LogLine(const char* text)
{
    std::fprintf(stderr, "[NewAA DLAA] %s\n", text);
}

NEWAA_API int NewAA_NgxInit(const char* projectId, const char* engineVersion, const wchar_t* applicationDataPath, const wchar_t* featureSearchPath, VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device, PFN_vkGetInstanceProcAddr getInstanceProcAddr)
{
    g_device = device;
    OpenNgxLog(applicationDataPath);

    // Feature info: where NGX looks for nvngx_dlss.dll, and where it writes its messages.
    static const wchar_t* searchPaths[1];
    searchPaths[0] = featureSearchPath;
    static NVSDK_NGX_FeatureCommonInfo featureInfo;
    std::memset(&featureInfo, 0, sizeof(featureInfo));
    featureInfo.PathListInfo.Path = searchPaths;
    featureInfo.PathListInfo.Length = 1;
    featureInfo.LoggingInfo.LoggingCallback = NgxLogCallback;
    // ON records NGX's errors and warnings. VERBOSE writes dozens of lines every frame and costs frame time.
    featureInfo.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
    featureInfo.LoggingInfo.DisableOtherLoggingSinks = false;
    // Use the engine's loader. NGX's default loader can disagree with the device's function table, which gives FAIL_PlatformError.
    PFN_vkGetDeviceProcAddr getDeviceProcAddr = nullptr;
    if (getInstanceProcAddr != nullptr)
    {
        getDeviceProcAddr = (PFN_vkGetDeviceProcAddr)getInstanceProcAddr(instance, "vkGetDeviceProcAddr");
    }
    // List the extensions NGX needs, so the log shows which ones the engine must enable.
    unsigned int instanceExtCount = 0;
    const char** instanceExts = nullptr;
    unsigned int deviceExtCount = 0;
    const char** deviceExts = nullptr;
    NVSDK_NGX_Result requiredResult = NVSDK_NGX_VULKAN_RequiredExtensions(&instanceExtCount, &instanceExts, &deviceExtCount, &deviceExts);
    if (NVSDK_NGX_SUCCEED(requiredResult))
    {
        for (unsigned int i = 0; i < instanceExtCount; ++i)
        {
            LogLine(("NGX requires instance extension: " + std::string(instanceExts[i])).c_str());
        }
        for (unsigned int i = 0; i < deviceExtCount; ++i)
        {
            LogLine(("NGX requires device extension: " + std::string(deviceExts[i])).c_str());
        }
    }
    else
    {
        LogLine("NVSDK_NGX_VULKAN_RequiredExtensions failed");
    }

    // No NVIDIA-issued application ID, so identify the project with our own GUID and the CUSTOM engine type.
    NVSDK_NGX_Result result = NVSDK_NGX_VULKAN_Init_with_ProjectID(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, engineVersion, applicationDataPath, instance, physicalDevice, device, getInstanceProcAddr, getDeviceProcAddr, &featureInfo, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(result))
    {
        LogLine("NVSDK_NGX_VULKAN_Init_with_ProjectID failed");
        return (int)result;
    }
    result = NVSDK_NGX_VULKAN_GetCapabilityParameters(&g_parameters);
    if (NVSDK_NGX_FAILED(result))
    {
        LogLine("NVSDK_NGX_VULKAN_GetCapabilityParameters failed");
    }
    return (int)result;
}

NEWAA_API int NewAA_DlaaSupported()
{
    if (g_parameters == nullptr)
    {
        return 0;
    }
    int available = 0;
    NVSDK_NGX_Result queryResult = NVSDK_NGX_Parameter_GetI(g_parameters, NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    return (NVSDK_NGX_SUCCEED(queryResult) && available != 0) ? 1 : 0;
}

static int ReadIntOrMinusOne(const char* name)
{
    int value = -1;
    NVSDK_NGX_Result result = NVSDK_NGX_Parameter_GetI(g_parameters, name, &value);
    return NVSDK_NGX_SUCCEED(result) ? value : -1;
}

NEWAA_API int NewAA_DlaaStatus(int* available, int* needsUpdatedDriver, int* minDriverMajor, int* minDriverMinor, int* featureInitResult)
{
    if (available == nullptr || needsUpdatedDriver == nullptr || minDriverMajor == nullptr || minDriverMinor == nullptr || featureInitResult == nullptr)
    {
        return (int)NVSDK_NGX_Result_FAIL_InvalidParameter;
    }
    if (g_parameters == nullptr)
    {
        return (int)NVSDK_NGX_Result_FAIL_NotInitialized;
    }
    *available = ReadIntOrMinusOne(NVSDK_NGX_Parameter_SuperSampling_Available);
    *needsUpdatedDriver = ReadIntOrMinusOne(NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver);
    *minDriverMajor = ReadIntOrMinusOne(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor);
    *minDriverMinor = ReadIntOrMinusOne(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor);
    *featureInitResult = ReadIntOrMinusOne(NVSDK_NGX_Parameter_SuperSampling_FeatureInitResult);
    return (int)NVSDK_NGX_Result_Success;
}

NEWAA_API int NewAA_DlaaCreate(VkCommandBuffer commandBuffer, uint32_t renderWidth, uint32_t renderHeight)
{
    if (g_parameters == nullptr)
    {
        return (int)NVSDK_NGX_Result_Fail;
    }
    if (g_dlaaHandle != nullptr && g_createdWidth == renderWidth && g_createdHeight == renderHeight)
    {
        return (int)NVSDK_NGX_Result_Success;
    }
    if (g_dlaaHandle != nullptr)
    {
        // Size changed: release the old feature first.
        NVSDK_NGX_VULKAN_ReleaseFeature(g_dlaaHandle);
        g_dlaaHandle = nullptr;
    }

    // DLAA is the DLSS feature at native resolution. Output size equals render size.
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_Width, (int)renderWidth);
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_Height, (int)renderHeight);
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_OutWidth, (int)renderWidth);
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_OutHeight, (int)renderHeight);
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_PerfQualityValue, NVSDK_NGX_PerfQuality_Value_DLAA);
    // Feature flags: LDR input (no IsHDR), motion vectors at render resolution, auto-exposure off.
    // The engine uses a reverse-Z depth buffer (near = 1, far = 0), so DLSS has to be told the depth is inverted.
    NVSDK_NGX_Parameter_SetI(g_parameters, NVSDK_NGX_Parameter_DLSS_Feature_Create_Flags, NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_DepthInverted);

    NVSDK_NGX_Result result = NVSDK_NGX_VULKAN_CreateFeature(commandBuffer, NVSDK_NGX_Feature_SuperSampling, g_parameters, &g_dlaaHandle);
    if (NVSDK_NGX_FAILED(result))
    {
        LogLine("NVSDK_NGX_VULKAN_CreateFeature (DLAA) failed");
        g_dlaaHandle = nullptr;
        return (int)result;
    }
    g_createdWidth = renderWidth;
    g_createdHeight = renderHeight;
    LogLine("DLAA feature created");
    return (int)result;
}

NEWAA_API int NewAA_DlaaEvaluate(VkCommandBuffer commandBuffer,
                                 VkImage color, VkImageView colorView, VkFormat colorFormat,
                                 VkImage depth, VkImageView depthView, VkFormat depthFormat,
                                 VkImage motion, VkImageView motionView, VkFormat motionFormat,
                                 VkImage output, VkImageView outputView, VkFormat outputFormat,
                                 uint32_t width, uint32_t height,
                                 float jitterX, float jitterY,
                                 float motionScaleX, float motionScaleY,
                                 int reset)
{
    if (g_dlaaHandle == nullptr || g_parameters == nullptr)
    {
        return (int)NVSDK_NGX_Result_Fail;
    }

    VkImageSubresourceRange range{};
    range.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    range.levelCount = 1;
    range.layerCount = 1;

    VkImageSubresourceRange depthRange = range;
    depthRange.aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT;

    NVSDK_NGX_Resource_VK colorResource = NVSDK_NGX_Create_ImageView_Resource_VK(colorView, color, range, colorFormat, width, height, false);
    NVSDK_NGX_Resource_VK depthResource = NVSDK_NGX_Create_ImageView_Resource_VK(depthView, depth, depthRange, depthFormat, width, height, false);
    NVSDK_NGX_Resource_VK motionResource = NVSDK_NGX_Create_ImageView_Resource_VK(motionView, motion, range, motionFormat, width, height, false);
    NVSDK_NGX_Resource_VK outputResource = NVSDK_NGX_Create_ImageView_Resource_VK(outputView, output, range, outputFormat, width, height, true);

    NVSDK_NGX_VK_DLSS_Eval_Params evalParams{};
    evalParams.Feature.pInColor = &colorResource;
    evalParams.Feature.pInOutput = &outputResource;
    evalParams.pInDepth = &depthResource;
    evalParams.pInMotionVectors = &motionResource;
    evalParams.InJitterOffsetX = jitterX;
    evalParams.InJitterOffsetY = jitterY;
    evalParams.InMVScaleX = motionScaleX;
    evalParams.InMVScaleY = motionScaleY;
    evalParams.InReset = reset;
    evalParams.InRenderSubrectDimensions.Width = width;
    evalParams.InRenderSubrectDimensions.Height = height;

    NVSDK_NGX_Result result = NGX_VULKAN_EVALUATE_DLSS_EXT(commandBuffer, g_dlaaHandle, g_parameters, &evalParams);
    if (NVSDK_NGX_FAILED(result))
    {
        LogLine("DLAA evaluate failed");
    }
    return (int)result;
}

NEWAA_API int NewAA_DlaaRelease()
{
    if (g_dlaaHandle != nullptr)
    {
        NVSDK_NGX_VULKAN_ReleaseFeature(g_dlaaHandle);
        g_dlaaHandle = nullptr;
    }
    NVSDK_NGX_Result result = NVSDK_NGX_VULKAN_Shutdown1(g_device);
    g_parameters = nullptr;
    return (int)result;
}
