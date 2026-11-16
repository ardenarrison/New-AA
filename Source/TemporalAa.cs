// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
namespace KSA.Mods.NewAA;

// The temporal anti-aliasers that run through the DLAA pipeline (DlaaRenderer): the same jittered camera, motion vectors, history
// resets and orbit line overlay, with DLSS, FSR or New AA's own TAA doing the resolve.
public enum TemporalBackend
{
	None,
	Dlss,
	Fsr,
	Taa
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
			if (FsrAntiAliasing.IsSelected)
			{
				return TemporalBackend.Fsr;
			}
			return TaaAntiAliasing.IsSelected ? TemporalBackend.Taa : TemporalBackend.None;
		}
	}

	// DLAA, FSR or TAA is the selected anti-aliasing method.
	public static bool IsSelected => Selected != TemporalBackend.None;

	// The selected backend can run, so the camera is jittered for it.
	public static bool IsReady => Selected switch
	{
		TemporalBackend.Dlss => DlaaRuntime.IsReady,
		TemporalBackend.Fsr => FsrRuntime.IsReady,
		TemporalBackend.Taa => TaaRuntime.IsReady,
		_ => false
	};
}
