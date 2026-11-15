// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal;
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

// The new Anti-Aliasing menu entry. Uses enum value 7, which the game's enum does not define.
public static class TaaAntiAliasing
{
	public const int EnumValue = 7;

	// Off: TAA is not offered in the dropdown. Set to true to bring it back.
	public const bool Enabled = false;

	public const string Label = "TAA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;
}

// Push constant for TaaResolveFrag.frag. Layout matches its push_constant block.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TaaData
{
	public float4x4 PrevViewProjection;

	public float4x4 InvViewProjection;

	public float4 RtMetrics;

	public float HistoryWeight;

	public float CameraMotion;
}

// Per-viewport TAA. Passes: tonemap composite -> resolve (writes one history buffer, reads the other) -> copy to final.
// The history buffers alternate each frame.
public sealed class TaaRenderer : IDisposable
{
	private const float HistoryWeight = 0.9f;

	private readonly RenderTarget _sceneLdrTarget;

	private readonly RenderTarget[] _historyTargets;

	private readonly ScreenspaceRenderer _sceneLdrComposite;

	private readonly ScreenspaceRenderer[] _resolvePasses;

	private readonly ScreenspaceRenderer[] _copyPasses;

	private int _writeIndex;

	private int _activeIndex;

	private bool _historyValid;

	public VkExtent2D Extent { get; }

	public TaaRenderer(Renderer renderer, string name, int frameCount, RenderTarget offscreenTarget, IRenderPassInfo finalPassInfo, ShaderReference[] compositeShaders, VkPushConstantRange compositeRange, VkSampler pointSampler, VkSampler linearSampler, ShaderReference[] resolveShaders, ShaderReference[] copyShaders, VkImageView depthView)
	{
		Extent = offscreenTarget.Extent;
		_sceneLdrTarget = AaPasses.CreateTarget(renderer, name + " Scene LDR", Extent, VkFormat.R8G8B8A8UNorm);
		_historyTargets = new RenderTarget[2]
		{
			AaPasses.CreateTarget(renderer, name + " History 0", Extent, VkFormat.R16G16B16A16SFloat),
			AaPasses.CreateTarget(renderer, name + " History 1", Extent, VkFormat.R16G16B16A16SFloat)
		};

		_sceneLdrComposite = AaPasses.CreatePass(renderer, _sceneLdrTarget, compositeShaders, 1, frameCount, pointSampler, compositeRange, false);
		_sceneLdrComposite.AddImage(offscreenTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_sceneLdrComposite.Build();

		VkPushConstantRange resolveRange = new VkPushConstantRange
		{
			StageFlags = VkShaderStageFlags.FragmentBit,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<TaaData>()
		};
		_resolvePasses = new ScreenspaceRenderer[2];
		_copyPasses = new ScreenspaceRenderer[2];
		for (int i = 0; i < 2; i++)
		{
			// Pass i writes history i and reads history 1 - i.
			int read = 1 - i;
			ScreenspaceRenderer resolve = AaPasses.CreatePass(renderer, _historyTargets[i], resolveShaders, 3, frameCount, linearSampler, resolveRange, true);
			resolve.AddImage(_sceneLdrTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.AddImage(_historyTargets[read].ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.AddImage(depthView, VkImageLayout.DepthStencilReadOnlyOptimal);
			resolve.Build();
			_resolvePasses[i] = resolve;

			ScreenspaceRenderer copy = AaPasses.CreatePass(renderer, finalPassInfo, copyShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
			copy.AddImage(_historyTargets[i].ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			copy.Build();
			_copyPasses[i] = copy;
		}
	}

	// Composite, then resolve into this frame's history buffer. Call after the depth image is readable.
	public void RenderPreparePasses(CommandBuffer commandBuffer, int frameIndex, in CompositeData compositeData)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2LdrComposite))
		{
			AaPasses.BeginCleared(commandBuffer, _sceneLdrTarget);
			_sceneLdrComposite.Render(commandBuffer, frameIndex, compositeData);
			AaPasses.EndSampled(commandBuffer, _sceneLdrTarget);

			TaaData push = new TaaData
			{
				PrevViewProjection = TaaCamera.PreviousViewProjection,
				InvViewProjection = TaaCamera.CurrentInverseViewProjection,
				RtMetrics = new float4(1f / (float)Extent.Width, 1f / (float)Extent.Height, Extent.Width, Extent.Height),
				HistoryWeight = _historyValid ? HistoryWeight : 0f,
				CameraMotion = TaaCamera.CameraMotion
			};
			int write = _writeIndex;
			AaPasses.BeginCleared(commandBuffer, _historyTargets[write]);
			_resolvePasses[write].Render(commandBuffer, frameIndex, push);
			AaPasses.EndSampled(commandBuffer, _historyTargets[write]);

			_activeIndex = write;
		}
		_writeIndex = 1 - _writeIndex;
		_historyValid = true;
	}

	// Copies the resolved frame to the final target. Recorded inside the same pass as the original final composite.
	public void RenderFinalBlit(CommandBuffer commandBuffer, int frameIndex)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2Blit))
		{
			_copyPasses[_activeIndex].Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		}
	}

	public void Dispose()
	{
		for (int i = 0; i < 2; i++)
		{
			_copyPasses[i].Dispose();
			_resolvePasses[i].Dispose();
		}
		_sceneLdrComposite.Dispose();
		for (int i = 0; i < 2; i++)
		{
			_historyTargets[i].Dispose();
		}
		_sceneLdrTarget.Dispose();
	}
}

// Owns the TAA renderers and their shared resources. Mirrors SmaaState.
internal static class TaaState
{
	private static readonly Dictionary<int, TaaRenderer> Renderers = new Dictionary<int, TaaRenderer>();

	private static readonly string[] ShaderIds = { "TaaVert", "TaaResolveFrag", "TaaCopyFrag" };

	private static ShaderReference[]? _compositeShaders;

	private static VkPushConstantRange _compositeRange;

	private static VkSampler _linearSampler;

	private static bool _sharedReady;

	private static bool _warnedMissing;

	public static bool IsActive => TaaAntiAliasing.Enabled && GameSettings.Current.AntiAliasingMethod == TaaAntiAliasing.Method;

	private static bool ResourcesAvailable()
	{
		foreach (string id in ShaderIds)
		{
			if (!ModLibrary.TryGet<ShaderReference>(id, out _))
			{
				if (!_warnedMissing)
				{
					_warnedMissing = true;
					Console.Error.WriteLine("[TAA] Missing shader '" + id + "'. Falling back to no anti-aliasing.");
				}
				return false;
			}
		}
		return true;
	}

	// Returns true when the TAA prepare pass ran. Returns false when the original path should run.
	public static bool TryPrepare(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		TaaRenderer? taa = GetOrCreate(shaderSlot);
		IViewport? viewport = FxaaState.FindViewport(shaderSlot);
		if (taa == null || viewport == null)
		{
			return false;
		}
		// Same depth barrier the engine uses before sampling the offscreen depth.
		commandBuffer.PipelineBarrier2(viewport.OffscreenTarget.DepthImage!, ImageBarrierInfo.Presets.DepthSampledReadC);
		taa.RenderPreparePasses(commandBuffer, frameIndex, Program.TonemapData);
		return true;
	}

	// Returns true when the TAA final pass ran. Never creates anything, so it matches TryPrepare.
	public static bool TryFinal(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		TaaRenderer? taa = GetExisting(shaderSlot);
		if (taa == null)
		{
			return false;
		}
		taa.RenderFinalBlit(commandBuffer, frameIndex);
		return true;
	}

	private static TaaRenderer? GetOrCreate(int shaderSlot)
	{
		if (!IsActive || !ResourcesAvailable())
		{
			return null;
		}
		IViewport? viewport = FxaaState.FindViewport(shaderSlot);
		if (viewport == null)
		{
			return null;
		}
		VkExtent2D extent = viewport.OffscreenTarget.Extent;
		if (Renderers.TryGetValue(shaderSlot, out TaaRenderer? existing))
		{
			if (existing.Extent.Width == extent.Width && existing.Extent.Height == extent.Height)
			{
				return existing;
			}
			// Resize: new history buffers, so history starts over.
			existing.Dispose();
			Renderers.Remove(shaderSlot);
		}
		EnsureShared();
		Renderer renderer = Program.GetRenderer();
		TaaRenderer created = new TaaRenderer(
			renderer,
			"TAA Viewport " + shaderSlot,
			renderer.MaxFramesInFlight,
			viewport.OffscreenTarget,
			viewport.CompositeTarget,
			_compositeShaders!,
			_compositeRange,
			Program.PointClampedSampler,
			_linearSampler,
			ShadersFor("TaaVert", "TaaResolveFrag"),
			ShadersFor("TaaVert", "TaaCopyFrag"),
			viewport.OffscreenTarget.DepthImage!.ImageView);
		Renderers[shaderSlot] = created;
		return created;
	}

	private static TaaRenderer? GetExisting(int shaderSlot)
	{
		if (!IsActive)
		{
			return null;
		}
		return Renderers.TryGetValue(shaderSlot, out TaaRenderer? renderer) ? renderer : null;
	}

	private static ShaderReference[] ShadersFor(string vertexId, string fragmentId)
	{
		return new ShaderReference[2]
		{
			ModLibrary.Get<ShaderReference>(vertexId),
			ModLibrary.Get<ShaderReference>(fragmentId)
		};
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
