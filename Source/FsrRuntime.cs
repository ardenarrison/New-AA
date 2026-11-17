// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.Logging;
using Brutal.VulkanApi;
using Core;
using HarmonyLib;
using RenderCore;

namespace KSA.Mods.NewAA;

// The menu entry for FSR Native AA (AMD FSR upscaling at a 1.0x ratio, so it only anti-aliases). Uses enum value 9, which the
// game's enum does not define. Works on any GPU the FSR runtime supports, not only AMD ones.
public static class FsrAntiAliasing
{
	public const int EnumValue = 9;

	public const string Label = "FSR Native AA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;

	public static bool IsSelected => GameSettings.Current.AntiAliasingMethod == Method;

	// AMD ships the FSR runtime (amd_fidelityfx_*_vk.dll) for Windows only, so FSR Native AA is not offered anywhere else.
	public static bool RuntimeExistsForThisSystem => OperatingSystem.IsWindows();
}

// Loads AMD's FSR runtime once, the first time FSR is selected. Keeps FSR off if anything fails, so the game falls back to no
// anti-aliasing. The upscaler itself lives in FsrUpscaler.
internal static unsafe class FsrRuntime
{
	private static bool _attempted;

	private static bool _ready;

	private static FfxVkDeviceContext _device;

	// Versions the runtime offers for this device, newest first, as the version query returned them.
	private static readonly List<(ulong Id, string Name)> Versions = new List<(ulong Id, string Name)>();

	public static bool IsReady => _ready;

	public static FfxVkDeviceContext Device => _device;

	public static IReadOnlyList<(ulong Id, string Name)> AvailableVersions => Versions;

	public static FsrUpscaler? Upscaler { get; private set; }

	public static bool EnsureInitialized(Renderer renderer)
	{
		if (_attempted)
		{
			return _ready;
		}
		_attempted = true;

		if (!FsrAntiAliasing.RuntimeExistsForThisSystem)
		{
			// Only reached when FSR is still selected from a settings file written on Windows.
			Log("AMD's FSR runtime exists for Windows only, so FSR Native AA is not available on this system. Using no anti aliasing.");
			return false;
		}

		string modFolder = Path.GetDirectoryName(typeof(FsrRuntime).Assembly.Location) ?? string.Empty;
		string loaderPath = Path.Combine(modFolder, FsrNative.LoaderDll);
		foreach (string name in new[] { FsrNative.LoaderDll, FsrNative.UpscalerDll })
		{
			if (!File.Exists(Path.Combine(modFolder, name)))
			{
				Log(name + " not found in the mod folder. FSR disabled, using no anti aliasing.");
				return false;
			}
		}
		string? layoutProblem = FsrNative.CheckLayout();
		if (layoutProblem != null)
		{
			Log("FSR API bindings do not match the headers (" + layoutProblem + "). FSR disabled, using no anti aliasing.");
			return false;
		}

		try
		{
			FsrNative.Load(loaderPath);
			// Adapter: the engine's native Vulkan handles, the same ones DLAA uses.
			VkInstance instance = renderer.Instance;
			VkPhysicalDevice physicalDevice = renderer.PhysicalDevice;
			VkDevice device = renderer.Device.Handle;
			_device = new FfxVkDeviceContext
			{
				Device = device.VkHandle,
				PhysicalDevice = physicalDevice.VkHandle,
				GetDeviceProcAddr = EngineGetDeviceProcAddr(instance)
			};
			if (_device.GetDeviceProcAddr == 0)
			{
				Log("vkGetDeviceProcAddr not found. FSR disabled, using no anti aliasing.");
				return false;
			}

			uint queryResult = FsrApi.QueryVersions(_device, Versions);
			if (queryResult != FsrNative.ReturnOk)
			{
				Log("FSR version query failed (" + queryResult + ").");
			}
			if (Versions.Count == 0)
			{
				Log("The FSR runtime offers no upscaler for this GPU. Using no anti aliasing.");
				return false;
			}
			List<string> names = Versions.ConvertAll(v => v.Name);
			Log("FSR runtime loaded. Upscaler versions for this GPU: " + string.Join(", ", names) + ".");
			Upscaler = new FsrUpscaler(renderer.MaxFramesInFlight);
			_ready = true;
		}
		catch (Exception ex)
		{
			Log("FSR initialization threw an exception: " + ex.Message + ". Using no anti aliasing.");
			_ready = false;
		}
		return _ready;
	}

	// Called when no FSR version could create a context. Stops the camera jitter and keeps the game on its own path.
	public static void Disable(string reason)
	{
		if (!_ready)
		{
			return;
		}
		_ready = false;
		Log(reason + " FSR disabled for this session, using no anti aliasing.");
	}

	private static readonly AccessTools.FieldRef<Camera, ViewProjection> CameraVp = AccessTools.FieldRefAccess<Camera, ViewProjection>("_vp");

	// What FSR needs to turn depth into view-space distance. The game's perspective projection uses NearPlane and FarPlane, its
	// orthographic one (Camera.UpdateProjection) NearPlane and 50 km. A perspective matrix has M34 = +-1, an orthographic one 0;
	// the jitter does not change M34.
	public static void CameraPlanes(Camera camera, out float near, out float far, out float fovVertical)
	{
		bool perspective = Math.Abs(CameraVp(camera).projection.M34) > 0.5f;
		near = camera.NearPlane;
		far = perspective ? camera.FarPlane : 50000f;
		fovVertical = camera.GetFieldOfView();
	}

	// The engine's vkGetDeviceProcAddr, so FSR uses the same loader and function table as the device.
	private static nint EngineGetDeviceProcAddr(VkInstance instance)
	{
		ReadOnlySpan<byte> name = "vkGetDeviceProcAddr\0"u8;
		fixed (byte* namePointer = name)
		{
			PfnVkVoidFunction function = Vulkan.GetInstanceProcAddr.Invoke(instance, namePointer);
			return (nint)function.Invoke;
		}
	}

	public static void Log(string message)
	{
		DefaultCategory.Log.Info("[FSR] " + message, "FsrRuntime", "NewAA", 0);
	}
}

// One FSR upscaler context for the main viewport, at native resolution (render size = output size, which is FSR's Native AA mode).
// A new size needs a new context. The old one may still be in use by frames the GPU has not finished, so it is destroyed a few
// frames later instead of straight away.
internal sealed unsafe class FsrUpscaler
{
	// Runtime messages logged per session. FSR can repeat a warning every frame.
	private const int MaxMessagesLogged = 40;

	private static int _messagesLogged;

	private readonly int _framesInFlight;

	private readonly List<(nint Context, long DestroyAtFrame)> _retired = new List<(nint, long)>();

	private readonly Stopwatch _clock = new Stopwatch();

	private nint _context;

	private uint _width;

	private uint _height;

	private long _frame;

	// 0: the runtime picks the version (newest for this GPU). Otherwise the version that worked when the default did not.
	private ulong _versionOverride;

	private bool _versionChosen;

	public string ProviderName { get; private set; } = string.Empty;

	public FsrUpscaler(int framesInFlight)
	{
		_framesInFlight = Math.Max(framesInFlight, 1);
	}

	// Makes sure a context for this size exists. Returns FFX_API_RETURN_OK or the creation error.
	public uint EnsureContext(uint width, uint height)
	{
		DestroyRetired();
		if (_context != 0 && _width == width && _height == height)
		{
			return FsrNative.ReturnOk;
		}
		if (_context != 0)
		{
			_retired.Add((_context, _frame + _framesInFlight + 2));
			_context = 0;
		}

		uint result;
		if (_versionChosen)
		{
			result = Create(width, height, _versionOverride);
		}
		else
		{
			// Let the runtime pick first. If that version cannot run on this device (FSR 4 needs features some GPUs lack), try the
			// others, newest first, and keep the first one that works for every later context.
			result = Create(width, height, 0);
			if (result != FsrNative.ReturnOk)
			{
				FsrRuntime.Log("Default FSR version failed to start (" + result + "). Trying the other versions.");
				foreach ((ulong id, string name) in FsrRuntime.AvailableVersions)
				{
					result = Create(width, height, id);
					FsrRuntime.Log("  " + name + ": " + (result == FsrNative.ReturnOk ? "ok" : "failed (" + result + ")"));
					if (result == FsrNative.ReturnOk)
					{
						_versionOverride = id;
						break;
					}
				}
			}
			_versionChosen = result == FsrNative.ReturnOk;
		}
		if (result != FsrNative.ReturnOk)
		{
			return result;
		}
		_width = width;
		_height = height;
		ProviderName = QueryProviderName();
		FsrRuntime.Log("FSR context created: " + ProviderName + ", " + width + " x " + height + " (Native AA).");
		_clock.Restart();
		return result;
	}

	private uint Create(uint width, uint height, ulong versionOverride)
	{
		uint result = FsrApi.CreateUpscaleContext(FsrRuntime.Device, width, height, versionOverride, (delegate* unmanaged[Cdecl]<uint, char*, void>)&OnMessage, out nint context);
		if (result == FsrNative.ReturnOk)
		{
			_context = context;
		}
		return result;
	}

	private string QueryProviderName()
	{
		return FsrApi.ProviderName(_context);
	}

	// Records FSR into the command buffer. Color, depth and motion must be in SHADER_READ_ONLY_OPTIMAL and the output in GENERAL;
	// FSR puts them back in those layouts when it is done. jitter is in render pixels, motion in UV units (current to previous).
	public uint Dispatch(nint commandBuffer, nint color, nint depth, nint motion, nint output, uint width, uint height,
		float jitterX, float jitterY, bool reset, float cameraNear, float cameraFar, float fovVertical)
	{
		_frame++;
		if (_context == 0)
		{
			return FsrNative.ReturnNoProvider;
		}
		// Milliseconds since the last dispatch. FSR wants about 16.6 at 60 fps and warns below 1.
		float frameTime = _clock.IsRunning ? (float)_clock.Elapsed.TotalMilliseconds : 16.6f;
		_clock.Restart();
		frameTime = Math.Clamp(frameTime, 1f, 200f);
		return FsrApi.Dispatch(_context, commandBuffer, color, depth, motion, output, width, height, jitterX, jitterY, reset, frameTime, cameraNear, cameraFar, fovVertical);
	}

	// Destroys the old contexts that the GPU can no longer be using.
	private void DestroyRetired()
	{
		for (int i = _retired.Count - 1; i >= 0; i--)
		{
			if (_frame >= _retired[i].DestroyAtFrame)
			{
				FsrApi.DestroyContext(_retired[i].Context);
				_retired.RemoveAt(i);
			}
		}
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static void OnMessage(uint type, char* message)
	{
		try
		{
			if (_messagesLogged >= MaxMessagesLogged || message == null)
			{
				return;
			}
			_messagesLogged++;
			string text = new string(message);
			FsrRuntime.Log((type == FsrNative.MessageTypeError ? "runtime error: " : "runtime warning: ") + text
				+ (_messagesLogged == MaxMessagesLogged ? " (further FSR messages are not logged)" : string.Empty));
		}
		catch
		{
			// Never let an exception cross back into native code.
		}
	}
}
