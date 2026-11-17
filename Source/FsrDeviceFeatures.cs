// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.Logging;
using Brutal.VulkanApi;
using HarmonyLib;

namespace KSA.Mods.NewAA;

// AMD's FSR runtime chooses its shader variants from what the GPU supports (fp16, 8/16-bit storage, cooperative matrices, FP8), not
// from what the game enabled on its device. The game creates its device with a feature chain that covers Vulkan 1.3, descriptor
// indexing and a few others, but not these. Using a feature the device was created without is invalid, so this adds them, each one
// only if the GPU supports it, to the chain the game passes to vkCreateDevice. The extensions they need are added the same way.
// Applied from StarMapBeforeMain, like DlaaDeviceExtensions, because the game creates its device before mods load.
internal static unsafe class FsrDeviceFeatures
{
	private const string HarmonyId = "ksa.mod.newaa.fsrdevice";

	// VkStructureType values (vulkan_core.h).
	private const int Vulkan11Features = 49;

	private const int Vulkan12Features = 51;

	// One feature struct this patch may add: its sType, the extensions that provide it (empty for core ones; the first one the GPU
	// supports is used), the core struct that already contains the same members (adding both is invalid), and its VkBool32 members.
	private readonly struct FeatureStruct
	{
		public readonly int SType;

		public readonly string[] Extensions;

		public readonly int CoveredBy;

		public readonly string[] Members;

		public FeatureStruct(int sType, string[] extensions, int coveredBy, params string[] members)
		{
			SType = sType;
			Extensions = extensions;
			CoveredBy = coveredBy;
			Members = members;
		}
	}

	private static readonly string[] CoreVulkan = Array.Empty<string>();

	// FSR 3's shaders use compute derivatives (SPV_KHR_compute_shader_derivatives) on every GPU, which Vulkan validation reports as
	// an error on the game's device. fp16, 8/16-bit storage, the memory model, cooperative matrices and FP8 are what the FSR 4 path
	// on AMD GPUs can use.
	private static readonly FeatureStruct[] Wanted =
	{
		new FeatureStruct(1000201000, new[] { "VK_KHR_compute_shader_derivatives", "VK_NV_compute_shader_derivatives" }, 0, "computeDerivativeGroupQuads", "computeDerivativeGroupLinear"),
		new FeatureStruct(1000082000, CoreVulkan, Vulkan12Features, "shaderFloat16", "shaderInt8"),
		new FeatureStruct(1000083000, CoreVulkan, Vulkan11Features, "storageBuffer16BitAccess", "uniformAndStorageBuffer16BitAccess", "storagePushConstant16", "storageInputOutput16"),
		new FeatureStruct(1000177000, CoreVulkan, Vulkan12Features, "storageBuffer8BitAccess", "uniformAndStorageBuffer8BitAccess", "storagePushConstant8"),
		new FeatureStruct(1000211000, CoreVulkan, Vulkan12Features, "vulkanMemoryModel", "vulkanMemoryModelDeviceScope", "vulkanMemoryModelAvailabilityVisibilityChains"),
		new FeatureStruct(1000506000, new[] { "VK_KHR_cooperative_matrix" }, 0, "cooperativeMatrix", "cooperativeMatrixRobustBufferAccess"),
		new FeatureStruct(1000567000, new[] { "VK_EXT_shader_float8" }, 0, "shaderFloat8", "shaderFloat8CooperativeMatrix")
	};

	// Every struct above: sType, pNext, then up to four VkBool32 members. Unused trailing members are ignored by the driver.
	[StructLayout(LayoutKind.Sequential)]
	private struct GenericFeatures
	{
		public int SType;

		public void* Next;

		public uint Member0;

		public uint Member1;

		public uint Member2;

		public uint Member3;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BaseStructure
	{
		public int SType;

		public BaseStructure* Next;
	}

	public static void Apply()
	{
		if (!FsrAntiAliasing.RuntimeExistsForThisSystem)
		{
			// No FSR on this system, so the game's device stays exactly as the game creates it.
			return;
		}
		try
		{
			MethodBase? target = AccessTools.Method(typeof(Core.PhysicalDeviceExtensions), nameof(Core.PhysicalDeviceExtensions.CreateDevice),
				new[] { typeof(PhysicalDevice), typeof(VkPhysicalDeviceFeatures2).MakeByRefType(), typeof(ReadOnlySpan<VkDeviceQueueCreateInfo>), typeof(HashSet<string>) });
			if (target == null)
			{
				Console.Error.WriteLine("[FSR] Core.PhysicalDeviceExtensions.CreateDevice not found. FSR may use features the device lacks.");
				return;
			}
			new Harmony(HarmonyId).Patch(target, prefix: new HarmonyMethod(typeof(FsrDeviceFeatures), nameof(AddFeatures)));
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[FSR] Could not patch device creation: " + ex.Message);
		}
	}

	// Runs just before the game calls vkCreateDevice. The added structs must stay alive until the call returns, so they are allocated
	// in native memory and never freed (a few dozen bytes, once).
	private static void AddFeatures(PhysicalDevice physicalDevice, ref VkPhysicalDeviceFeatures2 features, HashSet<string> enabledExtensionNames)
	{
		try
		{
			HashSet<int> present = new HashSet<int>();
			for (BaseStructure* node = (BaseStructure*)features.Next; node != null; node = node->Next)
			{
				present.Add(node->SType);
			}

			List<FeatureStruct> candidates = new List<FeatureStruct>();
			List<string> extensionsAdded = new List<string>();
			foreach (FeatureStruct wanted in Wanted)
			{
				if (present.Contains(wanted.SType) || (wanted.CoveredBy != 0 && present.Contains(wanted.CoveredBy)))
				{
					continue;
				}
				if (wanted.Extensions.Length > 0)
				{
					string? extension = Array.Find(wanted.Extensions, enabledExtensionNames.Contains)
						?? Array.Find(wanted.Extensions, physicalDevice.SupportsExtension);
					if (extension == null)
					{
						continue;
					}
					if (enabledExtensionNames.Add(extension))
					{
						extensionsAdded.Add(extension);
					}
				}
				candidates.Add(wanted);
			}
			if (candidates.Count == 0)
			{
				return;
			}

			// Ask the driver which members it supports, in a chain of our own.
			GenericFeatures* structs = (GenericFeatures*)NativeMemory.AllocZeroed((nuint)(sizeof(GenericFeatures) * candidates.Count));
			for (int i = 0; i < candidates.Count; i++)
			{
				structs[i].SType = candidates[i].SType;
				structs[i].Next = i + 1 < candidates.Count ? &structs[i + 1] : null;
			}
			VkPhysicalDeviceFeatures2 query = new VkPhysicalDeviceFeatures2 { Next = structs };
			physicalDevice.GetFeatures2(ref query);

			// Keep only the structs with at least one supported member, and link them in front of the game's chain.
			List<string> enabled = new List<string>();
			void* head = features.Next;
			for (int i = candidates.Count - 1; i >= 0; i--)
			{
				GenericFeatures* item = &structs[i];
				uint* members = &item->Member0;
				bool any = false;
				for (int m = 0; m < candidates[i].Members.Length; m++)
				{
					if (members[m] != 0)
					{
						any = true;
						enabled.Add(candidates[i].Members[m]);
					}
				}
				if (!any)
				{
					continue;
				}
				item->Next = head;
				head = item;
			}
			features.Next = head;
			enabled.Reverse();
			DefaultCategory.Log.Info("[FSR] Enabled device features for FSR: " + (enabled.Count > 0 ? string.Join(", ", enabled) : "none")
				+ (extensionsAdded.Count > 0 ? ". Extensions: " + string.Join(", ", extensionsAdded) : string.Empty), "AddFeatures", "NewAA", 0);
		}
		catch (Exception ex)
		{
			// Leave the game's chain as it was. FSR still starts, it may just use features the device was created without.
			DefaultCategory.Log.Info("[FSR] Could not add device features for FSR: " + ex.Message, "AddFeatures", "NewAA", 0);
		}
	}
}
