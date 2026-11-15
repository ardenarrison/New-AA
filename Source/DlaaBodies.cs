// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace KSA.Mods.NewAA;

// Push constant for DlaaBodyMotionFrag.frag. Layout matches its push_constant block.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DlaaBodyData
{
	public float4x4 PrevViewProjection;

	public float4x4 InvViewProjection;

	public DlaaBodyMotion Body;
}

// One rigid body's motion between last frame and this one, in the form the motion shader uses.
// A point p fixed to the body (ego space, this frame) was at  p + B * p + T  in last frame's ego space.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DlaaBodyMotion
{
	// xyz = body centre in ego space this frame, w = outer radius
	public float4 Center;

	// x = inner radius, y = 1 for a celestial body (spherical shell), 0 for a vehicle
	public float4 Info;

	// Columns of B = previousOrientation * inverse(currentOrientation) - identity
	public float4 BColumn0;

	public float4 BColumn1;

	public float4 BColumn2;

	// xyz = T = previousCentre - previousOrientation * inverse(currentOrientation) * currentCentre
	public float4 Translation;

	// Everything that has to be a difference of large numbers is done here in double, then stored as small floats.
	public static DlaaBodyMotion Compute(double3 currentCenter, doubleQuat currentOrientation, double3 previousCenter, doubleQuat previousOrientation, float outerRadius, float innerRadius, bool celestial)
	{
		doubleQuat inverseCurrent = currentOrientation.Inverse();
		double3 Rotate(double3 value) => value.Transform(inverseCurrent).Transform(previousOrientation);

		double3 column0 = Rotate(double3.UnitX) - double3.UnitX;
		double3 column1 = Rotate(double3.UnitY) - double3.UnitY;
		double3 column2 = Rotate(double3.UnitZ) - double3.UnitZ;
		double3 translation = previousCenter - Rotate(currentCenter);

		return new DlaaBodyMotion
		{
			Center = new float4((float)currentCenter.X, (float)currentCenter.Y, (float)currentCenter.Z, outerRadius),
			Info = new float4(innerRadius, celestial ? 1f : 0f, 0f, 0f),
			BColumn0 = new float4((float)column0.X, (float)column0.Y, (float)column0.Z, 0f),
			BColumn1 = new float4((float)column1.X, (float)column1.Y, (float)column1.Z, 0f),
			BColumn2 = new float4((float)column2.X, (float)column2.Y, (float)column2.Z, 0f),
			Translation = new float4((float)translation.X, (float)translation.Y, (float)translation.Z, 0f)
		};
	}

	// What the motion shader does: where a point was last frame, in last frame's ego space.
	public readonly float3 PreviousPosition(float3 p)
	{
		return new float3(
			p.X + p.X * BColumn0.X + p.Y * BColumn1.X + p.Z * BColumn2.X + Translation.X,
			p.Y + p.X * BColumn0.Y + p.Y * BColumn1.Y + p.Z * BColumn2.Y + Translation.Y,
			p.Z + p.X * BColumn0.Z + p.Y * BColumn1.Z + p.Z * BColumn2.Z + Translation.Z);
	}

	// How far, in metres, a point on the body ends up from where the camera-only vectors would put it, at most.
	// distance = camera to body centre, radius = the body's outer radius, cameraTranslation = what the camera-only vectors assume.
	public readonly float MaxDeviation(float3 cameraTranslation, float distance)
	{
		float bNorm = MathF.Sqrt(
			BColumn0.X * BColumn0.X + BColumn0.Y * BColumn0.Y + BColumn0.Z * BColumn0.Z
			+ BColumn1.X * BColumn1.X + BColumn1.Y * BColumn1.Y + BColumn1.Z * BColumn1.Z
			+ BColumn2.X * BColumn2.X + BColumn2.Y * BColumn2.Y + BColumn2.Z * BColumn2.Z);
		float dx = Translation.X - cameraTranslation.X;
		float dy = Translation.Y - cameraTranslation.Y;
		float dz = Translation.Z - cameraTranslation.Z;
		return bNorm * (distance + Center.W) + MathF.Sqrt(dx * dx + dy * dy + dz * dz);
	}
}

// Finds the vehicles and celestial bodies near the camera and works out how each moved since last frame.
// The game stores positions relative to the camera and only puts the camera's rotation in the view matrix, so this is the only place
// that knows an object moved. Without it, anything that moves on its own (a turning vehicle, a spinning planet) leaves a trail.
public static class DlaaBodies
{
	private const int MaxCelestials = 6;

	private const int MaxVehicles = 12;

	// Smallest angular radius, in radians, worth a pass of its own. A pixel is about 0.0008 radians at 1080p.
	private const float MinCelestialAngularRadius = 1e-3f;

	private const float MinVehicleAngularRadius = 1e-4f;

	// Angle one pixel covers at 1080p with the default field of view.
	private const float PixelAngle = 8e-4f;

	private struct State
	{
		public double3 PositionEgo;

		public doubleQuat Orientation;
	}

	private readonly struct Candidate(float sortKey, DlaaBodyMotion motion)
	{
		public readonly float SortKey = sortKey;

		public readonly DlaaBodyMotion Motion = motion;
	}

	private static Dictionary<Astronomical, State> _previous = new Dictionary<Astronomical, State>();

	private static Dictionary<Astronomical, State> _current = new Dictionary<Astronomical, State>();

	private static readonly List<Candidate> Celestials = new List<Candidate>();

	private static readonly List<Candidate> Vehicles = new List<Candidate>();

	private static int _capturedFrame = -1;

	private static bool _warned;

	// The passes to draw this frame, in order: big celestial bodies first, vehicles last, so smaller things overwrite what they sit on.
	public static readonly List<DlaaBodyMotion> Draws = new List<DlaaBodyMotion>();

	// Records every body's state for next frame and builds this frame's draw list. Safe to call once per viewport per frame.
	public static void Capture(int frame, Camera camera, bool reset, float3 cameraTranslation)
	{
		if (frame == _capturedFrame)
		{
			return;
		}
		_capturedFrame = frame;
		try
		{
			CaptureCore(camera, reset, cameraTranslation);
		}
		catch (Exception ex)
		{
			// Motion vectors are an improvement, never a reason to lose the frame. Fall back to camera-only vectors.
			Draws.Clear();
			_previous.Clear();
			_current.Clear();
			if (!_warned)
			{
				_warned = true;
				Brutal.Logging.DefaultCategory.Log.Info("[DLAA] Per-object motion vectors disabled for this frame: " + ex.Message, "Capture", "NewAA", 0);
			}
		}
	}

	private static void CaptureCore(Camera camera, bool reset, float3 cameraTranslation)
	{
		Draws.Clear();
		Celestials.Clear();
		Vehicles.Clear();
		_current.Clear();

		CelestialSystem? system = Universe.CurrentSystem;
		if (system == null)
		{
			SwapStates();
			return;
		}

		ReadOnlySpan<Astronomical> all = system.All.AsSpan();
		for (int i = 0; i < all.Length; i++)
		{
			Astronomical body = all[i];
			bool isVehicle = body is Vehicle;
			if (!isVehicle && body is not Celestial)
			{
				continue;
			}

			double3 position = camera.GetPositionEgo(body);
			doubleQuat orientation = body.GetBodyFixed2Ecl();
			_current[body] = new State { PositionEgo = position, Orientation = orientation };
			if (reset || !_previous.TryGetValue(body, out State previous))
			{
				continue;
			}

			float radius = isVehicle ? ((Vehicle)body).BoundingSphereRadiusBody : (float)body.MeanRadius;
			double distance = position.Length();
			if (radius <= 0f || distance <= 0.0)
			{
				continue;
			}

			float angularRadius = (float)(radius / distance);
			if (angularRadius < (isVehicle ? MinVehicleAngularRadius : MinCelestialAngularRadius))
			{
				continue;
			}

			// A vehicle is a sphere around its centre. A celestial body is a shell: the surface plus its terrain, nothing deeper.
			float outer = isVehicle ? radius * 1.1f : radius * 1.02f + 2000f;
			float inner = isVehicle ? 0f : radius * 0.5f;
			DlaaBodyMotion motion = DlaaBodyMotion.Compute(position, orientation, previous.PositionEgo, previous.Orientation, outer, inner, !isVehicle);
			// A celestial body that moves less than a tenth of a pixel from what the camera-only vectors already give needs no pass,
			// measured at its nearest point (where a pixel is smallest).
			// A vehicle always gets one, even when it is perfectly still in view. It often sits inside a planet's shell (on the pad, in low
			// orbit), and without its own pass the planet's pass would write the planet's motion over the vehicle's pixels.
			if (!isVehicle)
			{
				float nearestDistance = MathF.Max((float)distance - outer, 0.5f);
				if (motion.MaxDeviation(cameraTranslation, (float)distance) < 0.1f * PixelAngle * nearestDistance)
				{
					continue;
				}
			}
			// Bigger bodies first, so a vehicle standing on a planet overwrites the planet's value.
			(isVehicle ? Vehicles : Celestials).Add(new Candidate(angularRadius, motion));
		}

		AddNearest(Celestials, MaxCelestials);
		AddNearest(Vehicles, MaxVehicles);
		SwapStates();
	}

	// Forget everything. Used when DLAA stops running, so a stale history is never used later.
	public static void Clear()
	{
		_capturedFrame = -1;
		Draws.Clear();
		_previous.Clear();
		_current.Clear();
	}

	private static void AddNearest(List<Candidate> candidates, int max)
	{
		// Keep the ones that cover the most of the screen, then draw them largest radius first.
		candidates.Sort((a, b) => b.SortKey.CompareTo(a.SortKey));
		if (candidates.Count > max)
		{
			candidates.RemoveRange(max, candidates.Count - max);
		}
		candidates.Sort((a, b) => b.Motion.Center.W.CompareTo(a.Motion.Center.W));
		foreach (Candidate candidate in candidates)
		{
			Draws.Add(candidate.Motion);
		}
	}

	private static void SwapStates()
	{
		(_previous, _current) = (_current, _previous);
	}
}
