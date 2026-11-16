// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
namespace KSA.Mods.NewAA;

// The temporal anti-aliasers that run through the DLAA pipeline (DlaaRenderer): the same jittered camera, motion vectors, history
// resets and orbit line overlay, with DLSS or FSR doing the resolve.
public enum TemporalBackend
{
	None,
	Dlss,
	Fsr
}

internal static class TemporalAa
{
	public static TemporalBackend Selected
	{
		get
		{
			if (DlaaAntiAliasing.IsSelected)
			{
				return TemporalBackend.Dlss;
			}
			return FsrAntiAliasing.IsSelected ? TemporalBackend.Fsr : TemporalBackend.None;
		}
	}

	// DLAA or FSR is the selected anti-aliasing method.
	public static bool IsSelected => Selected != TemporalBackend.None;

	// The selected backend's runtime is running, so the camera is jittered for it.
	public static bool IsReady => Selected switch
	{
		TemporalBackend.Dlss => DlaaRuntime.IsReady,
		TemporalBackend.Fsr => FsrRuntime.IsReady,
		_ => false
	};
}
