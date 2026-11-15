// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using Brutal.Numerics;
using HarmonyLib;

namespace KSA.Mods.NewAA;

// Camera side of TAA. Each frame it jitters the projection by a sub-pixel offset and keeps the
// unjittered matrices needed for reprojection. The jitter is applied only to the view-projection used for
// rendering. The stored projection stays unjittered, so the offset never accumulates across frames.
internal static class TaaCamera
{
	private static int _frame;

	private static bool _hasCurrent;

	public static float4x4 PreviousViewProjection;

	public static float4x4 CurrentViewProjection;

	public static float4x4 CurrentInverseViewProjection;

	// The jitter applied this frame, in render pixels. DLAA needs these.
	public static float JitterPixelsX;

	public static float JitterPixelsY;

	// Sum of absolute differences between this frame's and last frame's unjittered view-projection. Zero when the camera is still.
	public static float CameraMotion;

	// How far the camera moved relative to the object it follows since last frame, in ego (ecliptic-aligned) axes.
	// The view matrix only holds the camera's rotation, so orbiting or zooming is invisible to the matrices and needs this.
	public static float3 CameraTranslation;

	// Rotation and translation together. Zero when the camera is still.
	public static float TotalCameraMotion;

	private static double3 _previousPositionCce;

	private static IFollowable? _previousFollowing;

	// Set when the camera postfix has a jittered matrix ready for the GPU copy.
	public static bool JitterPending;

	public static float4x4 PendingJitteredViewProjection;

	public static float4x4 PendingJitteredProjection;

	private static int _consecutiveJumps;

	private static int _jumpsLogged;

	private const int MaxJumpsLogged = 12;

	private static CameraMode _previousMode;

	// Counts calls to Advance, which is once per rendered frame.
	public static int FrameNumber { get; private set; }

	// The frame on which the camera last jumped (changed what it follows, switched mode, or moved or turned too far to track).
	// DLSS and the per-object history must start over on that frame. Starts at the first frame.
	public static int ResetFrameNumber { get; private set; }

	// A sum of absolute matrix differences above this is a turn too fast to follow (a flick or a snap to a new view).
	// Map view navigation easily turns the camera by this much in a frame, and DLSS follows that fine with correct motion vectors.
	private const float JumpRotation = 6f;

	// Soft jump reasons (a fast turn, a big move) can restart DLSS at most this often. Hard ones (new followed object, new mode) always can.
	private const int SoftJumpCooldownFrames = 30;

	// Moving the camera by more than this fraction of its distance to the followed object in one frame is a jump.
	private const double JumpDistanceFraction = 0.5;

	// Jitter is applied for TAA (when enabled) and for DLAA once NGX is running.
	public static bool IsActive => (TaaAntiAliasing.Enabled && GameSettings.Current.AntiAliasingMethod == TaaAntiAliasing.Method) || DlaaRuntime.IsReady;

	// The camera is not being tracked (DLAA and TAA are off). The next tracked frame starts over.
	public static void Invalidate()
	{
		_hasCurrent = false;
	}

	// Called once per frame with the camera's unjittered matrices.
	public static void Advance(float4x4 viewProjection, float4x4 inverseViewProjection, double3 positionCce, IFollowable? following, CameraMode mode)
	{
		PreviousViewProjection = _hasCurrent ? CurrentViewProjection : viewProjection;
		CameraMotion = Difference(viewProjection, PreviousViewProjection);
		// A point that is fixed relative to the followed object sits at (point - camera) in ego space, so it was
		// ego_now + (camera_now - camera_before) last frame. The difference is small, so float is precise enough.
		double3 translation = _hasCurrent ? positionCce - _previousPositionCce : double3.Zero;
		CameraTranslation = new float3((float)translation.X, (float)translation.Y, (float)translation.Z);
		TotalCameraMotion = CameraMotion + (float)translation.Length();

		FrameNumber++;
		string? reason = null;
		if (!_hasCurrent)
		{
			reason = "first tracked frame";
		}
		else if (!ReferenceEquals(following, _previousFollowing))
		{
			reason = "followed object changed";
		}
		else if (mode != _previousMode)
		{
			reason = "camera mode changed";
		}
		else if (FrameNumber - ResetFrameNumber >= SoftJumpCooldownFrames)
		{
			if (CameraMotion > JumpRotation)
			{
				reason = "turned too fast (" + CameraMotion.ToString("F2") + ")";
			}
			else if (translation.Length() > JumpDistanceFraction * System.Math.Max(positionCce.Length(), 1.0))
			{
				reason = "moved too far (" + translation.Length().ToString("F1") + " m)";
			}
		}
		// A jump is one frame. If the camera keeps looking like it jumped, DLSS would never accumulate anything, so only the first
		// frame of a run restarts it.
		_consecutiveJumps = reason != null ? _consecutiveJumps + 1 : 0;
		if (reason != null && _consecutiveJumps == 1)
		{
			ResetFrameNumber = FrameNumber;
			if (_jumpsLogged < MaxJumpsLogged)
			{
				_jumpsLogged++;
				Brutal.Logging.DefaultCategory.Log.Info("[DLAA] Camera jump, restarting DLSS history: " + reason, "Advance", "NewAA", 0);
			}
		}
		_previousFollowing = following;
		_previousMode = mode;
		_previousPositionCce = positionCce;
		CurrentViewProjection = viewProjection;
		CurrentInverseViewProjection = inverseViewProjection;
		_hasCurrent = true;
		_frame++;
	}

	// Projection with a Halton(2,3) sub-pixel offset applied in clip space.
	public static float4x4 JitteredProjection(float4x4 projection)
	{
		int index = (_frame % 8) + 1;
		int2 size = Program.RenderedViewport.Size;
		JitterPixelsX = Halton(index, 2) - 0.5f;
		JitterPixelsY = Halton(index, 3) - 0.5f;
		float offsetX = (Halton(index, 2) - 0.5f) * 2f / size.X;
		float offsetY = (Halton(index, 3) - 0.5f) * 2f / size.Y;
		// Adding offsetX to clip.x is the same as adding offsetX * w, which is a translation in row 3 of a right-multiplied matrix.
		float4x4 jitter = new float4x4(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, offsetX, offsetY, 0f, 1f);
		return projection * jitter;
	}

	private static float Difference(float4x4 a, float4x4 b)
	{
		return System.Math.Abs(a.M11 - b.M11) + System.Math.Abs(a.M12 - b.M12) + System.Math.Abs(a.M13 - b.M13) + System.Math.Abs(a.M14 - b.M14)
		     + System.Math.Abs(a.M21 - b.M21) + System.Math.Abs(a.M22 - b.M22) + System.Math.Abs(a.M23 - b.M23) + System.Math.Abs(a.M24 - b.M24)
		     + System.Math.Abs(a.M31 - b.M31) + System.Math.Abs(a.M32 - b.M32) + System.Math.Abs(a.M33 - b.M33) + System.Math.Abs(a.M34 - b.M34)
		     + System.Math.Abs(a.M41 - b.M41) + System.Math.Abs(a.M42 - b.M42) + System.Math.Abs(a.M43 - b.M43) + System.Math.Abs(a.M44 - b.M44);
	}

	private static float Halton(int index, int radix)
	{
		float fraction = 1f;
		float result = 0f;
		while (index > 0)
		{
			fraction /= radix;
			result += fraction * (index % radix);
			index /= radix;
		}
		return result;
	}
}

// Runs after Camera.OnFrame. Records the camera's unjittered matrices, then applies the jitter for rendering.
[HarmonyPatch(typeof(Camera), nameof(Camera.OnFrame))]
internal static class CameraOnFramePatch
{
	private static void Postfix(Camera __instance)
	{
		// Safety: if Program.Render never finished last frame, put the unjittered matrices back before anything reads them.
		JitterWindow.End();
		if (!TaaAntiAliasing.Enabled && !DlaaAntiAliasing.IsSelected)
		{
			// Not tracking. The matrices stored for the GPU are from the last tracked frame, so they must never be used again.
			// (Leaving this set after DLAA was switched off made the geometry use a frozen camera while everything else used the live one.)
			TaaCamera.JitterPending = false;
			TaaCamera.Invalidate();
			return;
		}
		// Only the main camera is tracked and jittered. The game also runs cameras for small viewports (part thumbnails, portraits).
		// DLAA does not run on those, so jittering them would leave them shaking with nothing to resolve it.
		IGameViewport? mainViewport = Program.MainViewport;
		if (mainViewport == null || __instance != mainViewport.GetCamera())
		{
			return;
		}
		ref ViewProjection vp = ref AccessTools.FieldRefAccess<Camera, ViewProjection>("_vp")(__instance);
		ref ViewProjection vpInv = ref AccessTools.FieldRefAccess<Camera, ViewProjection>("_vpInv")(__instance);
		TaaCamera.Advance(vp.viewProjection, vpInv.viewProjection, __instance.PositionCce, __instance.Following, mainViewport.Mode);
		// The jittered matrix is only for the GPU. The camera keeps its unjittered one, because the game also uses it on the CPU
		// to place orbit lines, markers and labels (Camera.EgoToScreen). Jittering that moved them by up to half a pixel every
		// frame, away from where DLSS puts the scene. UpdateShaderDataPatch swaps the jittered matrix in only while the GPU copy is made.
		TaaCamera.JitterPending = TaaCamera.IsActive;
		if (TaaCamera.JitterPending)
		{
			TaaCamera.PendingJitteredProjection = TaaCamera.JitteredProjection(vp.projection);
			TaaCamera.PendingJitteredViewProjection = vp.view * TaaCamera.PendingJitteredProjection;
		}
	}
}

// The jittered matrices live in the camera only while the game records a frame's GPU work. That runs from Program.UpdateShaderData to the
// end of Program.Render. Everything the game does with the camera before that (placing orbit lines, markers and labels with
// Camera.EgoToScreen, hover tests, culling) sees the unjittered camera, so it lines up with the image DLSS produces.
// Both the view-projection and the projection are swapped: the game's atmosphere, cloud, ring and sky shaders rebuild their view rays
// from the inverse projection, and the mesh passes build their own view-projection from the projection. All of them must be jittered
// the same way as the geometry, or the limb of a planet, where those layers meet the jittered surface, flips by up to half a pixel
// every frame. Staying swapped until Render returns also keeps the "previous frame" matrices the cloud and atmosphere code stores
// jittered the same way as the current ones.
internal static class JitterWindow
{
	private static readonly AccessTools.FieldRef<Camera, ViewProjection> CameraVp = AccessTools.FieldRefAccess<Camera, ViewProjection>("_vp");

	private static Camera? _camera;

	private static float4x4 _unjitteredViewProjection;

	private static float4x4 _unjitteredProjection;

	public static void Begin(Camera camera)
	{
		if (_camera != null)
		{
			return;
		}
		ref ViewProjection vp = ref CameraVp(camera);
		_unjitteredViewProjection = vp.viewProjection;
		_unjitteredProjection = vp.projection;
		vp.viewProjection = TaaCamera.PendingJitteredViewProjection;
		vp.projection = TaaCamera.PendingJitteredProjection;
		_camera = camera;
	}

	// Safe to call at any time. Does nothing unless a window is open.
	public static void End()
	{
		if (_camera == null)
		{
			return;
		}
		ref ViewProjection vp = ref CameraVp(_camera);
		vp.viewProjection = _unjitteredViewProjection;
		vp.projection = _unjitteredProjection;
		_camera = null;
	}
}

[HarmonyPatch(typeof(Program), nameof(Program.UpdateShaderData))]
internal static class UpdateShaderDataPatch
{
	private static void Prefix(IViewport viewport)
	{
		// Checked again here, not just trusted from the camera patch: the anti-aliasing setting can change after Camera.OnFrame ran.
		if (TaaCamera.JitterPending && (TaaAntiAliasing.Enabled || DlaaAntiAliasing.IsSelected) && viewport.IsMain())
		{
			JitterWindow.Begin(viewport.GetCamera());
		}
	}
}

[HarmonyPatch(typeof(Program), "Render")]
internal static class RenderPatch
{
	private static void Postfix()
	{
		JitterWindow.End();
	}
}
