using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Controllers.Sensors;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;
using NeoSmart.AsyncLock;

namespace LenovoLegionToolkit.Lib.Controllers;

/// <summary>
/// Software, temperature driven fan curve and custom fixed fan speed controller.
///
/// Lenovo firmware does not expose a direct "set fan RPM" method, only a 10-step fan table used in Custom (God) mode.
/// This controller periodically reads CPU/GPU temperatures, evaluates either the user curve or fixed speed setting,
/// and writes a flat fan table (every step set to the target level), so the fans spin at the requested level.
/// Firmware minimums are always kept, so the BIOS still ramps the fans up when the heatsink gets very hot, even if this app stops responding.
/// </summary>
public class FanCurveController
{
    private static readonly TimeSpan ForceRefreshInterval = TimeSpan.FromSeconds(60);

    private const int FAN_TABLE_LENGTH = 10;

    private readonly AsyncLock _lock = new();

    private readonly FanCurveSettings _settings;
    private readonly ApplicationSettings _applicationSettings;
    private readonly GodModeSettings _godModeSettings;
    private readonly GodModeController _godModeController;
    private readonly SensorsController _sensorsController;
    private readonly SensorsGroupController _sensorsGroupController;
    private readonly PowerModeFeature _powerModeFeature;
    private readonly PowerModeListener _powerModeListener;
    private readonly PowerStateListener _powerStateListener;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool? _isSupported;

    private volatile bool _forceRefresh;
    private int? _appliedLevel;
    private DateTime _lastAppliedAt = DateTime.MinValue;

    public event EventHandler<FanCurveStatus>? StatusChanged;

    public FanCurveStatus Status { get; private set; } = FanCurveStatus.Disabled;

    public FanCurveController(FanCurveSettings settings,
        ApplicationSettings applicationSettings,
        GodModeSettings godModeSettings,
        GodModeController godModeController,
        SensorsController sensorsController,
        SensorsGroupController sensorsGroupController,
        PowerModeFeature powerModeFeature,
        PowerModeListener powerModeListener,
        PowerStateListener powerStateListener)
    {
        _settings = settings;
        _applicationSettings = applicationSettings;
        _godModeSettings = godModeSettings;
        _godModeController = godModeController;
        _sensorsController = sensorsController;
        _sensorsGroupController = sensorsGroupController;
        _powerModeFeature = powerModeFeature;
        _powerModeListener = powerModeListener;
        _powerStateListener = powerStateListener;

        // Applying a Custom mode preset writes the preset fan table, so ours has to be written again.
        _godModeController.PresetChanged += (_, _) => _forceRefresh = true;
        _powerModeListener.Changed += (_, e) =>
        {
            if (e.State == PowerModeState.GodMode)
                _forceRefresh = true;
        };
        _powerStateListener.Changed += (_, e) =>
        {
            if (e.PowerStateEvent is PowerStateEvent.Resume or PowerStateEvent.StatusChange)
                _forceRefresh = true;
        };
    }

    public async Task<bool> IsSupportedAsync()
    {
        if (_isSupported.HasValue)
            return _isSupported.Value;

        try
        {
            var mi = await Compatibility.GetMachineInformationAsync().ConfigureAwait(false);
            if (!mi.Properties.SupportsGodMode)
                return (_isSupported = false).Value;

            var hasSensors = await _sensorsController.IsSupportedAsync().ConfigureAwait(false)
                || (_applicationSettings.Store.EnableHardwareSensors && _sensorsGroupController.IsLibreHardwareMonitorInitialized());
            if (!hasSensors)
                return (_isSupported = false).Value;

            var state = await _godModeController.GetStateAsync().ConfigureAwait(false);
            var hasFanTable = state.Presets.TryGetValue(state.ActivePresetId, out var preset) && preset.FanTableInfo.HasValue;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Fan curve support checked. [hasFanTable={hasFanTable}]");

            return (_isSupported = hasFanTable).Value;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to check fan curve support.", ex);

            return (_isSupported = false).Value;
        }
    }

    public async Task<FanTableData[]?> GetFanTableDataAsync()
    {
        try
        {
            var state = await _godModeController.GetStateAsync().ConfigureAwait(false);
            return state.Presets.TryGetValue(state.ActivePresetId, out var preset)
                ? preset.FanTableInfo?.Data
                : null;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to get fan table data.", ex);

            return null;
        }
    }

    public async Task<(int cpuTemperature, int gpuTemperature)> GetTemperaturesAsync()
    {
        var (cpuTemperature, gpuTemperature) = await _sensorsController.GetTemperaturesAsync().ConfigureAwait(false);

        if ((cpuTemperature < 1 || gpuTemperature < 1)
            && _applicationSettings.Store.EnableHardwareSensors
            && _sensorsGroupController.IsLibreHardwareMonitorInitialized())
        {
            try
            {
                await _sensorsGroupController.UpdateAsync(force: false, scope: HardwareUpdateScope.Cpu | HardwareUpdateScope.Gpu).ConfigureAwait(false);
                var snapshot = _sensorsGroupController.Snapshot;

                if (cpuTemperature < 1)
                {
                    var lhmCpuTemp = snapshot[SensorItem.CpuTemperature];
                    if (!float.IsNaN(lhmCpuTemp) && lhmCpuTemp is > 0f and < 150f)
                        cpuTemperature = (int)Math.Round(lhmCpuTemp);
                }

                if (gpuTemperature < 1)
                {
                    var lhmGpuTemp = snapshot[SensorItem.GpuCoreTemperature];
                    if (!float.IsNaN(lhmGpuTemp) && lhmGpuTemp is > 0f and < 150f)
                        gpuTemperature = (int)Math.Round(lhmGpuTemp);
                }
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Failed to read fallback temperatures from LibreHardwareMonitor.", ex);
            }
        }

        return (cpuTemperature, gpuTemperature);
    }

    public async Task<(int cpuFanSpeed, int gpuFanSpeed)> GetFanSpeedsAsync()
    {
        var fanSpeeds = await _sensorsController.GetFanSpeedsAsync().ConfigureAwait(false);
        return (fanSpeeds.CpuFanSpeed, fanSpeeds.GpuFanSpeed);
    }

    public async Task StartStopIfNeededAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
        {
            var enabled = _settings.Store.Enabled;

            await StopInternalAsync(!enabled).ConfigureAwait(false);

            if (!enabled)
            {
                UpdateStatus(FanCurveStatus.Disabled);
                return;
            }

            if (!await IsSupportedAsync().ConfigureAwait(false))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Fan curve is not supported on this device.");

                UpdateStatus(FanCurveStatus.NotSupported);
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Run(() => RunAsync(token), token);
        }
    }

    public async Task StopAsync()
    {
        using (await _lock.LockAsync().ConfigureAwait(false))
        {
            await StopInternalAsync(true).ConfigureAwait(false);
            UpdateStatus(FanCurveStatus.Disabled);
        }
    }

    public void RequestImmediateRefresh() => _forceRefresh = true;

    private async Task StopInternalAsync(bool restore)
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        if (_task is not null)
        {
            try { await _task.ConfigureAwait(false); }
            catch { /* Ignored. */ }
        }

        _cts?.Dispose();
        _cts = null;
        _task = null;

        if (restore && _appliedLevel.HasValue)
            await RestorePresetFanTableAsync().ConfigureAwait(false);

        _appliedLevel = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Fan curve started.");

        _forceRefresh = true;

        ushort[] minimum;
        try
        {
            minimum = (await _godModeController.GetMinimumFanTableAsync().ConfigureAwait(false)).GetTable();
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to get minimum fan table, using safe fallback.", ex);

            minimum = [1, 1, 1, 1, 1, 1, 1, 1, 3, 5];
        }

        while (!token.IsCancellationRequested)
        {
            var store = _settings.Store;

            try
            {
                await TickAsync(store, minimum).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Fan curve tick failed.", ex);

                UpdateStatus(Status with { State = FanCurveState.Error });
                _forceRefresh = true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(store.Interval, 1, 30)), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Fan curve stopped.");
    }

    private async Task TickAsync(FanCurveSettings.FanCurveSettingsStore store, IReadOnlyList<ushort> minimum)
    {
        var (cpuTemperature, gpuTemperature) = await GetTemperaturesAsync().ConfigureAwait(false);
        var fanSpeeds = await _sensorsController.GetFanSpeedsAsync().ConfigureAwait(false);
        var cpuFanSpeed = fanSpeeds.CpuFanSpeed;
        var gpuFanSpeed = fanSpeeds.GpuFanSpeed;
        var temperature = GetSourceTemperature(store.Source, cpuTemperature, gpuTemperature);

        var powerMode = await _powerModeFeature.GetStateAsync().ConfigureAwait(false);
        if (powerMode != PowerModeState.GodMode)
        {
            _appliedLevel = null;
            UpdateStatus(new(FanCurveState.InactivePowerMode, cpuTemperature, gpuTemperature, temperature, -1, -1, cpuFanSpeed, gpuFanSpeed));
            return;
        }

        if (GetActiveGodModePreset()?.FanFullSpeed == true)
        {
            _appliedLevel = null;
            UpdateStatus(new(FanCurveState.InactiveFanFullSpeed, cpuTemperature, gpuTemperature, temperature, 100, FanCurve.MAX_LEVEL, cpuFanSpeed, gpuFanSpeed));
            return;
        }

        var (level, isCriticalOverride) = EvaluateTargetLevel(store, temperature, _appliedLevel);

        var forceRefresh = _forceRefresh || DateTime.UtcNow - _lastAppliedAt > ForceRefreshInterval;
        if (level != _appliedLevel || forceRefresh)
        {
            _forceRefresh = false;

            await ApplyLevelAsync(level, minimum).ConfigureAwait(false);

            if (Log.Instance.IsTraceEnabled && level != _appliedLevel)
                Log.Instance.Trace($"Fan level changed. [mode={store.Mode}, temperature={temperature}, cpu={cpuTemperature}, gpu={gpuTemperature}, level={_appliedLevel?.ToString() ?? "-"} -> {level}, critical={isCriticalOverride}]");

            _appliedLevel = level;
            _lastAppliedAt = DateTime.UtcNow;
        }

        var effectiveLevel = Math.Max(level, minimum.Count > 0 ? minimum[0] : 0);
        UpdateStatus(new(FanCurveState.Active, cpuTemperature, gpuTemperature, temperature, FanCurve.LevelToSpeed(effectiveLevel), effectiveLevel, cpuFanSpeed, gpuFanSpeed, isCriticalOverride));
    }

    public static (int level, bool isCriticalOverride) EvaluateTargetLevel(
        FanCurveSettings.FanCurveSettingsStore store,
        int temperature,
        int? previousLevel)
    {
        var criticalTemp = Math.Clamp(store.CriticalTemperature, 70, 105);

        if (temperature <= 0)
        {
            // Temperature could not be read - better loud than hot.
            return (FanCurve.MAX_LEVEL, true);
        }

        if (temperature >= criticalTemp)
            return (FanCurve.MAX_LEVEL, true);

        if (store.Mode == FanCurveMode.Fixed)
        {
            var fixedLevel = FanCurve.SpeedToLevel(Math.Clamp(store.FixedSpeed, 0, 100));
            return (fixedLevel, false);
        }

        var points = FanCurve.Normalize(store.Points);
        var level = FanCurve.SpeedToLevel(FanCurve.GetSpeed(points, temperature));

        // Hysteresis: only slow down once the temperature dropped by the hysteresis below the threshold.
        if (previousLevel is { } currentLevel && level < currentLevel)
        {
            var hysteresis = Math.Clamp(store.Hysteresis, 0, 20);
            var hysteresisLevel = FanCurve.SpeedToLevel(FanCurve.GetSpeed(points, temperature + hysteresis));
            level = Math.Min(currentLevel, hysteresisLevel);
        }

        return (level, false);
    }

    public static int GetSourceTemperature(FanCurveTemperatureSource source, int cpuTemperature, int gpuTemperature) => source switch
    {
        FanCurveTemperatureSource.CPU => cpuTemperature > 0 ? cpuTemperature : gpuTemperature,
        FanCurveTemperatureSource.GPU => gpuTemperature > 0 ? gpuTemperature : cpuTemperature,
        _ => Math.Max(cpuTemperature, gpuTemperature)
    };

    private void UpdateStatus(FanCurveStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static async Task ApplyLevelAsync(int level, IReadOnlyList<ushort> minimum)
    {
        var table = new ushort[FAN_TABLE_LENGTH];
        for (var i = 0; i < table.Length; i++)
        {
            var min = i < minimum.Count ? minimum[i] : 0;
            table[i] = (ushort)Math.Clamp(Math.Max(level, min), 0, FanCurve.MAX_LEVEL);
        }

        var fanTable = new FanTable(table);
        await WMI.LenovoFanMethod.FanSetTableAsync(fanTable.GetBytes()).ConfigureAwait(false);
    }

    private GodModeSettings.GodModeSettingsStore.Preset? GetActiveGodModePreset()
    {
        var store = _godModeSettings.Store;
        return store.Presets.GetValueOrDefault(store.ActivePresetId);
    }

    private async Task RestorePresetFanTableAsync()
    {
        try
        {
            if (await _powerModeFeature.GetStateAsync().ConfigureAwait(false) != PowerModeState.GodMode)
                return;

            var preset = GetActiveGodModePreset();
            if (preset?.FanFullSpeed == true)
                return;

            var fanTable = preset?.FanTable ?? await _godModeController.GetDefaultFanTableAsync().ConfigureAwait(false);

            var minimum = (await _godModeController.GetMinimumFanTableAsync().ConfigureAwait(false)).GetTable();
            var values = fanTable.GetTable();
            var isValid = true;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] > FanCurve.MAX_LEVEL || (i < minimum.Length && values[i] < minimum[i]))
                    isValid = false;
            }

            if (!isValid)
                fanTable = await _godModeController.GetDefaultFanTableAsync().ConfigureAwait(false);

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Restoring preset fan table... [table={fanTable}]");

            await WMI.LenovoFanMethod.FanSetTableAsync(fanTable.GetBytes()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to restore preset fan table.", ex);
        }
    }
}
