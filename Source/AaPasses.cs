// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using Brutal;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA.Rendering;
using KSA.Rendering.PostProcessing;
using RenderCore;
using RenderCore.Pipelines;

namespace KSA.Mods.NewAA;

// Helpers shared by the FXAA and SMAA renderers.
internal static class AaPasses
{
	// Push constant with the render-target size. Every anti-aliasing pass reads it.
	public static FxaaData PushFor(VkExtent2D extent)
	{
		return new FxaaData
		{
			RtMetrics = new float4(1f / (float)extent.Width, 1f / (float)extent.Height, extent.Width, extent.Height)
		};
	}

	public static VkPushConstantRange PushRange(VkShaderStageFlags stages)
	{
		return new VkPushConstantRange
		{
			StageFlags = stages,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<FxaaData>()
		};
	}

	public static RenderTarget CreateTarget(Renderer renderer, string name, VkExtent2D extent, VkFormat format)
	{
		return new RenderTarget(renderer, name, extent, format, VkFormat.Undefined);
	}

	// opaque = true writes the output directly (BlendNone). opaque = false alpha-blends, as the game's composite does.
	public static ScreenspaceRenderer CreatePass(Renderer renderer, IRenderPassInfo target, ShaderReference[] shaders, int imageCount, int frameCount, VkSampler sampler, VkPushConstantRange range, bool opaque)
	{
		Span<ShaderReference> shaderRefs = shaders;
		VkPushConstantRange rangeCopy = range;
		if (opaque)
		{
			return new ScreenspaceRenderer(renderer, target, shaderRefs, imageCount, 0, frameCount, sampler, Presets.BlendState.BlendNone, new Span<VkPushConstantRange>(ref rangeCopy));
		}
		return new ScreenspaceRenderer(renderer, target, shaderRefs, imageCount, 0, frameCount, sampler, Presets.BlendState.BlendColorAlpha, new Span<VkPushConstantRange>(ref rangeCopy));
	}

	public static void BeginCleared(CommandBuffer commandBuffer, RenderTarget target)
	{
		target.BeginRendering(commandBuffer, new VkClearValue
		{
			Color = new VkClearColorValue
			{
				Float32 = Color.Black.AsFloat4
			}
		}, new VkClearValue());
	}

	public static void EndSampled(CommandBuffer commandBuffer, RenderTarget target)
	{
		target.EndRendering(commandBuffer, ImageBarrierInfo.Presets.SampledReadC, ImageBarrierInfo.Presets.DepthSampledReadVfc);
	}
}
