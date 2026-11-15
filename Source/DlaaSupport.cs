// Copyright 2026 Arden Arrison. Licensed under the Apache License 2.0 (see LICENSE).
using System;
using System.Diagnostics;
using System.Globalization;
using Brutal.Logging;

namespace KSA.Mods.NewAA;

// The menu entry for DLAA (NVIDIA DLSS at native resolution). Uses enum value 8, which the game's enum does not define.
public static class DlaaAntiAliasing
{
	public const int EnumValue = 8;

	public const string Label = "DLAA";

	public static readonly GameSettings.AntiAliasing Method = (GameSettings.AntiAliasing)EnumValue;

	public static bool IsSelected => GameSettings.Current.AntiAliasingMethod == Method;
}

// Checks whether this PC's GPU can run DLAA. Uses nvidia-smi, which ships with the NVIDIA driver.
// DLAA needs an RTX GPU (Tensor cores) with compute capability 7.5 (Turing) or newer.
// The check reads the first GPU that nvidia-smi lists, and runs once per session.
public static class DlaaSupport
{
	private const double MinimumComputeCapability = 7.5;

	private const string LogMember = "DlaaSupport";

	private const string LogFile = "NewAA";

	private static bool _evaluated;

	private static bool _compatible;

	private static string _gpuName = string.Empty;

	private static string _detail = string.Empty;

	private static bool _reported;

	public static bool IsCompatible
	{
		get
		{
			Evaluate();
			return _compatible;
		}
	}

	public static string GpuName
	{
		get
		{
			Evaluate();
			return _gpuName;
		}
	}

	// Logs an incompatible GPU once per session.
	public static void ReportOnce()
	{
		if (_reported)
		{
			return;
		}
		_reported = true;
		Evaluate();
		if (!_compatible)
		{
			DefaultCategory.Log.Info("GPU incompatible with DLAA using no anti aliasing", LogMember, LogFile, 0);
			DefaultCategory.Log.Info("DLAA check: " + _detail, LogMember, LogFile, 0);
		}
	}

	private static void Evaluate()
	{
		if (_evaluated)
		{
			return;
		}
		_evaluated = true;

		string firstLine;
		try
		{
			ProcessStartInfo info = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,compute_cap --format=csv,noheader")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start nvidia-smi");
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(5000);
			firstLine = FirstNonEmptyLine(output);
		}
		catch (Exception ex)
		{
			_detail = "nvidia-smi not available (" + ex.Message + ")";
			return;
		}

		int comma = firstLine.LastIndexOf(',');
		if (comma < 0)
		{
			_detail = "unexpected nvidia-smi output: '" + firstLine + "'";
			return;
		}

		_gpuName = firstLine.Substring(0, comma).Trim();
		string capabilityText = firstLine.Substring(comma + 1).Trim();
		if (!double.TryParse(capabilityText, NumberStyles.Float, CultureInfo.InvariantCulture, out double capability))
		{
			_detail = _gpuName + ", could not read compute capability '" + capabilityText + "'";
			return;
		}

		bool isRtx = _gpuName.Contains("RTX");
		_compatible = isRtx && capability >= MinimumComputeCapability;
		_detail = _gpuName + ", compute capability " + capabilityText + (isRtx ? string.Empty : ", not an RTX GPU");
	}

	private static string FirstNonEmptyLine(string text)
	{
		foreach (string line in text.Split('\n'))
		{
			string trimmed = line.Trim();
			if (trimmed.Length > 0)
			{
				return trimmed;
			}
		}
		return string.Empty;
	}
}
