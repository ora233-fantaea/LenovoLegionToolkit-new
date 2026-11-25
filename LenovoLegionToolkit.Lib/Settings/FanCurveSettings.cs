namespace LenovoLegionToolkit.Lib.Settings;

public class FanCurveSettings() : AbstractSettings<FanCurveSettings.FanCurveSettingsStore>("fan_curve.json")
{
    public const int DEFAULT_FIXED_SPEED = 50;
    public const int DEFAULT_HYSTERESIS = 3;
    public const int DEFAULT_INTERVAL = 2;
    public const int DEFAULT_CRITICAL_TEMPERATURE = 95;

    public class FanCurveSettingsStore
    {
        public bool Enabled { get; set; }
        public FanCurveMode Mode { get; set; } = FanCurveMode.Curve;
        public int FixedSpeed { get; set; } = DEFAULT_FIXED_SPEED;
        public FanCurveTemperatureSource Source { get; set; } = FanCurveTemperatureSource.Max;
        public int Hysteresis { get; set; } = DEFAULT_HYSTERESIS;
        public int Interval { get; set; } = DEFAULT_INTERVAL;
        public int CriticalTemperature { get; set; } = DEFAULT_CRITICAL_TEMPERATURE;
        public FanCurvePoint[] Points { get; set; } = FanCurve.DefaultPoints;
    }
}
