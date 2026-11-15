// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;

namespace KSA.Mods.NewAA;

// P/Invoke bindings for NewAANgx.dll (Native\newaa_ngx.cpp). The DLL is loaded from the mod folder by path,
// so it does not have to be on the system search path.
internal static class DlaaNative
{
	private const string LibraryName = "NewAANgx";

	// NVSDK_NGX_Result_Success in nvsdk_ngx_defs.h. NGX uses 0x1 for success, not 0.
	public const int NgxSuccess = 0x1;

	private static IntPtr _handle;

	public static void Load(string dllPath)
	{
		if (_handle != IntPtr.Zero)
		{
			return;
		}
		_handle = NativeLibrary.Load(dllPath);
		NativeLibrary.SetDllImportResolver(typeof(DlaaNative).Assembly, Resolve);
	}

	private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		return libraryName == LibraryName ? _handle : IntPtr.Zero;
	}

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
	public static extern int NewAA_NgxInit([MarshalAs(UnmanagedType.LPStr)] string projectId, [MarshalAs(UnmanagedType.LPStr)] string engineVersion, string applicationDataPath, string featureSearchPath, VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device, nint getInstanceProcAddr);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int NewAA_DlaaSupported();

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int NewAA_DlaaStatus(out int available, out int needsUpdatedDriver, out int minDriverMajor, out int minDriverMinor, out int featureInitResult);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int NewAA_DlaaCreate(VkCommandBuffer commandBuffer, uint renderWidth, uint renderHeight);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int NewAA_DlaaEvaluate(
		VkCommandBuffer commandBuffer,
		VkImage color, VkImageView colorView, VkFormat colorFormat,
		VkImage depth, VkImageView depthView, VkFormat depthFormat,
		VkImage motion, VkImageView motionView, VkFormat motionFormat,
		VkImage output, VkImageView outputView, VkFormat outputFormat,
		uint width, uint height,
		float jitterX, float jitterY,
		float motionScaleX, float motionScaleY,
		int reset);

	[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
	public static extern int NewAA_DlaaRelease();
}
