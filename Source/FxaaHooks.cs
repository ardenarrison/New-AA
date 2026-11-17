// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
// StarMap injection layer for the New AA mod (FXAA, SMAA, TAA, DLAA, FSR Native AA).
// Patches are applied by FxaaMod.Initialize(), called from FxaaStarMapMod.OnImmediateLoad.
using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA.Rendering.PostProcessing;
using RenderCore;
using RenderCore.Pipelines;

namespace KSA.Mods.NewAA;

// The new FXAA menu entry. Uses enum value 5, which the game's enum does not define.
public static class FxaaAntiAliasing
{
	public const int EnumValue = 5;

	public const string Label = "FXAA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;
}

internal static class FxaaState
{
	private static readonly Dictionary<int, FxaaRenderer> Renderers = new Dictionary<int, FxaaRenderer>();

	private static ShaderReference[]? _compositeShaders;

	private static VkPushConstantRange _compositeRange;

	private static VkSampler _linearSampler;

	private static bool _sharedReady;

	private static bool _warnedMissingShader;

	// True only when the user has selected FXAA in the Anti-Aliasing dropdown.
	public static bool IsActive => GameSettings.Current.AntiAliasingMethod == FxaaAntiAliasing.Method;

	private static bool ShaderAvailable()
	{
		if (ModLibrary.TryGet<ShaderReference>("FxaaFrag", out _))
		{
			return true;
		}
		if (!_warnedMissingShader)
		{
			_warnedMissingShader = true;
			Console.Error.WriteLine("[FXAA] Shader 'FxaaFrag' is not registered. Falling back to no anti-aliasing.");
		}
		return false;
	}

	// Used by the prepare pass. Creates the renderer for this viewport when needed.
	public static FxaaRenderer? GetOrCreate(int shaderSlot)
	{
		if (!IsActive || !ShaderAvailable())
		{
			return null;
		}
		IViewport? viewport = FindViewport(shaderSlot);
		if (viewport == null)
		{
			return null;
		}
		VkExtent2D extent = viewport.OffscreenTarget.Extent;
		if (Renderers.TryGetValue(shaderSlot, out FxaaRenderer? existing))
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
		FxaaRenderer created = new FxaaRenderer(renderer, $"FXAA Viewport {shaderSlot}", renderer.MaxFramesInFlight, viewport.OffscreenTarget, viewport.CompositeTarget, _compositeShaders!, _compositeRange, Program.PointClampedSampler, _linearSampler);
		Renderers[shaderSlot] = created;
		return created;
	}

	// Used by the final pass. Never creates anything, so it always matches the prepare pass decision.
	public static FxaaRenderer? GetExisting(int shaderSlot)
	{
		if (!IsActive)
		{
			return null;
		}
		return Renderers.TryGetValue(shaderSlot, out FxaaRenderer? renderer) ? renderer : null;
	}

	// Shared with SmaaState.
	internal static IViewport? FindViewport(int shaderSlot)
	{
		foreach (IViewport viewport in ViewportRegistry.Views)
		{
			if (viewport.ShaderSlot == shaderSlot)
			{
				return viewport;
			}
		}
		return null;
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

// Adds FXAA, SMAA, TAA, DLAA and FSR Native AA to the list of options the Anti-Aliasing dropdown shows. FSR Native AA only where
// AMD's FSR runtime exists (Windows).
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.GetAvailableAntiAliasingMethods))]
internal static class AvailableAntiAliasingMethodsPatch
{
	private static void Postfix(ref ReadOnlySpan<GameSettings.AntiAliasing> __result)
	{
		List<GameSettings.AntiAliasing> methods = new List<GameSettings.AntiAliasing>(__result.ToArray());
		foreach (GameSettings.AntiAliasing extra in new[] { FxaaAntiAliasing.Method, SmaaAntiAliasing.Method, TaaAntiAliasing.Method, DlaaAntiAliasing.Method, FsrAntiAliasing.Method })
		{
			if (extra == FsrAntiAliasing.Method && !FsrAntiAliasing.RuntimeExistsForThisSystem)
			{
				continue;
			}
			if (!methods.Contains(extra))
			{
				methods.Add(extra);
			}
		}
		__result = methods.ToArray();
	}
}

// Gives the new options their dropdown labels. The game's EnumCollection only knows the enum's own values, so GetName returns null
// for the extra ones and the dropdown would draw empty, duplicate ImGui labels. Instead of patching the generic GetName method,
// the extra values and labels are added to the collection's own tables.
internal static class AntiAliasingNames
{
	public static void Register()
	{
		StringEnumCollection<GameSettings.AntiAliasing> collection = EnumCollections.AntiAliasingMethods;
		List<GameSettings.AntiAliasing> values = new List<GameSettings.AntiAliasing>(collection.Values);
		List<string> names = new List<string>(collection.Names);
		foreach ((GameSettings.AntiAliasing method, string label) in new[]
		{
			(FxaaAntiAliasing.Method, FxaaAntiAliasing.Label),
			(SmaaAntiAliasing.Method, SmaaAntiAliasing.Label),
			(TaaAntiAliasing.Method, TaaAntiAliasing.Label),
			(DlaaAntiAliasing.Method, DlaaAntiAliasing.Label),
			(FsrAntiAliasing.Method, FsrAntiAliasing.Label)
		})
		{
			int index = values.IndexOf(method);
			if (index >= 0)
			{
				names[index] = label;
				continue;
			}
			values.Add(method);
			names.Add(label);
		}
		Type type = collection.GetType().BaseType!;
		collection.Values = values.ToArray();
		AccessTools.Field(type, nameof(EnumCollection<GameSettings.AntiAliasing, string>.Names))!.SetValue(collection, names.ToArray());
		AccessTools.Field(type, "_valuesAsInts")!.SetValue(collection, values.ConvertAll(value => (int)value).ToArray());
		AccessTools.Field(type, "<Length>k__BackingField")!.SetValue(collection, values.Count);
	}
}

// Replaces the CMAA2 prepare pass when FXAA, SMAA, TAA, DLAA or FSR is selected.
[HarmonyPatch(typeof(Program), "RenderAaPreparePasses")]
internal static class RenderAaPreparePassesPatch
{
	private static bool Prefix(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		if (TemporalAa.IsSelected)
		{
			if (DlaaState.TryPrepare(commandBuffer, shaderSlot, frameIndex))
			{
				return false;
			}
			// DLAA, FSR or TAA could not run this frame. If the orbit lines were held back for it, draw them into the scene now.
			DlaaLineDeferral.Flush(commandBuffer, shaderSlot, frameIndex);
			if (DlaaAntiAliasing.IsSelected)
			{
				DlaaSupport.ReportOnce();
			}
			return true;
		}
		DlaaLineDeferral.Flush(commandBuffer, shaderSlot, frameIndex);
		if (SmaaState.IsActive)
		{
			SmaaRenderer? smaa = SmaaState.GetOrCreate(shaderSlot);
			if (smaa == null)
			{
				return true; // SMAA unavailable: run the original path.
			}
			smaa.RenderPreparePasses(commandBuffer, frameIndex, Program.TonemapData);
			return false;
		}
		FxaaRenderer? fxaa = FxaaState.GetOrCreate(shaderSlot);
		if (fxaa == null)
		{
			return true;
		}
		fxaa.RenderPreparePasses(commandBuffer, frameIndex, Program.TonemapData);
		return false;
	}
}

// Replaces the final composite when FXAA, SMAA, TAA, DLAA or FSR is selected. Makes the same decision as the prepare pass.
[HarmonyPatch(typeof(Program), "RenderFinalComposite")]
internal static class RenderFinalCompositePatch
{
	private static bool Prefix(CommandBuffer commandBuffer, int shaderSlot, int frameIndex)
	{
		if (TemporalAa.IsSelected)
		{
			return !DlaaState.TryFinal(commandBuffer, shaderSlot, frameIndex);
		}
		if (SmaaState.IsActive)
		{
			SmaaRenderer? smaa = SmaaState.GetExisting(shaderSlot);
			if (smaa == null)
			{
				return true;
			}
			smaa.RenderFinalBlit(commandBuffer, frameIndex);
			return false;
		}
		FxaaRenderer? fxaa = FxaaState.GetExisting(shaderSlot);
		if (fxaa == null)
		{
			return true;
		}
		fxaa.RenderFinalBlit(commandBuffer, frameIndex);
		return false;
	}
}

public static class FxaaMod
{
	private const string HarmonyId = "ksa.mod.newaa";

	// StarMap entry point. Call once at load time.
	public static void Initialize()
	{
		AntiAliasingNames.Register();
		new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
	}
}
