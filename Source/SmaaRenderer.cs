// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
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

// The new Anti-Aliasing menu entry. Uses enum value 6, which the game's enum does not define.
public static class SmaaAntiAliasing
{
	public const int EnumValue = 6;

	public const string Label = "SMAA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;
}

// Per-viewport SMAA 1x (preset HIGH). Three passes after the tonemap composite:
//   LDR scene -> edge detection (RG) -> blending weights (RGBA, uses area/search textures) -> neighborhood blending -> final
public sealed class SmaaRenderer : IDisposable
{
	private readonly RenderTarget _sceneLdrTarget;

	private readonly RenderTarget _edgesTarget;

	private readonly RenderTarget _blendTarget;

	private readonly ScreenspaceRenderer _sceneLdrComposite;

	private readonly ScreenspaceRenderer _edgePass;

	private readonly ScreenspaceRenderer _weightsPass;

	private readonly ScreenspaceRenderer _blendPass;

	public VkExtent2D Extent { get; }

	public SmaaRenderer(Renderer renderer, string name, int frameCount, RenderTarget offscreenTarget, IRenderPassInfo finalPassInfo, ShaderReference[] compositeShaders, VkPushConstantRange compositeRange, VkSampler pointSampler, VkSampler linearSampler, ShaderReference[] edgeShaders, ShaderReference[] weightsShaders, ShaderReference[] blendShaders, VkPushConstantRange passRange, VkImageView areaView, VkImageView searchView)
	{
		Extent = offscreenTarget.Extent;
		_sceneLdrTarget = AaPasses.CreateTarget(renderer, name + " Scene LDR", Extent, VkFormat.R8G8B8A8UNorm);
		_edgesTarget = AaPasses.CreateTarget(renderer, name + " Edges", Extent, VkFormat.R8G8UNorm);
		_blendTarget = AaPasses.CreateTarget(renderer, name + " Blend", Extent, VkFormat.R8G8B8A8UNorm);

		// Tonemap composite (same as FXAA and CMAA2).
		_sceneLdrComposite = AaPasses.CreatePass(renderer, _sceneLdrTarget, compositeShaders, 1, frameCount, pointSampler, compositeRange, false);
		_sceneLdrComposite.AddImage(offscreenTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_sceneLdrComposite.Build();

		// Pass 1: edges. Reads the LDR scene.
		_edgePass = AaPasses.CreatePass(renderer, _edgesTarget, edgeShaders, 1, frameCount, linearSampler, passRange, true);
		_edgePass.AddImage(_sceneLdrTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_edgePass.Build();

		// Pass 2: blending weights. Reads edges, area and search textures (bindings 0, 1, 2).
		_weightsPass = AaPasses.CreatePass(renderer, _blendTarget, weightsShaders, 3, frameCount, linearSampler, passRange, true);
		_weightsPass.AddImage(_edgesTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_weightsPass.AddImage(areaView, VkImageLayout.ShaderReadOnlyOptimal);
		_weightsPass.AddImage(searchView, VkImageLayout.ShaderReadOnlyOptimal);
		_weightsPass.Build();

		// Pass 3: neighborhood blending into the final target. Reads the LDR scene and the blend weights.
		_blendPass = AaPasses.CreatePass(renderer, finalPassInfo, blendShaders, 2, frameCount, linearSampler, passRange, true);
		_blendPass.AddImage(_sceneLdrTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_blendPass.AddImage(_blendTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_blendPass.Build();
	}

	// Mirrors the CMAA2 prepare pass: composite into the LDR target, then run the edge and blending-weight passes.
	public void RenderPreparePasses(CommandBuffer commandBuffer, int frameIndex, in CompositeData compositeData)
	{
		FxaaData push = AaPasses.PushFor(Extent);
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2LdrComposite))
		{
			AaPasses.BeginCleared(commandBuffer, _sceneLdrTarget);
			_sceneLdrComposite.Render(commandBuffer, frameIndex, compositeData);
			AaPasses.EndSampled(commandBuffer, _sceneLdrTarget);

			AaPasses.BeginCleared(commandBuffer, _edgesTarget);
			_edgePass.Render(commandBuffer, frameIndex, push);
			AaPasses.EndSampled(commandBuffer, _edgesTarget);

			AaPasses.BeginCleared(commandBuffer, _blendTarget);
			_weightsPass.Render(commandBuffer, frameIndex, push);
			AaPasses.EndSampled(commandBuffer, _blendTarget);
		}
	}

	// Mirrors the CMAA2 final blit. Recorded inside the same pass as the original final composite.
	public void RenderFinalBlit(CommandBuffer commandBuffer, int frameIndex)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2Blit))
		{
			_blendPass.Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		}
	}

	public void Dispose()
	{
		_blendPass.Dispose();
		_weightsPass.Dispose();
		_edgePass.Dispose();
		_sceneLdrComposite.Dispose();
		_blendTarget.Dispose();
		_edgesTarget.Dispose();
		_sceneLdrTarget.Dispose();
	}
}

// Owns the SMAA renderers and their shared resources. Mirrors FxaaState.
internal static class SmaaState
{
	private static readonly Dictionary<int, SmaaRenderer> Renderers = new Dictionary<int, SmaaRenderer>();

	private static readonly string[] ShaderIds = { "SmaaEdgeVert", "SmaaEdgeFrag", "SmaaWeightsVert", "SmaaWeightsFrag", "SmaaBlendVert", "SmaaBlendFrag" };

	private static readonly string[] TextureIds = { "SmaaAreaTex", "SmaaSearchTex" };

	private static ShaderReference[]? _compositeShaders;

	private static VkPushConstantRange _compositeRange;

	private static VkPushConstantRange _passRange;

	private static VkSampler _linearSampler;

	private static bool _sharedReady;

	private static bool _warnedMissing;

	public static bool IsActive => GameSettings.Current.AntiAliasingMethod == SmaaAntiAliasing.Method;

	// True when all six shaders and both lookup textures are registered and loaded.
	private static bool ResourcesAvailable()
	{
		foreach (string id in ShaderIds)
		{
			if (!ModLibrary.TryGet<ShaderReference>(id, out _))
			{
				return Warn("shader '" + id + "'");
			}
		}
		foreach (string id in TextureIds)
		{
			if (!ModLibrary.TryGet<TextureReference>(id, out TextureReference? texture) || texture == null || !texture.IsLoaded())
			{
				return Warn("texture '" + id + "'");
			}
		}
		return true;
	}

	private static bool Warn(string what)
	{
		if (!_warnedMissing)
		{
			_warnedMissing = true;
			Console.Error.WriteLine("[SMAA] Missing or unloaded " + what + ". Falling back to no anti-aliasing.");
		}
		return false;
	}

	public static SmaaRenderer? GetOrCreate(int shaderSlot)
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
		if (Renderers.TryGetValue(shaderSlot, out SmaaRenderer? existing))
		{
			if (existing.Extent.Width == extent.Width && existing.Extent.Height == extent.Height)
			{
				return existing;
			}
			existing.Dispose();
			Renderers.Remove(shaderSlot);
		}
		EnsureShared();
		Renderer renderer = Program.GetRenderer();
		SmaaRenderer created = new SmaaRenderer(
			renderer,
			"SMAA Viewport " + shaderSlot,
			renderer.MaxFramesInFlight,
			viewport.OffscreenTarget,
			viewport.CompositeTarget,
			_compositeShaders!,
			_compositeRange,
			Program.PointClampedSampler,
			_linearSampler,
			ShadersFor("SmaaEdgeVert", "SmaaEdgeFrag"),
			ShadersFor("SmaaWeightsVert", "SmaaWeightsFrag"),
			ShadersFor("SmaaBlendVert", "SmaaBlendFrag"),
			_passRange,
			ModLibrary.Get<TextureReference>("SmaaAreaTex").ImageView,
			ModLibrary.Get<TextureReference>("SmaaSearchTex").ImageView);
		Renderers[shaderSlot] = created;
		return created;
	}

	// Used by the final pass. Never creates anything, so it always matches the prepare pass decision.
	public static SmaaRenderer? GetExisting(int shaderSlot)
	{
		if (!IsActive)
		{
			return null;
		}
		return Renderers.TryGetValue(shaderSlot, out SmaaRenderer? renderer) ? renderer : null;
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
		// SMAA's vertex shaders read the metrics too, so the push constant needs both stages.
		_passRange = AaPasses.PushRange(VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit);
		_linearSampler = Program.GetRenderer().Device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);
		_sharedReady = true;
	}
}
