// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Logging;
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

// The TAA menu entry. Uses enum value 7, which the game's enum does not define.
public static class TaaAntiAliasing
{
	public const int EnumValue = 7;

	public const string Label = "TAA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;

	public static bool IsSelected => GameSettings.Current.AntiAliasingMethod == Method;
}

// TAA needs no runtime, only its shaders. It is ready once they are registered, unless setting it up failed this session.
internal static class TaaRuntime
{
	private static readonly string[] ShaderIds = { "TaaResolveFrag", "TaaSharpenFrag", "TaaDepthFrag" };

	private static bool _ready;

	private static bool _disabled;

	private static bool _warnedMissing;

	public static bool IsReady
	{
		get
		{
			if (!_ready && !_disabled)
			{
				_ready = ShadersRegistered();
			}
			return _ready && !_disabled;
		}
	}

	// Turns TAA off for this session. The game then uses no anti-aliasing while TAA is selected.
	public static void Disable(string reason)
	{
		if (_disabled)
		{
			return;
		}
		_disabled = true;
		Log(reason + " TAA is off for this session, using no anti aliasing.");
	}

	public static void Log(string message)
	{
		DefaultCategory.Log.Info("[TAA] " + message, "TaaRuntime", "NewAA", 0);
	}

	private static bool ShadersRegistered()
	{
		foreach (string id in ShaderIds)
		{
			if (!ModLibrary.TryGet<ShaderReference>(id, out _))
			{
				if (!_warnedMissing)
				{
					_warnedMissing = true;
					Log("Missing shader '" + id + "'. Using no anti aliasing.");
				}
				return false;
			}
		}
		return true;
	}
}

// Push constant for TaaResolveFrag.frag. Layout matches its push_constant block.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TaaData
{
	// x = 1/width, y = 1/height, z = width, w = height
	public float4 RtMetrics;

	// xy = this frame's jitter in render pixels, z = 1 to use the history or 0 to start over, w = unused
	public float4 Params;
}

// The TAA resolve, used by DlaaRenderer in place of DLSS or FSR. Inputs are the ones those get: the jittered LDR scene, the motion
// vectors and the depth. Passes: resolve (reads one history buffer, writes the other) -> sharpen into the output the final pass shows
// -> keep the depth for the next frame's resolve (TaaDepthFrag, which stores the nearest depth around each pixel).
// The history buffers are 16-bit float, because an 8-bit history that keeps 90% each frame stops a few steps short of the true colour.
internal sealed class TaaResolver : IDisposable
{
	private readonly RenderTarget[] _history;

	private readonly RenderTarget _output;

	private readonly RenderTarget _previousDepth;

	private readonly ScreenspaceRenderer[] _resolvePasses;

	private readonly ScreenspaceRenderer[] _sharpenPasses;

	private readonly ScreenspaceRenderer _depthPass;

	private int _writeIndex;

	private bool _historyInitialized;

	public VkExtent2D Extent { get; }

	// The sharpened result. Readable by the final pass after Resolve.
	public VkImageView OutputView => _output.ColorImage!.ImageView;

	public TaaResolver(Renderer renderer, string name, int frameCount, VkExtent2D extent, VkSampler linearSampler, VkImageView sceneView, VkImageView motionView, VkImageView depthView)
	{
		Extent = extent;
		ShaderReference vertex = ModLibrary.Get<ShaderReference>("TaaVert");
		ShaderReference[] resolveShaders = { vertex, ModLibrary.Get<ShaderReference>("TaaResolveFrag") };
		ShaderReference[] sharpenShaders = { vertex, ModLibrary.Get<ShaderReference>("TaaSharpenFrag") };
		ShaderReference[] depthShaders = { vertex, ModLibrary.Get<ShaderReference>("TaaDepthFrag") };

		_history = new RenderTarget[2]
		{
			AaPasses.CreateTarget(renderer, name + " TAA History 0", extent, VkFormat.R16G16B16A16SFloat),
			AaPasses.CreateTarget(renderer, name + " TAA History 1", extent, VkFormat.R16G16B16A16SFloat)
		};
		_output = AaPasses.CreateTarget(renderer, name + " TAA Output", extent, VkFormat.R8G8B8A8UNorm);
		// 32-bit: the depth of anything far away is tiny (reverse-Z), and the resolve only needs to know whether it is exactly 0.
		_previousDepth = AaPasses.CreateTarget(renderer, name + " TAA Previous Depth", extent, VkFormat.R32SFloat);

		_depthPass = AaPasses.CreatePass(renderer, _previousDepth, depthShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
		_depthPass.AddImage(depthView, VkImageLayout.DepthStencilReadOnlyOptimal);
		_depthPass.Build();

		VkPushConstantRange resolveRange = new VkPushConstantRange
		{
			StageFlags = VkShaderStageFlags.FragmentBit,
			Offset = ByteSize.Zero,
			Size = ByteSize.Of<TaaData>()
		};
		_resolvePasses = new ScreenspaceRenderer[2];
		_sharpenPasses = new ScreenspaceRenderer[2];
		for (int i = 0; i < 2; i++)
		{
			// Pass i writes history i and reads history 1 - i.
			ScreenspaceRenderer resolve = AaPasses.CreatePass(renderer, _history[i], resolveShaders, 5, frameCount, linearSampler, resolveRange, true);
			resolve.AddImage(sceneView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.AddImage(_history[1 - i].ColorImage!.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.AddImage(motionView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.AddImage(depthView, VkImageLayout.DepthStencilReadOnlyOptimal);
			resolve.AddImage(_previousDepth.ColorImage!.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			resolve.Build();
			_resolvePasses[i] = resolve;

			ScreenspaceRenderer sharpen = AaPasses.CreatePass(renderer, _output, sharpenShaders, 1, frameCount, linearSampler, AaPasses.PushRange(VkShaderStageFlags.FragmentBit), true);
			sharpen.AddImage(_history[i].ColorImage!.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
			sharpen.Build();
			_sharpenPasses[i] = sharpen;
		}
	}

	// Records the resolve and the sharpen pass. The scene and motion targets must be readable and the depth in its sampled layout.
	public void Resolve(CommandBuffer commandBuffer, int frameIndex, float jitterX, float jitterY, bool reset)
	{
		int write = _writeIndex;
		int read = 1 - write;
		if (!_historyInitialized)
		{
			// The resolve always binds the other history buffer and the previous depth, so they have to be in a readable layout from the
			// first frame on.
			AaPasses.BeginCleared(commandBuffer, _history[read]);
			AaPasses.EndSampled(commandBuffer, _history[read]);
			AaPasses.BeginCleared(commandBuffer, _previousDepth);
			AaPasses.EndSampled(commandBuffer, _previousDepth);
			_historyInitialized = true;
			reset = true;
		}

		TaaData push = new TaaData
		{
			RtMetrics = new float4(1f / (float)Extent.Width, 1f / (float)Extent.Height, Extent.Width, Extent.Height),
			Params = new float4(jitterX, jitterY, reset ? 0f : 1f, 0f)
		};
		AaPasses.BeginCleared(commandBuffer, _history[write]);
		_resolvePasses[write].Render(commandBuffer, frameIndex, push);
		AaPasses.EndSampled(commandBuffer, _history[write]);

		AaPasses.BeginCleared(commandBuffer, _output);
		_sharpenPasses[write].Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		AaPasses.EndSampled(commandBuffer, _output);

		// After the resolve has read last frame's depth.
		AaPasses.BeginCleared(commandBuffer, _previousDepth);
		_depthPass.Render(commandBuffer, frameIndex, AaPasses.PushFor(Extent));
		AaPasses.EndSampled(commandBuffer, _previousDepth);

		_writeIndex = read;
	}

	public void Dispose()
	{
		_depthPass.Dispose();
		for (int i = 0; i < 2; i++)
		{
			_sharpenPasses[i].Dispose();
			_resolvePasses[i].Dispose();
		}
		_previousDepth.Dispose();
		_output.Dispose();
		for (int i = 0; i < 2; i++)
		{
			_history[i].Dispose();
		}
	}
}
