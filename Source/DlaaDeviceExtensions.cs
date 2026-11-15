// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Logging;
using Brutal.VulkanApi;
using HarmonyLib;
using Render;

namespace KSA.Mods.NewAA;

// NVIDIA NGX (DLSS/DLAA) loads its GPU kernels through Vulkan extensions that have to be enabled when the VkDevice is created.
// The game creates its device before any mod loads, without these, so NGX reports DLAA unavailable
// ("Request VK_NVX_binary_import extension" in ngx_log.txt). This adds them to the game's device extension list.
// The list is what NVSDK_NGX_VULKAN_RequiredExtensions reports for the DLSS SDK this mod is built with.
// Applied from StarMapBeforeMain, which runs before the game creates its device.
internal static class DlaaDeviceExtensions
{
	private static readonly string[] Required =
	{
		"VK_NVX_binary_import",
		"VK_NVX_image_view_handle",
		"VK_KHR_push_descriptor",
		"VK_EXT_buffer_device_address"
	};

	private const string HarmonyId = "ksa.mod.newaa.device";

	public static void Apply()
	{
		try
		{
			MethodBase? target = AccessTools.Method(typeof(HdrOutput), "AddDeviceExtensions");
			if (target == null)
			{
				Console.Error.WriteLine("[DLAA] HdrOutput.AddDeviceExtensions not found. DLAA will be unavailable.");
				return;
			}
			new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(typeof(DlaaDeviceExtensions), nameof(AddExtensions)));
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("[DLAA] Could not patch device creation: " + ex.Message);
		}
	}

	// Runs at the end of HdrOutput.AddDeviceExtensions, the last step before the game calls CreateDevice.
	private static void AddExtensions(PhysicalDevice physicalDevice, HashSet<string> deviceExtensions)
	{
		List<string> added = new List<string>(Required.Length);
		List<string> unsupported = new List<string>(Required.Length);
		foreach (string extension in Required)
		{
			// VK_EXT_buffer_device_address can't be enabled together with the KHR version.
			if (extension == "VK_EXT_buffer_device_address" && deviceExtensions.Contains("VK_KHR_buffer_device_address"))
			{
				continue;
			}
			if (!physicalDevice.SupportsExtension(extension))
			{
				unsupported.Add(extension);
			}
			else if (deviceExtensions.Add(extension))
			{
				added.Add(extension);
			}
		}
		DefaultCategory.Log.Info("[DLAA] Enabled device extensions for NGX: " + (added.Count > 0 ? string.Join(", ", added) : "none")
			+ (unsupported.Count > 0 ? ". Not supported by this GPU: " + string.Join(", ", unsupported) : string.Empty), "AddExtensions", "NewAA", 0);
	}
}
