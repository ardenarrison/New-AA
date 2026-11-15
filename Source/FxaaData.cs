// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace KSA.Rendering.PostProcessing;

// Push-constant block for FxaaFrag.frag. Same layout as Cmaa2BlitData.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FxaaData
{
	// x = 1/width, y = 1/height, z = width, w = height
	public float4 RtMetrics;
}
