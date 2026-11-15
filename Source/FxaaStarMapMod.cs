// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using KSA;
using StarMap.API;

namespace KSA.Mods.NewAA;

// StarMap finds this class through [StarMapMod] in newaa.dll.
// Shaders and lookup textures are declared in Content/NewAAAssets.xml, which mod.toml lists under assets.
// The game's content loader only registers assets that come from those files.
// The lifecycle method is called with the KSA Mod object, as StarMapImmediateLoadAttribute requires.
[StarMapMod]
public class FxaaStarMapMod
{
	// Runs before the game starts, so before it creates its Vulkan device.
	[StarMapBeforeMain]
	public void OnBeforeMain()
	{
		DlaaDeviceExtensions.Apply();
	}

	// Runs right after the mod loads, before the game prepares its systems.
	[StarMapImmediateLoad]
	public void OnImmediateLoad(Mod mod)
	{
		FxaaMod.Initialize();
	}
}
