// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Logging;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.Pointers.Extensions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA.Rendering;
using KSA.Rendering.PostProcessing;
using RenderCore;
using RenderCore.Pipelines;

namespace KSA.Mods.NewAA;

// Push constant for DlaaLineVert.vert and DlaaLineFrag.frag. Layout matches their push_constant block.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DlaaLinePush
{
	public float4x4 ViewProjection;

	// x = quad half width in pixels, y = render width, z = render height, w = near clip w
	public float4 Params;
}

// The pipeline that draws orbit lines as antialiased quads. One per game, built when the first DLAA renderer needs it.
internal static class DlaaLinePipeline
{
	// Half width of each quad in pixels. The line itself is about 1.2 pixels wide, the rest is the soft edge.
	public const float HalfWidth = 1.1f;

	private static VkPipelineLayout _layout;

	private static VkPipeline _pipeline;

	private static bool _built;

	private static bool _failed;

	// Colour replaces what is there, alpha keeps the larger value. Overlapping quads at a joint then do not double up.
	private static readonly PtrOwner<VkPipelineColorBlendAttachmentState> BlendAttachment = new PtrOwner<VkPipelineColorBlendAttachmentState>(new VkPipelineColorBlendAttachmentState
	{
		BlendEnable = true,
		SrcColorBlendFactor = VkBlendFactor.One,
		DstColorBlendFactor = VkBlendFactor.Zero,
		ColorBlendOp = VkBlendOp.Add,
		SrcAlphaBlendFactor = VkBlendFactor.One,
		DstAlphaBlendFactor = VkBlendFactor.One,
		AlphaBlendOp = VkBlendOp.Max,
		ColorWriteMask = (VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit)
	});

	private static readonly PtrOwner<VkPipelineColorBlendStateCreateInfo> BlendState = new PtrOwner<VkPipelineColorBlendStateCreateInfo>(new VkPipelineColorBlendStateCreateInfo
	{
		AttachmentCount = 1,
		Attachments = BlendAttachment
	});

	public static VkPipelineLayout Layout => _layout;

	public static VkPipeline Pipeline => _pipeline;

	public static bool EnsureBuilt(Renderer renderer, VkFormat colorFormat, VkFormat depthFormat)
	{
		if (_built)
		{
			return true;
		}
		if (_failed)
		{
			return false;
		}
		try
		{
			Build(renderer, colorFormat, depthFormat);
			_built = true;
		}
		catch (Exception ex)
		{
			_failed = true;
			DefaultCategory.Log.Info("[DLAA] Orbit line overlay unavailable, orbit lines stay in the scene: " + ex.Message, "EnsureBuilt", "NewAA", 0);
		}
		return _built;
	}

	private unsafe static void Build(Renderer renderer, VkFormat colorFormat, VkFormat depthFormat)
	{
		ShaderReference vertex = ModLibrary.Get<ShaderReference>("DlaaLineVert");
		ShaderReference fragment = ModLibrary.Get<ShaderReference>("DlaaLineFrag");

		VkPushConstantRange range = new VkPushConstantRange
		{
			StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<DlaaLinePush>()
		};
		_layout = renderer.Device.CreatePipelineLayout(default, new ReadOnlySpan<VkPushConstantRange>(in range), null);

		ReadOnlySpan<byte> entry = "main"u8;
		Span<VkPipelineShaderStageCreateInfo> stages = stackalloc VkPipelineShaderStageCreateInfo[2]
		{
			new VkPipelineShaderStageCreateInfo
			{
				Name = entry.AsPointer(),
				Module = vertex.Shader.Value,
				Stage = vertex.Stage
			},
			new VkPipelineShaderStageCreateInfo
			{
				Name = entry.AsPointer(),
				Module = fragment.Shader.Value,
				Stage = fragment.Stage
			}
		};

		// Every segment is one instance. Points i and i+1 come from the same buffer bound twice, one element apart.
		using VertexInput vertexInput = new VertexInput(4, 4)
			.AddBinding(0, ByteSize.Of<float3>(), VkVertexInputRate.Instance)
			.AddBinding(1, ByteSize.Of<float3>(), VkVertexInputRate.Instance)
			.AddBinding(2, ByteSize.Of<byte4>(), VkVertexInputRate.Instance)
			.AddBinding(3, ByteSize.Of<byte4>(), VkVertexInputRate.Instance)
			.AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
			.AddAttribute(1, 1, VkFormat.R32G32B32SFloat, ByteSize.Zero)
			.AddAttribute(2, 2, VkFormat.R8G8B8A8UInt, ByteSize.Zero)
			.AddAttribute(3, 3, VkFormat.R8G8B8A8UInt, ByteSize.Zero);

		Span<VkPipelineInputAssemblyStateCreateInfo> assembly = stackalloc VkPipelineInputAssemblyStateCreateInfo[1]
		{
			new VkPipelineInputAssemblyStateCreateInfo
			{
				Topology = VkPrimitiveTopology.TriangleStrip
			}
		};
		Span<VkPipelineMultisampleStateCreateInfo> multisample = stackalloc VkPipelineMultisampleStateCreateInfo[1]
		{
			new VkPipelineMultisampleStateCreateInfo
			{
				RasterizationSamples = VkSampleCountFlags._1Bit
			}
		};
		Span<VkPipelineRasterizationStateCreateInfo> rasterization = stackalloc VkPipelineRasterizationStateCreateInfo[1]
		{
			new VkPipelineRasterizationStateCreateInfo
			{
				CullMode = VkCullModeFlags.None,
				FrontFace = VkFrontFace.CounterClockwise,
				PolygonMode = VkPolygonMode.Fill,
				LineWidth = 1f
			}
		};
		Span<VkFormat> colorFormats = stackalloc VkFormat[1] { colorFormat };
		VkPipelineRenderingCreateInfo rendering = new VkPipelineRenderingCreateInfo();
		rendering.ColorAttachmentCount = 1;
		rendering.ColorAttachmentFormats = colorFormats.AsPointer();
		rendering.DepthAttachmentFormat = depthFormat;
		rendering.StencilAttachmentFormat = VkFormat.Undefined;
		rendering.ViewMask = 0;
		Span<VkPipelineRenderingCreateInfo> renderingSpan = stackalloc VkPipelineRenderingCreateInfo[1] { rendering };

		VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo();
		info.Layout = _layout;
		info.StageCount = 2;
		info.Stages = stages.AsPointer();
		info.ColorBlendState = BlendState;
		info.DynamicState = renderer.DynamicStateInfo;
		info.DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite;
		info.InputAssemblyState = assembly.AsPointer();
		info.MultisampleState = multisample.AsPointer();
		info.RasterizationState = rasterization.AsPointer();
		info.VertexInputState = vertexInput;
		info.ViewportState = renderer.ViewportState;
		info.Next = renderingSpan.AsPointer();
		_pipeline = renderer.Device.CreateGraphicsPipeline(PipelineCaches.Shared, info, null);
	}
}

// Draws the game's orbit lines for one DLAA renderer, after DLSS. The game draws them into the scene, jittered with everything else.
// DLSS then smears thin one-pixel lines. Here the same line data is drawn again, unjittered and antialiased, over the DLSS output.
//
//   lines target   straight line colour in rgb, coverage in alpha, tested against the scene's depth
//   tonemap        the game's own composite pass over the colour, so the lines match the scene's look
//   combine        DlaaLineCombineFrag.frag mixes the tonemapped lines over the final image by coverage
public sealed class DlaaLineOverlay : IDisposable
{
	private readonly RenderTarget _lineTarget;

	private readonly RenderTarget _lineLdrTarget;

	private readonly ScreenspaceRenderer _tonemap;

	private readonly VkExtent2D _extent;

	// Tonemapped line colour. Sampled by the combine pass.
	public VkImageView LineColorView => _lineLdrTarget.ColorImage!.ImageView;

	// The alpha channel holds the coverage. Sampled by the combine pass.
	public VkImageView CoverageView => _lineTarget.ColorImage!.ImageView;

	internal DlaaLineOverlay(Renderer renderer, string name, int frameCount, VkExtent2D extent, ShaderReference[] compositeShaders, VkPushConstantRange compositeRange, VkSampler pointSampler)
	{
		_extent = extent;
		_lineTarget = AaPasses.CreateTarget(renderer, name + " Lines", extent, VkFormat.R8G8B8A8UNorm);
		_lineLdrTarget = AaPasses.CreateTarget(renderer, name + " Lines LDR", extent, VkFormat.R8G8B8A8UNorm);
		_tonemap = AaPasses.CreatePass(renderer, _lineLdrTarget, compositeShaders, 1, frameCount, pointSampler, compositeRange, false);
		_tonemap.AddImage(_lineTarget.ColorImage!.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
		_tonemap.Build();
	}

	// Draws the lines the game queued for this viewport and clears the queue. Returns false if there was nothing to draw.
	internal unsafe bool Draw(CommandBuffer commandBuffer, IViewport viewport, int frameIndex, in CompositeData compositeData)
	{
		DeviceVector positions = OrbitLinePass.PositionDataVector[viewport.ShaderSlot][frameIndex];
		DeviceVector colors = OrbitLinePass.ColorDataVector[viewport.ShaderSlot][frameIndex];
		int vertexCount = (int)colors.ElementCount;
		if (vertexCount < 2)
		{
			positions.Clear();
			colors.Clear();
			return false;
		}

		RenderTarget scene = viewport.OffscreenTarget;
		RenderImage depth = scene.DepthAttachment!;

		Span<VkBufferMemoryBarrier2> inBuffers = stackalloc VkBufferMemoryBarrier2[2];
		Span<VkImageMemoryBarrier2> inImages = stackalloc VkImageMemoryBarrier2[2];
		BarrierBatch barrierBatch = new BarrierBatch(inBuffers, inImages);
		VkBufferMemoryBarrier2 positionBarrier = new VkBufferMemoryBarrier2
		{
			Buffer = positions.VkBuffer,
			Offset = positions.Offset,
			Size = positions.Range,
			SrcStageMask = VkPipelineStageFlags2.HostBit,
			DstStageMask = VkPipelineStageFlags2.VertexInputBit,
			SrcAccessMask = VkAccessFlags2.HostWriteBit,
			DstAccessMask = VkAccessFlags2.VertexAttributeReadBit,
			SrcQueueFamilyIndex = -1,
			DstQueueFamilyIndex = -1
		};
		barrierBatch.Add(in positionBarrier);
		VkBufferMemoryBarrier2 colorBarrier = new VkBufferMemoryBarrier2
		{
			Buffer = colors.VkBuffer,
			Offset = colors.Offset,
			Size = colors.Range,
			SrcStageMask = VkPipelineStageFlags2.HostBit,
			DstStageMask = VkPipelineStageFlags2.VertexInputBit,
			SrcAccessMask = VkAccessFlags2.HostWriteBit,
			DstAccessMask = VkAccessFlags2.VertexAttributeReadBit,
			SrcQueueFamilyIndex = -1,
			DstQueueFamilyIndex = -1
		};
		barrierBatch.Add(in colorBarrier);
		barrierBatch.Add(_lineTarget.ColorAttachment!, ImageBarrierInfo.Presets.ColorAttachmentWrite, 0, inForceBarrier: true);
		barrierBatch.Add(depth, ImageBarrierInfo.Presets.DepthStencilAttachmentReadWrite);
		barrierBatch.SubmitAndFlush(commandBuffer);

		VkClearValue clear = new VkClearValue();
		clear.Color = new VkClearColorValue();
		VkRenderingAttachmentInfo colorAttachment = new VkRenderingAttachmentInfo();
		colorAttachment.ImageView = _lineTarget.ColorAttachment!.ImageView;
		colorAttachment.ImageLayout = VkImageLayout.ColorAttachmentOptimal;
		colorAttachment.ResolveMode = VkResolveModeFlags.None;
		colorAttachment.LoadOp = VkAttachmentLoadOp.Clear;
		colorAttachment.StoreOp = VkAttachmentStoreOp.Store;
		colorAttachment.ClearValue = clear;
		VkRenderingAttachmentInfo depthAttachment = new VkRenderingAttachmentInfo();
		depthAttachment.ImageView = depth.ImageView;
		depthAttachment.ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal;
		depthAttachment.ResolveMode = VkResolveModeFlags.None;
		depthAttachment.LoadOp = VkAttachmentLoadOp.Load;
		depthAttachment.StoreOp = VkAttachmentStoreOp.Store;
		VkRenderingInfo renderingInfo = new VkRenderingInfo();
		renderingInfo.RenderArea = new VkRect2D
		{
			Extent = _extent
		};
		renderingInfo.LayerCount = 1;
		renderingInfo.ViewMask = 0;
		renderingInfo.ColorAttachmentCount = 1;
		renderingInfo.ColorAttachments = &colorAttachment;
		renderingInfo.DepthAttachment = &depthAttachment;
		commandBuffer.BeginRendering(in renderingInfo);
		commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, DlaaLinePipeline.Pipeline);
		Program.SetViewport(commandBuffer);
		DlaaLinePush push = new DlaaLinePush
		{
			ViewProjection = TaaCamera.CurrentViewProjection,
			Params = new float4(DlaaLinePipeline.HalfWidth, _extent.Width, _extent.Height, Program.GetMainCamera().NearPlane)
		};
		commandBuffer.PushConstants(DlaaLinePipeline.Layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);

		// Bindings 1 and 3 read the next point of the same buffers: one element further on.
		Span<VkBuffer> buffers = stackalloc VkBuffer[4];
		buffers[0] = positions.VkBuffer;
		buffers[1] = positions.VkBuffer;
		buffers[2] = colors.VkBuffer;
		buffers[3] = colors.VkBuffer;
		Span<ByteSize64> offsets = stackalloc ByteSize64[4];
		offsets[0] = positions.Offset;
		offsets[1] = positions.Offset + (ulong)sizeof(float3);
		offsets[2] = colors.Offset;
		offsets[3] = colors.Offset + (ulong)sizeof(byte4);
		commandBuffer.BindVertexBuffers(0, buffers, offsets);
		commandBuffer.Draw(4, vertexCount - 1, 0, 0);
		commandBuffer.EndRendering();

		positions.Clear();
		colors.Clear();

		commandBuffer.PipelineBarrier2(_lineTarget.ColorImage!, ImageBarrierInfo.Presets.SampledReadC);
		AaPasses.BeginCleared(commandBuffer, _lineLdrTarget);
		_tonemap.Render(commandBuffer, frameIndex, compositeData);
		AaPasses.EndSampled(commandBuffer, _lineLdrTarget);
		return true;
	}

	public void Dispose()
	{
		_tonemap.Dispose();
		_lineLdrTarget.Dispose();
		_lineTarget.Dispose();
	}
}

// Decides, per viewport and frame, whether the game's orbit line pass draws into the scene or leaves its line data for the overlay.
internal static class DlaaLineDeferral
{
	private static readonly bool[] Deferred = new bool[8];

	private static bool _flushing;

	// Called from the orbit line pass. True means the pass should not draw: the overlay will draw this frame's lines later.
	public static bool TryDefer(IViewport viewport)
	{
		int slot = viewport.ShaderSlot;
		if (_flushing || (uint)slot >= (uint)Deferred.Length || !DlaaAntiAliasing.IsSelected || !viewport.IsMain() || !DlaaState.CanDrawLines(slot))
		{
			return false;
		}
		Deferred[slot] = true;
		return true;
	}

	public static bool IsDeferred(int slot)
	{
		return (uint)slot < (uint)Deferred.Length && Deferred[slot];
	}

	public static void MarkDrawn(int slot)
	{
		if ((uint)slot < (uint)Deferred.Length)
		{
			Deferred[slot] = false;
		}
	}

	// The overlay could not run this frame, so draw the lines the way the game would have: into the scene.
	public static void Flush(CommandBuffer commandBuffer, int slot, int frameIndex)
	{
		if (!IsDeferred(slot))
		{
			return;
		}
		Deferred[slot] = false;
		IViewport? viewport = FxaaState.FindViewport(slot);
		if (viewport == null)
		{
			return;
		}
		_flushing = true;
		try
		{
			OrbitLinePass.Run(commandBuffer, viewport, frameIndex);
		}
		finally
		{
			_flushing = false;
		}
	}
}

// The game's orbit line pass. Skipped while the overlay is going to draw the lines after DLSS.
[HarmonyPatch(typeof(OrbitLinePass), nameof(OrbitLinePass.Run))]
internal static class OrbitLinePassRunPatch
{
	private static bool Prefix(IViewport viewport)
	{
		return !DlaaLineDeferral.TryDefer(viewport);
	}
}
