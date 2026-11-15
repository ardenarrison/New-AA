// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Logging;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA.Rendering;
using KSA.Rendering.PostProcessing;
using RenderCore;
using RenderCore.Pipelines;

namespace KSA.Mods.NewAA;

// Push constant for DlaaMotionFrag.frag. Layout matches its push_constant block.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DlaaMotionData
{
	public float4x4 PrevViewProjection;

	public float4x4 InvViewProjection;

	// x = 1/width, y = 1/height, z = width, w = height
	public float4 RtMetrics;

	// xyz = camera translation relative to the followed object, w = total camera motion (0 when still)
	public float4 CameraTranslation;
}

// Starts NVIDIA NGX once, after the GPU check passes. Keeps DLAA off if anything fails, so the game falls back to no anti-aliasing.
internal static class DlaaRuntime
{
	// NGX project ID for New AA (a GUID, as NVIDIA's SDK asks for when no application ID was issued) and the version sent with it.
	private const string ProjectId = "968cb9ed-5fa2-420d-b5c6-52ce9254da73";
	private const string EngineVersion = "0.1.0";

	private static bool _attempted;

	private static bool _ready;

	public static bool IsReady => _ready;

	public static bool EnsureInitialized(Renderer renderer)
	{
		if (_attempted)
		{
			return _ready;
		}
		_attempted = true;

		if (!DlaaSupport.IsCompatible)
		{
			return false;
		}

		string modFolder = Path.GetDirectoryName(typeof(DlaaRuntime).Assembly.Location) ?? string.Empty;
		string dllPath = Path.Combine(modFolder, "NewAANgx.dll");
		if (!File.Exists(dllPath))
		{
			Log("NewAANgx.dll not found in the mod folder. DLAA disabled, using no anti aliasing.");
			return false;
		}

		string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Kitten Space Agency", "NewAA", "ngx");
		Directory.CreateDirectory(dataPath);

		try
		{
			DlaaNative.Load(dllPath);
			// Adapter: the engine's native Vulkan handles. If these property names differ in your build, change only these lines.
			VkInstance instance = renderer.Instance;
			VkPhysicalDevice physicalDevice = renderer.PhysicalDevice;
			VkDevice device = renderer.Device.Handle;

			int initResult = DlaaNative.NewAA_NgxInit(ProjectId, EngineVersion, dataPath, modFolder, instance, physicalDevice, device, EngineGetInstanceProcAddr());
			Log("NGX messages are written to " + Path.Combine(dataPath, "ngx_log.txt"));
			if (initResult != DlaaNative.NgxSuccess)
			{
				Log("NGX initialization failed (result 0x" + initResult.ToString("X") + "). DLAA disabled, using no anti aliasing.");
				return false;
			}
			_ready = DlaaNative.NewAA_DlaaSupported() == 1;
			int statusResult = DlaaNative.NewAA_DlaaStatus(out int available, out int needsDriver, out int minMajor, out int minMinor, out int initResultCode);
			Log("NGX status on " + DlaaSupport.GpuName + ": query 0x" + statusResult.ToString("X")
				+ ", SuperSampling.Available=" + available
				+ ", NeedsUpdatedDriver=" + needsDriver
				+ ", MinDriver=" + minMajor + "." + minMinor
				+ ", FeatureInitResult=0x" + initResultCode.ToString("X"));
			Log(_ready ? "DLAA initialized on " + DlaaSupport.GpuName + "." : "NGX reports DLAA is not available on this device. Using no anti aliasing.");
		}
		catch (Exception ex)
		{
			Log("DLAA initialization threw an exception: " + ex.Message + ". Using no anti aliasing.");
			_ready = false;
		}
		return _ready;
	}

	// The engine's vkGetInstanceProcAddr, so NGX uses the same Vulkan loader as the device.
	private static unsafe nint EngineGetInstanceProcAddr()
	{
		return (nint)Vulkan.GetInstanceProcAddr.Invoke;
	}

	private static void Log(string message)
	{
		DefaultCategory.Log.Info("[DLAA] " + message, "DlaaRuntime", "NewAA", 0);
	}
}

// Per-viewport DLAA. Passes: tonemap composite -> camera motion vectors -> DLAA (NGX) -> copy to final.
// If the DLAA call fails for a frame, the copy falls back to the LDR scene.
public sealed class DlaaRenderer : IDisposable
{
	private readonly Renderer _renderer;

	private readonly RenderTarget _sceneLdrTarget;

	private readonly RenderTarget _motionTarget;

	private readonly RenderImage _output;

	private readonly VkImage _depthImage;

	private readonly VkImageView _depthView;

	private readonly ScreenspaceRenderer _sceneLdrComposite;

	private readonly ScreenspaceRenderer _motionPass;

	private readonly ScreenspaceRenderer _copyFromDlaa;

	private readonly ScreenspaceRenderer _copyFromLdr;

	// Motion vectors of vehicles and celestial bodies, drawn over the camera-only vectors.
	private readonly ScreenspaceRenderer _bodyPass;

	// Orbit lines drawn after DLSS. Null when the overlay could not be built, or for a viewport that is not the main one.
	private readonly DlaaLineOverlay? _overlay;

	private readonly ScreenspaceRenderer? _combineDlaa;

	private readonly ScreenspaceRenderer? _combineLdr;

	private readonly IViewport _viewport;

	private readonly bool _isMain;

	private bool _linesDrawn;

	private int _resetSeen;

	private bool _dlaaFrameOk;

	private int _lastCreateResult = int.MinValue;

	private int _lastEvaluateResult = int.MinValue;

	private bool _firstFrame = true;

	public VkExtent2D Extent { get; }

	// True when orbit lines will be drawn by the overlay after DLSS instead of inside the scene.
	public bool OverlayReady => _overlay != null;

	public DlaaRenderer(Renderer renderer, IViewport viewport, string name, int frameCount, RenderImage offscreenDepth, RenderTarget offscreenTarget, IRenderPassInfo finalPassInfo, ShaderReference[] compositeShaders, VkPushConstantRange compositeRange, VkSampler pointSampler, VkSampler linearSampler, ShaderReference[] motionShaders, ShaderReference[] bodyShaders, ShaderReference[] copyShaders, ShaderReference[] combineShaders)
	{
		_renderer = renderer;
		_viewport = viewport;
		_isMain = viewport.IsMain();
		Extent = offscreenTarget.Extent;
		_depthImage = offscreenDepth.Image;
		_depthView = offscreenDepth.ImageView;

		_sceneLdrTarget = AaPasses.CreateTarget(renderer, name + " Scene LDR", Extent, VkFormat.R8G8B8A8UNorm);
		_motionTarget = AaPasses.CreateTarget(renderer, name + " Motion", Extent, VkFormat.R16G16SFloat);
		_output = RenderImage.CreateColorStorage(renderer, name + " DLAA Output", Extent, VkFormat.R8G8B8A8UNorm);

		_sceneLdrComposite = AaPasses.CreatePass(renderer, _sceneLdrTarget, compositeShaders, 1, frameCount, pointSampler, compositeRange, false);
		_sceneLdrComposite.AddImage(offscreenTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_sceneLdrComposite.Build();

		VkPushConstantRange motionRange = new VkPushConstantRange
		{
			StageFlags = VkShaderStageFlags.FragmentBit,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<DlaaMotionData>()
		};
		_motionPass = AaPasses.CreatePass(renderer, _motionTarget, motionShaders, 1, frameCount, linearSampler, motionRange, true);
		_motionPass.AddImage(offscreenDepth.ImageView, VkImageLayout.DepthStencilReadOnlyOptimal);
		_motionPass.Build();

		_copyFromDlaa = AaPasses.CreatePass(renderer, finalPassInfo, copyShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
		_copyFromDlaa.AddImage(_output.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_copyFromDlaa.Build();

		_copyFromLdr = AaPasses.CreatePass(renderer, finalPassInfo, copyShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
		_copyFromLdr.AddImage(_sceneLdrTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_copyFromLdr.Build();

		VkPushConstantRange bodyRange = new VkPushConstantRange
		{
			StageFlags = VkShaderStageFlags.FragmentBit,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<DlaaBodyData>()
		};
		_bodyPass = AaPasses.CreatePass(renderer, _motionTarget, bodyShaders, 1, frameCount, linearSampler, bodyRange, true);
		_bodyPass.AddImage(offscreenDepth.ImageView, VkImageLayout.DepthStencilReadOnlyOptimal);
		_bodyPass.Build();

		if (_isMain && DlaaLinePipeline.EnsureBuilt(renderer, VkFormat.R8G8B8A8UNorm, offscreenDepth.Format))
		{
			try
			{
				_overlay = new DlaaLineOverlay(renderer, name, frameCount, Extent, compositeShaders, compositeRange, pointSampler);
				_combineDlaa = CreateCombine(renderer, finalPassInfo, combineShaders, frameCount, linearSampler, _output.ImageView);
				_combineLdr = CreateCombine(renderer, finalPassInfo, combineShaders, frameCount, linearSampler, _sceneLdrTarget.ColorImage.ImageView);
			}
			catch (Exception ex)
			{
				DefaultCategory.Log.Info("[DLAA] Orbit line overlay could not be created, orbit lines stay in the scene: " + ex.Message, "DlaaRenderer", "NewAA", 0);
				_overlay = null;
				_combineDlaa = null;
				_combineLdr = null;
			}
		}
	}

	// Final pass with orbit lines: base image (DLSS output or LDR scene), tonemapped lines, line coverage.
	private ScreenspaceRenderer CreateCombine(Renderer renderer, IRenderPassInfo finalPassInfo, ShaderReference[] combineShaders, int frameCount, VkSampler sampler, VkImageView baseView)
	{
		ScreenspaceRenderer combine = AaPasses.CreatePass(renderer, finalPassInfo, combineShaders, 3, frameCount, sampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
		combine.AddImage(baseView, VkImageLayout.ShaderReadOnlyOptimal);
		combine.AddImage(_overlay!.LineColorView, VkImageLayout.ShaderReadOnlyOptimal);
		combine.AddImage(_overlay.CoverageView, VkImageLayout.ShaderReadOnlyOptimal);
		combine.Build();
		return combine;
	}

	public void RenderPreparePasses(CommandBuffer commandBuffer, int frameIndex, in CompositeData compositeData, float jitterX, float jitterY)
	{
		int width = (int)Extent.Width;
		int height = (int)Extent.Height;

		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2LdrComposite))
		{
			AaPasses.BeginCleared(commandBuffer, _sceneLdrTarget);
			_sceneLdrComposite.Render(commandBuffer, frameIndex, compositeData);
			AaPasses.EndSampled(commandBuffer, _sceneLdrTarget);

			DlaaMotionData push = new DlaaMotionData
			{
				PrevViewProjection = TaaCamera.PreviousViewProjection,
				InvViewProjection = TaaCamera.CurrentInverseViewProjection,
				RtMetrics = new float4(1f / (float)width, 1f / (float)height, width, height),
				CameraTranslation = new float4(TaaCamera.CameraTranslation.X, TaaCamera.CameraTranslation.Y, TaaCamera.CameraTranslation.Z, TaaCamera.TotalCameraMotion)
			};
			AaPasses.BeginCleared(commandBuffer, _motionTarget);
			_motionPass.Render(commandBuffer, frameIndex, push);
			if (_isMain)
			{
				// Vehicles and celestial bodies overwrite the camera-only vectors inside their own volume.
				foreach (DlaaBodyMotion body in DlaaBodies.Draws)
				{
					DlaaBodyData bodyPush = new DlaaBodyData
					{
						PrevViewProjection = TaaCamera.PreviousViewProjection,
						InvViewProjection = TaaCamera.CurrentInverseViewProjection,
						Body = body
					};
					_bodyPass.Render(commandBuffer, frameIndex, bodyPush);
				}
			}
			AaPasses.EndSampled(commandBuffer, _motionTarget);
		}

		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2Blit))
		{
			// NGX writes the output as a storage image, so it has to be in the storage-write layout first.
			commandBuffer.PipelineBarrier2(_output, ImageBarrierInfo.Presets.StorageWriteC);

			// Adapter: the native command buffer handle. If the property name differs in your build, change only this line.
			VkCommandBuffer nativeCommandBuffer = commandBuffer.Handle;
			// DLSS starts its history over on the first frame and whenever the camera jumped (new followed object, mode switch, huge move).
			bool jumped = _resetSeen != TaaCamera.ResetFrameNumber;
			_resetSeen = TaaCamera.ResetFrameNumber;
			uint reset = (_firstFrame || jumped) ? 1u : 0u;
			int createResult = DlaaNative.NewAA_DlaaCreate(nativeCommandBuffer, (uint)width, (uint)height);
			int evaluateResult = createResult == DlaaNative.NgxSuccess
				? DlaaNative.NewAA_DlaaEvaluate(
					nativeCommandBuffer,
					_sceneLdrTarget.ColorImage.Image, _sceneLdrTarget.ColorImage.ImageView, VkFormat.R8G8B8A8UNorm,
					_depthImage, _depthView, VkFormat.D32SFloat,
					_motionTarget.ColorImage.Image, _motionTarget.ColorImage.ImageView, VkFormat.R16G16SFloat,
					_output.Image, _output.ImageView, VkFormat.R8G8B8A8UNorm,
					(uint)width, (uint)height,
					jitterX, jitterY,
					(float)width, (float)height,
					(int)reset)
				: -1;
			_dlaaFrameOk = createResult == DlaaNative.NgxSuccess && evaluateResult == DlaaNative.NgxSuccess;
			_firstFrame = false;

			// Log whenever the NGX results change, so the game log shows why DLAA is or isn't running.
			if (createResult != _lastCreateResult || evaluateResult != _lastEvaluateResult)
			{
				_lastCreateResult = createResult;
				_lastEvaluateResult = evaluateResult;
				string outcome = _dlaaFrameOk ? "DLAA running." : "falling back to no anti aliasing.";
				DefaultCategory.Log.Info("[DLAA] create 0x" + createResult.ToString("X") + ", evaluate 0x" + evaluateResult.ToString("X") + ", " + outcome, "DlaaRenderer", "NewAA", 0);
			}

			commandBuffer.PipelineBarrier2(_output, ImageBarrierInfo.Presets.SampledReadC);
		}

		_linesDrawn = false;
		if (_overlay != null && DlaaLineDeferral.IsDeferred(_viewport.ShaderSlot))
		{
			using (commandBuffer.TagRegion(Profiler.GpuTag.OrbitLines))
			{
				try
				{
					_linesDrawn = _overlay.Draw(commandBuffer, _viewport, frameIndex, compositeData);
				}
				catch (Exception ex)
				{
					DefaultCategory.Log.Info("[DLAA] Orbit line overlay failed: " + ex.Message, "DlaaRenderer", "NewAA", 0);
				}
			}
			DlaaLineDeferral.MarkDrawn(_viewport.ShaderSlot);
		}
	}

	public void RenderFinalBlit(CommandBuffer commandBuffer, int frameIndex)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2Blit))
		{
			ScreenspaceRenderer copy = _linesDrawn
				? (_dlaaFrameOk ? _combineDlaa! : _combineLdr!)
				: (_dlaaFrameOk ? _copyFromDlaa : _copyFromLdr);
			copy.Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		}
	}

	public void Dispose()
	{
		_combineLdr?.Dispose();
		_combineDlaa?.Dispose();
		_overlay?.Dispose();
		_bodyPass.Dispose();
		_copyFromLdr.Dispose();
		_copyFromDlaa.Dispose();
		_motionPass.Dispose();
		_sceneLdrComposite.Dispose();
		_output.Dispose();
		_motionTarget.Dispose();
		_sceneLdrTarget.Dispose();
	}
}

// Owns the DLAA renderers. Mirrors TaaState. Returns false from TryPrepare when DLAA can't run, so the original path runs.
internal static class DlaaState
{
	private static readonly Dictionary<int, DlaaRenderer> Renderers = new Dictionary<int, DlaaRenderer>();

	private static readonly string[] ShaderIds = { "TaaVert", "DlaaMotionFrag", "DlaaBodyMotionFrag", "DlaaCopyFrag", "DlaaLineVert", "DlaaLineFrag", "DlaaLineCombineFrag" };

	private static ShaderReference[]? _compositeShaders;

	private static VkPushConstantRange _compositeRange;

	private static VkSampler _linearSampler;

	private static bool _sharedReady;

	private static bool _warnedMissing;

	public static bool TryPrepare(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		if (!ShadersAvailable())
		{
			return false;
		}
		Renderer renderer = Program.GetRenderer();
		if (!DlaaRuntime.EnsureInitialized(renderer))
		{
			return false;
		}
		IViewport? viewport = FxaaState.FindViewport(shaderSlot);
		if (viewport == null || viewport.OffscreenTarget.DepthImage == null)
		{
			return false;
		}
		// The native plugin owns one DLSS feature. Running it for a second viewport (the game has a 128 x 128 one) made it tear down and
		// rebuild the feature every frame, so nothing ever accumulated. Only the main viewport uses DLAA. Others take the game's own path.
		if (!viewport.IsMain())
		{
			return false;
		}

		DlaaRenderer? dlaa = GetOrCreate(renderer, viewport, shaderSlot);
		if (dlaa == null)
		{
			return false;
		}
		if (viewport.IsMain())
		{
			// Work out how each vehicle and celestial body moved since last frame, for the motion vectors.
			bool jumped = TaaCamera.ResetFrameNumber == TaaCamera.FrameNumber;
			DlaaBodies.Capture(TaaCamera.FrameNumber, Program.GetMainCamera(), jumped, TaaCamera.CameraTranslation);
		}
		commandBuffer.PipelineBarrier2(viewport.OffscreenTarget.DepthImage, ImageBarrierInfo.Presets.DepthSampledReadC);
		dlaa.RenderPreparePasses(commandBuffer, frameIndex, Program.TonemapData, TaaCamera.JitterPixelsX, TaaCamera.JitterPixelsY);
		return true;
	}

	// True when this viewport has a DLAA renderer that will draw orbit lines after DLSS.
	public static bool CanDrawLines(int shaderSlot)
	{
		return DlaaRuntime.IsReady && Renderers.TryGetValue(shaderSlot, out DlaaRenderer? dlaa) && dlaa.OverlayReady;
	}

	public static bool TryFinal(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		if (!DlaaAntiAliasing.IsSelected || !Renderers.TryGetValue(shaderSlot, out DlaaRenderer? dlaa))
		{
			return false;
		}
		dlaa.RenderFinalBlit(commandBuffer, frameIndex);
		return true;
	}

	private static DlaaRenderer? GetOrCreate(Renderer renderer, IViewport viewport, int shaderSlot)
	{
		VkExtent2D extent = viewport.OffscreenTarget.Extent;
		if (Renderers.TryGetValue(shaderSlot, out DlaaRenderer? existing))
		{
			if (existing.Extent.Width == extent.Width && existing.Extent.Height == extent.Height)
			{
				return existing;
			}
			existing.Dispose();
			Renderers.Remove(shaderSlot);
		}
		EnsureShared();
		DlaaRenderer created = new DlaaRenderer(
			renderer,
			viewport,
			"DLAA Viewport " + shaderSlot,
			renderer.MaxFramesInFlight,
			viewport.OffscreenTarget.DepthImage!,
			viewport.OffscreenTarget,
			viewport.CompositeTarget,
			_compositeShaders!,
			_compositeRange,
			Program.PointClampedSampler,
			_linearSampler,
			new ShaderReference[2] { ModLibrary.Get<ShaderReference>("TaaVert"), ModLibrary.Get<ShaderReference>("DlaaMotionFrag") },
			new ShaderReference[2] { ModLibrary.Get<ShaderReference>("TaaVert"), ModLibrary.Get<ShaderReference>("DlaaBodyMotionFrag") },
			new ShaderReference[2] { ModLibrary.Get<ShaderReference>("TaaVert"), ModLibrary.Get<ShaderReference>("DlaaCopyFrag") },
			new ShaderReference[2] { ModLibrary.Get<ShaderReference>("TaaVert"), ModLibrary.Get<ShaderReference>("DlaaLineCombineFrag") });
		Renderers[shaderSlot] = created;
		return created;
	}

	private static bool ShadersAvailable()
	{
		foreach (string id in ShaderIds)
		{
			if (!ModLibrary.TryGet<ShaderReference>(id, out _))
			{
				if (!_warnedMissing)
				{
					_warnedMissing = true;
					DefaultCategory.Log.Info("[DLAA] Missing shader '" + id + "'. Using no anti aliasing.", "DlaaState", "NewAA", 0);
				}
				return false;
			}
		}
		return true;
	}

	private static void EnsureShared()
	{
		if (_sharedReady)
		{
			return;
		}
		_compositeShaders = new ShaderReference[2]
		{
			ModLibrary.Get<ShaderReference>("ScreenspaceVert"),
			ModLibrary.Get<ShaderReference>("CompositeFrag")
		};
		object rangeBoxed = AccessTools.Field(typeof(Program), "_compositePushConstantRange").GetValue(Program.Instance) ?? throw new InvalidOperationException("Program._compositePushConstantRange not found");
		_compositeRange = (VkPushConstantRange)rangeBoxed;
		_linearSampler = Program.GetRenderer().Device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
		_sharedReady = true;
	}
}
