// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KSA.Mods.NewAA;

// C# bindings for the AMD FSR API (ffx_api.h, vk/ffx_api_vk.h, ffx_upscale.h from AMD's FSR SDK 2.4.0).
// The API is five C functions in amd_fidelityfx_loader_vk.dll that take chains of descriptor structs. The loader finds the
// effect DLL (amd_fidelityfx_upscaler_vk.dll) in its own folder, so both live in the mod folder.
// Constants, struct sizes and field offsets were read from the SDK headers with MSVC (x64). CheckLayout compares the sizes at run
// time, so a mismatch turns FSR off instead of corrupting memory.
internal static unsafe class FsrNative
{
	public const string LoaderDll = "amd_fidelityfx_loader_vk.dll";

	public const string UpscalerDll = "amd_fidelityfx_upscaler_vk.dll";

	// FfxApiReturnCodes
	public const uint ReturnOk = 0;

	public const uint ReturnNoProvider = 4;

	// Descriptor struct types
	public const ulong CreateContextBackendVk = 0x1000003;

	public const ulong CreateContextUpscale = 0x10000;

	public const ulong CreateContextUpscaleVersion = 0x1000B;

	public const ulong DispatchUpscale = 0x10001;

	public const ulong QueryGetVersions = 0x4;

	public const ulong OverrideVersion = 0x5;

	public const ulong QueryGetProviderVersion = 0x6;

	// FFX_UPSCALER_VERSION of the headers these bindings follow (4.1.2).
	public const uint UpscalerApiVersion = 0x1001002;

	// FfxApiCreateContextUpscaleFlags
	public const uint EnableDepthInverted = 1u << 3;

	public const uint EnableNonLinearColorspace = 1u << 8;

	// FfxApiDispatchFsrUpscaleFlags
	public const uint FlagNonLinearColorSrgb = 1u << 1;

	// Resource descriptions
	public const uint ResourceTypeTexture2D = 2;

	public const uint SurfaceFormatR8G8B8A8Unorm = 0xA;

	public const uint SurfaceFormatR16G16Float = 0x12;

	public const uint SurfaceFormatR32Float = 0x1C;

	public const uint ResourceUsageReadOnly = 0;

	public const uint ResourceUsageUav = 1u << 1;

	public const uint ResourceUsageDepthTarget = 1u << 2;

	// FfxApiResourceState. PIXEL_COMPUTE_READ is VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, UNORDERED_ACCESS is VK_IMAGE_LAYOUT_GENERAL.
	public const uint StateUnorderedAccess = 1u << 1;

	public const uint StatePixelComputeRead = (1u << 2) | (1u << 3);

	public const uint MessageTypeError = 0;

	private static IntPtr _handle;

	private static delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, void*, uint> _createContext;

	private static delegate* unmanaged[Cdecl]<nint*, void*, uint> _destroyContext;

	private static delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, uint> _query;

	private static delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, uint> _dispatch;

	public static bool IsLoaded => _handle != IntPtr.Zero;

	// Loads the loader DLL by path (it does not have to be on the search path) and looks up the API functions.
	public static void Load(string loaderPath)
	{
		if (_handle != IntPtr.Zero)
		{
			return;
		}
		IntPtr handle = NativeLibrary.Load(loaderPath);
		_createContext = (delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, void*, uint>)NativeLibrary.GetExport(handle, "ffxCreateContext");
		_destroyContext = (delegate* unmanaged[Cdecl]<nint*, void*, uint>)NativeLibrary.GetExport(handle, "ffxDestroyContext");
		_query = (delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, uint>)NativeLibrary.GetExport(handle, "ffxQuery");
		_dispatch = (delegate* unmanaged[Cdecl]<nint*, FfxApiHeader*, uint>)NativeLibrary.GetExport(handle, "ffxDispatch");
		_handle = handle;
	}

	public static uint CreateContext(nint* context, FfxApiHeader* desc)
	{
		return _createContext(context, desc, null);
	}

	public static uint DestroyContext(nint* context)
	{
		return _destroyContext(context, null);
	}

	// context may be null for the queries the API allows without one (versions, for example).
	public static uint Query(nint* context, FfxApiHeader* desc)
	{
		return _query(context, desc);
	}

	public static uint Dispatch(nint* context, FfxApiHeader* desc)
	{
		return _dispatch(context, desc);
	}

	// Returns null when every struct has the size the C headers give it, otherwise a description of the first mismatch.
	public static string? CheckLayout()
	{
		(string Name, int Actual, int Expected)[] sizes =
		{
			("ffxApiHeader", sizeof(FfxApiHeader), 16),
			("FfxApiResourceDescription", sizeof(FfxApiResourceDescription), 32),
			("FfxApiResource", sizeof(FfxApiResource), 48),
			("ffxCreateBackendVKDesc", sizeof(FfxCreateBackendVkDesc), 40),
			("ffxCreateContextDescUpscale", sizeof(FfxCreateContextDescUpscale), 48),
			("ffxCreateContextDescUpscaleVersion", sizeof(FfxCreateContextDescUpscaleVersion), 24),
			("ffxDispatchDescUpscale", sizeof(FfxDispatchDescUpscale), 432),
			("ffxQueryDescGetVersions", sizeof(FfxQueryDescGetVersions), 56),
			("ffxOverrideVersion", sizeof(FfxOverrideVersion), 24),
			("ffxQueryGetProviderVersion", sizeof(FfxQueryGetProviderVersion), 32)
		};
		foreach ((string name, int actual, int expected) in sizes)
		{
			if (actual != expected)
			{
				return name + " is " + actual + " bytes, the FSR API expects " + expected;
			}
		}
		return null;
	}
}

// The FSR calls New AA makes, with no dependency on the game, so a stand-alone test can run exactly this code.
internal static unsafe class FsrApi
{
	// Fills versions with the upscaler versions the runtime offers for this device, newest first. Returns the query result.
	public static uint QueryVersions(FfxVkDeviceContext device, List<(ulong Id, string Name)> versions)
	{
		ulong count = 0;
		FfxQueryDescGetVersions query = new FfxQueryDescGetVersions
		{
			Header = new FfxApiHeader { Type = FsrNative.QueryGetVersions },
			CreateDescType = FsrNative.CreateContextUpscale,
			Device = &device,
			OutputCount = &count
		};
		uint result = FsrNative.Query(null, &query.Header);
		if (result != FsrNative.ReturnOk || count == 0)
		{
			return result;
		}
		ulong* ids = stackalloc ulong[(int)count];
		byte** names = stackalloc byte*[(int)count];
		query.VersionIds = ids;
		query.VersionNames = names;
		result = FsrNative.Query(null, &query.Header);
		if (result != FsrNative.ReturnOk)
		{
			return result;
		}
		// The API may overwrite the name strings on a later query, so copy them now.
		for (ulong i = 0; i < count; i++)
		{
			string name = names[i] != null ? Marshal.PtrToStringUTF8((IntPtr)names[i]) ?? "?" : "?";
			versions.Add((ids[i], name));
		}
		return result;
	}

	// Creates an upscaler context for Native AA (render size = output size). versionOverride 0 lets the runtime pick the version.
	// The game's tonemap writes gamma-encoded LDR colour (no HDR flag, no auto exposure) and uses reverse-Z depth.
	// message is an ffxApiMessage function pointer, or null.
	public static uint CreateUpscaleContext(FfxVkDeviceContext device, uint width, uint height, ulong versionOverride, void* message, out nint context)
	{
		FfxCreateContextDescUpscaleVersion version = new FfxCreateContextDescUpscaleVersion
		{
			Header = new FfxApiHeader { Type = FsrNative.CreateContextUpscaleVersion },
			Version = FsrNative.UpscalerApiVersion
		};
		FfxCreateBackendVkDesc backend = new FfxCreateBackendVkDesc
		{
			Header = new FfxApiHeader { Type = FsrNative.CreateContextBackendVk, Next = &version.Header },
			DeviceContext = device
		};
		FfxOverrideVersion overrideVersion = new FfxOverrideVersion
		{
			Header = new FfxApiHeader { Type = FsrNative.OverrideVersion },
			VersionId = versionOverride
		};
		if (versionOverride != 0)
		{
			version.Header.Next = &overrideVersion.Header;
		}
		FfxCreateContextDescUpscale create = new FfxCreateContextDescUpscale
		{
			Header = new FfxApiHeader { Type = FsrNative.CreateContextUpscale, Next = &backend.Header },
			Flags = FsrNative.EnableDepthInverted | FsrNative.EnableNonLinearColorspace,
			MaxRenderSize = new FfxApiDimensions2D(width, height),
			MaxUpscaleSize = new FfxApiDimensions2D(width, height),
			Message = message
		};
		nint created = 0;
		uint result = FsrNative.CreateContext(&created, &create.Header);
		context = result == FsrNative.ReturnOk ? created : 0;
		return result;
	}

	public static void DestroyContext(nint context)
	{
		FsrNative.DestroyContext(&context);
	}

	// The display name of the version a context runs, for example "FSR 3.1.6".
	public static string ProviderName(nint context)
	{
		FfxQueryGetProviderVersion query = new FfxQueryGetProviderVersion
		{
			Header = new FfxApiHeader { Type = FsrNative.QueryGetProviderVersion }
		};
		uint result = FsrNative.Query(&context, &query.Header);
		if (result != FsrNative.ReturnOk || query.VersionName == null)
		{
			return "FSR (version unknown)";
		}
		return Marshal.PtrToStringUTF8((IntPtr)query.VersionName) ?? "FSR";
	}

	// Records one FSR Native AA frame. Color (R8G8B8A8_UNORM), depth (D32_SFLOAT) and motion (R16G16_SFLOAT, UV units, current to
	// previous) must be in SHADER_READ_ONLY_OPTIMAL and the output (R8G8B8A8_UNORM storage) in GENERAL. FSR leaves them in those
	// layouts. jitter is in render pixels, frameTimeMs in milliseconds, fovVertical in radians.
	public static uint Dispatch(nint context, nint commandBuffer, nint color, nint depth, nint motion, nint output, uint width, uint height,
		float jitterX, float jitterY, bool reset, float frameTimeMs, float cameraNear, float cameraFar, float fovVertical)
	{
		FfxDispatchDescUpscale dispatch = new FfxDispatchDescUpscale
		{
			Header = new FfxApiHeader { Type = FsrNative.DispatchUpscale },
			CommandList = (void*)commandBuffer,
			Color = FfxApiResource.Image2D(color, FsrNative.SurfaceFormatR8G8B8A8Unorm, width, height, FsrNative.ResourceUsageReadOnly, FsrNative.StatePixelComputeRead),
			// DEPTHTARGET makes FSR view the image with the depth aspect, as D32_SFLOAT.
			Depth = FfxApiResource.Image2D(depth, FsrNative.SurfaceFormatR32Float, width, height, FsrNative.ResourceUsageDepthTarget, FsrNative.StatePixelComputeRead),
			MotionVectors = FfxApiResource.Image2D(motion, FsrNative.SurfaceFormatR16G16Float, width, height, FsrNative.ResourceUsageReadOnly, FsrNative.StatePixelComputeRead),
			Output = FfxApiResource.Image2D(output, FsrNative.SurfaceFormatR8G8B8A8Unorm, width, height, FsrNative.ResourceUsageUav, FsrNative.StateUnorderedAccess),
			// Same convention as DLSS: the sub-pixel offset the projection was moved by, in pixels, +y down.
			JitterOffset = new FfxApiFloatCoords2D(jitterX, jitterY),
			MotionVectorScale = new FfxApiFloatCoords2D(width, height),
			RenderSize = new FfxApiDimensions2D(width, height),
			UpscaleSize = new FfxApiDimensions2D(width, height),
			EnableSharpening = 0,
			Sharpness = 0f,
			FrameTimeDelta = frameTimeMs,
			PreExposure = 1f,
			Reset = (byte)(reset ? 1 : 0),
			CameraNear = cameraNear,
			CameraFar = cameraFar,
			CameraFovAngleVertical = fovVertical,
			ViewSpaceToMetersFactor = 1f,
			Flags = FsrNative.FlagNonLinearColorSrgb
		};
		return FsrNative.Dispatch(&context, &dispatch.Header);
	}
}

// struct ffxApiHeader
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FfxApiHeader
{
	public ulong Type;

	public FfxApiHeader* Next;
}

// struct FfxApiDimensions2D
[StructLayout(LayoutKind.Sequential)]
internal struct FfxApiDimensions2D
{
	public uint Width;

	public uint Height;

	public FfxApiDimensions2D(uint width, uint height)
	{
		Width = width;
		Height = height;
	}
}

// struct FfxApiFloatCoords2D
[StructLayout(LayoutKind.Sequential)]
internal struct FfxApiFloatCoords2D
{
	public float X;

	public float Y;

	public FfxApiFloatCoords2D(float x, float y)
	{
		X = x;
		Y = y;
	}
}

// struct FfxApiResourceDescription (the width/height/depth members are unions with size/stride/alignment for buffers)
[StructLayout(LayoutKind.Sequential)]
internal struct FfxApiResourceDescription
{
	public uint Type;

	public uint Format;

	public uint Width;

	public uint Height;

	public uint Depth;

	public uint MipCount;

	public uint Flags;

	public uint Usage;
}

// struct FfxApiResource. On Vulkan, Resource is the VkImage. The API makes its own image views.
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct FfxApiResource
{
	[FieldOffset(0)]
	public void* Resource;

	[FieldOffset(8)]
	public FfxApiResourceDescription Description;

	[FieldOffset(40)]
	public uint State;

	// A 2D image with one mip and one layer, in the given state.
	public static FfxApiResource Image2D(nint image, uint format, uint width, uint height, uint usage, uint state)
	{
		return new FfxApiResource
		{
			Resource = (void*)image,
			Description = new FfxApiResourceDescription
			{
				Type = FsrNative.ResourceTypeTexture2D,
				Format = format,
				Width = width,
				Height = height,
				Depth = 1,
				MipCount = 1,
				Flags = 0,
				Usage = usage
			},
			State = state
		};
	}
}

// struct VkDeviceContext (vk/ffx_api_vk.h)
[StructLayout(LayoutKind.Sequential)]
internal struct FfxVkDeviceContext
{
	public nint Device;

	public nint PhysicalDevice;

	// PFN_vkGetDeviceProcAddr
	public nint GetDeviceProcAddr;
}

// struct ffxCreateBackendVKDesc
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct FfxCreateBackendVkDesc
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public FfxVkDeviceContext DeviceContext;
}

// struct ffxCreateContextDescUpscale
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct FfxCreateContextDescUpscale
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public uint Flags;

	[FieldOffset(20)]
	public FfxApiDimensions2D MaxRenderSize;

	[FieldOffset(28)]
	public FfxApiDimensions2D MaxUpscaleSize;

	// ffxApiMessage: void (*)(uint32_t type, const wchar_t* message)
	[FieldOffset(40)]
	public void* Message;
}

// struct ffxCreateContextDescUpscaleVersion
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct FfxCreateContextDescUpscaleVersion
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public uint Version;
}

// struct ffxDispatchDescUpscale. The C bool members are one byte each.
[StructLayout(LayoutKind.Explicit, Size = 432)]
internal unsafe struct FfxDispatchDescUpscale
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	// VkCommandBuffer
	[FieldOffset(16)]
	public void* CommandList;

	[FieldOffset(24)]
	public FfxApiResource Color;

	[FieldOffset(72)]
	public FfxApiResource Depth;

	[FieldOffset(120)]
	public FfxApiResource MotionVectors;

	[FieldOffset(168)]
	public FfxApiResource Exposure;

	[FieldOffset(216)]
	public FfxApiResource Reactive;

	[FieldOffset(264)]
	public FfxApiResource TransparencyAndComposition;

	[FieldOffset(312)]
	public FfxApiResource Output;

	[FieldOffset(360)]
	public FfxApiFloatCoords2D JitterOffset;

	[FieldOffset(368)]
	public FfxApiFloatCoords2D MotionVectorScale;

	[FieldOffset(376)]
	public FfxApiDimensions2D RenderSize;

	[FieldOffset(384)]
	public FfxApiDimensions2D UpscaleSize;

	[FieldOffset(392)]
	public byte EnableSharpening;

	[FieldOffset(396)]
	public float Sharpness;

	// Milliseconds.
	[FieldOffset(400)]
	public float FrameTimeDelta;

	[FieldOffset(404)]
	public float PreExposure;

	[FieldOffset(408)]
	public byte Reset;

	[FieldOffset(412)]
	public float CameraNear;

	[FieldOffset(416)]
	public float CameraFar;

	// Radians.
	[FieldOffset(420)]
	public float CameraFovAngleVertical;

	[FieldOffset(424)]
	public float ViewSpaceToMetersFactor;

	[FieldOffset(428)]
	public uint Flags;
}

// struct ffxQueryDescGetVersions
[StructLayout(LayoutKind.Explicit, Size = 56)]
internal unsafe struct FfxQueryDescGetVersions
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public ulong CreateDescType;

	// On Vulkan, a VkDeviceContext*.
	[FieldOffset(24)]
	public void* Device;

	[FieldOffset(32)]
	public ulong* OutputCount;

	[FieldOffset(40)]
	public ulong* VersionIds;

	[FieldOffset(48)]
	public byte** VersionNames;
}

// struct ffxOverrideVersion
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct FfxOverrideVersion
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public ulong VersionId;
}

// struct ffxQueryGetProviderVersion
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal unsafe struct FfxQueryGetProviderVersion
{
	[FieldOffset(0)]
	public FfxApiHeader Header;

	[FieldOffset(16)]
	public ulong VersionId;

	[FieldOffset(24)]
	public byte* VersionName;
}
