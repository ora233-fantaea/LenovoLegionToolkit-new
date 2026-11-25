using System;
using System.Collections.Generic;
using System.Linq;

namespace LenovoLegionToolkit.Lib;

public enum FanCurveMode
{
    Curve,
    Fixed
}

public enum FanCurveTemperatureSource
{
    CPU,
    GPU,
    Max
}

public enum FanCurveState
{
    Disabled,
    NotSupported,
    InactivePowerMode,
    InactiveFanFullSpeed,
    Active,
    Error
}

/// <summary>
/// Single point of a temperature based fan curve.
/// </summary>
/// <param name="Temperature">Temperature in °C.</param>
/// <param name="Speed">Fan speed in percent (0-100).</param>
public readonly record struct FanCurvePoint(int Temperature, int Speed)
{
    public override string ToString() => $"{Temperature}°C@{Speed}%";
}

public readonly record struct FanCurveStatus(
    FanCurveState State,
    int CpuTemperature,
    int GpuTemperature,
    int Temperature,
    int Speed,
    int Level,
    int CpuFanSpeed = -1,
    int GpuFanSpeed = -1,
    bool IsCriticalOverride = false)
{
    public static readonly FanCurveStatus Disabled = new(FanCurveState.Disabled, -1, -1, -1, -1, -1);
    public static readonly FanCurveStatus NotSupported = new(FanCurveState.NotSupported, -1, -1, -1, -1, -1);
}

public static class FanCurve
{
    public const int MIN_TEMPERATURE = 20;
    public const int MAX_TEMPERATURE = 100;
    public const int MIN_POINTS = 2;
    public const int MAX_POINTS = 12;
    public const int MAX_LEVEL = 10;
    public const int SPEED_STEP = 100 / MAX_LEVEL;

    public static FanCurvePoint[] DefaultPoints =>
    [
        new(40, 10),
        new(50, 20),
        new(60, 30),
        new(70, 50),
        new(80, 70),
        new(90, 100)
    ];

    public static FanCurvePoint[] QuietPoints =>
    [
        new(45, 10),
        new(60, 20),
        new(70, 30),
        new(80, 50),
        new(88, 70),
        new(95, 100)
    ];

    public static FanCurvePoint[] PerformancePoints =>
    [
        new(35, 20),
        new(45, 30),
        new(55, 50),
        new(65, 70),
        new(75, 90),
        new(85, 100)
    ];

    /// <summary>
    /// Clamps, sorts and de-duplicates points and makes sure the speed never decreases with rising temperature.
    /// </summary>
    public static FanCurvePoint[] Normalize(IEnumerable<FanCurvePoint>? points)
    {
        var result = (points ?? [])
            .Select(p => new FanCurvePoint(Math.Clamp(p.Temperature, MIN_TEMPERATURE, MAX_TEMPERATURE), Math.Clamp(p.Speed, 0, 100)))
            .GroupBy(p => p.Temperature)
            .Select(g => g.MaxBy(p => p.Speed))
            .OrderBy(p => p.Temperature)
            .Take(MAX_POINTS)
            .ToList();

        if (result.Count < MIN_POINTS)
            return DefaultPoints;

        for (var i = 1; i < result.Count; i++)
        {
            if (result[i].Speed < result[i - 1].Speed)
                result[i] = result[i] with { Speed = result[i - 1].Speed };
        }

        return [.. result];
    }

    /// <summary>
    /// Linear interpolation of the curve. Below the first point the first speed is used, above the last point the last speed is used.
    /// </summary>
    public static double GetSpeed(IReadOnlyList<FanCurvePoint> points, double temperature)
    {
        if (points.Count == 0)
            return 100;

        var first = points[0];
        var last = points[points.Count - 1];

        if (temperature <= first.Temperature)
            return first.Speed;

        if (temperature >= last.Temperature)
            return last.Speed;

        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];

            if (temperature > b.Temperature)
                continue;

            if (b.Temperature == a.Temperature)
                return b.Speed;

            var t = (temperature - a.Temperature) / (b.Temperature - a.Temperature);
            return a.Speed + (b.Speed - a.Speed) * t;
        }

        return last.Speed;
    }

    /// <summary>
    /// Converts speed in percent to a BIOS fan level (0-10). Rounds up, so the fan never spins slower than requested.
    /// </summary>
    public static int SpeedToLevel(double speed) => Math.Clamp((int)Math.Ceiling(Math.Round(speed, 3) / SPEED_STEP), 0, MAX_LEVEL);

    public static int LevelToSpeed(int level) => Math.Clamp(level, 0, MAX_LEVEL) * SPEED_STEP;

    /// <summary>
    /// Estimates fan RPM for a given BIOS level (0-10) using firmware fan table data when available.
    /// </summary>
    public static int? EstimateRpm(IEnumerable<FanTableData>? tableData, FanTableType type, int level)
    {
        if (tableData is null)
            return null;

        var entry = tableData.FirstOrDefault(d => d.Type == type);
        if (entry.FanSpeeds is null || entry.FanSpeeds.Length == 0)
            return null;

        if (level <= 0)
            return 0;

        var index = Math.Clamp(level - 1, 0, entry.FanSpeeds.Length - 1);
        return entry.FanSpeeds[index];
    }
}
