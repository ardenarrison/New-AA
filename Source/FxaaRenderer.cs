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

// Per-viewport FXAA pass: tonemap composite into an LDR target, then one fullscreen FXAA pass into the final target.
public sealed class FxaaRenderer : IDisposable
{
	private readonly RenderTarget _sceneLdrTarget;

	private readonly ScreenspaceRenderer _sceneLdrComposite;

	private readonly ScreenspaceRenderer _fxaaPass;

	public VkExtent2D Extent { get; }

	public FxaaRenderer(Renderer renderer, string name, int frameCount, RenderTarget offscreenTarget, IRenderPassInfo finalPassInfo, ShaderReference[] compositeShaders, VkPushConstantRange compositePushConstantRange, VkSampler pointSampler, VkSampler linearSampler)
	{
		Extent = offscreenTarget.Extent;
		_sceneLdrTarget = AaPasses.CreateTarget(renderer, name + " Scene LDR", Extent, VkFormat.R8G8B8A8UNorm);

		_sceneLdrComposite = AaPasses.CreatePass(renderer, _sceneLdrTarget, compositeShaders, 1, frameCount, pointSampler, compositePushConstantRange, false);
		_sceneLdrComposite.AddImage(offscreenTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_sceneLdrComposite.Build();

		// FXAA reads the LDR target with bilinear filtering (point sampling would break its sub-pixel search).
		ShaderReference[] fxaaShaders = new ShaderReference[2]
		{
			ModLibrary.Get<ShaderReference>("ScreenspaceVert"),
			ModLibrary.Get<ShaderReference>("FxaaFrag")
		};
		_fxaaPass = AaPasses.CreatePass(renderer, finalPassInfo, fxaaShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), false);
		_fxaaPass.AddImage(_sceneLdrTarget.ColorImage.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_fxaaPass.Build();
	}

	// Mirrors the CMAA2 prepare pass: tonemap composite into the LDR target.
	public void RenderPreparePasses(CommandBuffer commandBuffer, int frameIndex, in CompositeData compositeData)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2LdrComposite))
		{
			AaPasses.BeginCleared(commandBuffer, _sceneLdrTarget);
			_sceneLdrComposite.Render(commandBuffer, frameIndex, compositeData);
			AaPasses.EndSampled(commandBuffer, _sceneLdrTarget);
		}
	}

	// Mirrors the CMAA2 final blit. Recorded inside the same pass as the original final composite.
	public void RenderFinalBlit(CommandBuffer commandBuffer, int frameIndex)
	{
		using (commandBuffer.TagRegion(Profiler.GpuTag.Cmaa2Blit))
		{
			_fxaaPass.Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		}
	}

	public void Dispose()
	{
		_fxaaPass.Dispose();
		_sceneLdrComposite.Dispose();
		_sceneLdrTarget.Dispose();
	}
}
