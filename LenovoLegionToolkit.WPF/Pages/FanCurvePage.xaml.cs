using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Common;
using Button = Wpf.Ui.Controls.Button;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class FanCurvePage
{
    private readonly FanCurveSettings _settings = IoCContainer.Resolve<FanCurveSettings>();
    private readonly FanCurveController _controller = IoCContainer.Resolve<FanCurveController>();
    private readonly PowerModeFeature _powerModeFeature = IoCContainer.Resolve<PowerModeFeature>();
    private readonly ApplicationSettings _applicationSettings = IoCContainer.Resolve<ApplicationSettings>();

    private FanTableData[]? _fanTableData;
    private CancellationTokenSource? _telemetryCts;
    private Task? _telemetryTask;

    private bool _isLoading;
    private bool _isUpdatingPointBoxes;
    private bool _isTypingInPointBox;
    private bool _isTypingInParameterBox;
    private bool _hasPendingCurveChanges;

    public static Task<bool> IsSupportedAsync() => IoCContainer.Resolve<FanCurveController>().IsSupportedAsync();

    public FanCurvePage()
    {
        InitializeComponent();

        _curveControl.CurveChanged += CurveControl_CurveChanged;
        _curveControl.CurveCommitted += CurveControl_CurveCommitted;
        _curveControl.SelectedPointChanged += CurveControl_SelectedPointChanged;

        IsVisibleChanged += FanCurvePage_IsVisibleChanged;
    }

    private async void FanCurvePage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            await LoadAsync();
            _controller.StatusChanged += Controller_StatusChanged;
            StartTelemetryLoop();
            return;
        }

        _controller.StatusChanged -= Controller_StatusChanged;
        await StopTelemetryLoopAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            var store = _settings.Store;

            _fanTableData = await _controller.GetFanTableDataAsync();
            _curveControl.SetFanTableData(_fanTableData);

            _enableToggle.IsChecked = store.Enabled;

            _modeComboBox.SetItems(
                [FanCurveMode.Curve, FanCurveMode.Fixed],
                store.Mode,
                GetModeDisplayName);

            _sourceComboBox.SetItems(
                [FanCurveTemperatureSource.Max, FanCurveTemperatureSource.CPU, FanCurveTemperatureSource.GPU],
                store.Source,
                GetSourceDisplayName);

            _fixedSpeedSlider.Value = Math.Clamp(store.FixedSpeed, 0, 100);
            UpdateFixedSpeedLabel((int)_fixedSpeedSlider.Value);

            _hysteresisBox.Value = Math.Clamp(store.Hysteresis, 0, 20);
            _intervalBox.Value = Math.Clamp(store.Interval, 1, 30);
            _criticalTempBox.Value = Math.Clamp(store.CriticalTemperature, 70, 105);

            _curveControl.CriticalTemperature = Math.Clamp(store.CriticalTemperature, 70, 105);
            _curveControl.Hysteresis = Math.Clamp(store.Hysteresis, 0, 20);
            _curveControl.SetPoints(store.Points);

            SetPendingChanges(false);
            UpdateModeVisibility(store.Mode);
            SyncPointEditorAndPills();
            ApplyStatus(_controller.Status);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void StartTelemetryLoop()
    {
        _telemetryCts?.Cancel();
        _telemetryCts = new CancellationTokenSource();
        var token = _telemetryCts.Token;

        _telemetryTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_settings.Store.Enabled)
                    {
                        var (cpuTemp, gpuTemp) = await _controller.GetTemperaturesAsync().ConfigureAwait(false);
                        var (cpuFan, gpuFan) = await _controller.GetFanSpeedsAsync().ConfigureAwait(false);
                        var sourceTemp = FanCurveController.GetSourceTemperature(_settings.Store.Source, cpuTemp, gpuTemp);
                        var status = new FanCurveStatus(FanCurveState.Disabled, cpuTemp, gpuTemp, sourceTemp, -1, -1, cpuFan, gpuFan);
                        await Dispatcher.InvokeAsync(() => ApplyStatus(status));
                    }
                    else
                    {
                        var status = _controller.Status;
                        await Dispatcher.InvokeAsync(() => ApplyStatus(status));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Fan curve telemetry refresh failed.", ex);
                }
            }
        }, token);
    }

    private async Task StopTelemetryLoopAsync()
    {
        if (_telemetryCts is not null)
            await _telemetryCts.CancelAsync();

        if (_telemetryTask is not null)
        {
            try { await _telemetryTask; }
            catch { /* Ignored. */ }
        }

        _telemetryCts?.Dispose();
        _telemetryCts = null;
        _telemetryTask = null;
    }

    private void Controller_StatusChanged(object? sender, FanCurveStatus status) => Dispatcher.InvokeAsync(() => ApplyStatus(status));

    private void ApplyStatus(FanCurveStatus status)
    {
        _cpuTempText.Text = status.CpuTemperature > 0 ? FormatTemperature(status.CpuTemperature) : "-";
        _gpuTempText.Text = status.GpuTemperature > 0 ? FormatTemperature(status.GpuTemperature) : "-";

        if (status.CpuFanSpeed >= 0 && status.GpuFanSpeed >= 0)
            _fanSpeedText.Text = $"{status.CpuFanSpeed} / {status.GpuFanSpeed} {Resource.RPM}";
        else if (status.CpuFanSpeed >= 0)
            _fanSpeedText.Text = $"{status.CpuFanSpeed} {Resource.RPM}";
        else
            _fanSpeedText.Text = "-";

        if (status.State == FanCurveState.Active && status.Speed >= 0)
            _targetText.Text = $"{status.Speed}% (Lv.{status.Level})";
        else if (status.State == FanCurveState.InactiveFanFullSpeed)
            _targetText.Text = "100% (Max)";
        else
            _targetText.Text = "-";

        _powerModeWarningInfoBar.IsOpen = _settings.Store.Enabled && status.State == FanCurveState.InactivePowerMode;
        _fullSpeedWarningInfoBar.IsOpen = _settings.Store.Enabled && status.State == FanCurveState.InactiveFanFullSpeed;

        if (status.State == FanCurveState.Active && status.IsCriticalOverride)
        {
            _statusBadge.Appearance = ControlAppearance.Danger;
            _statusBadge.Content = Resource.FanCurvePage_Status_Critical;
        }
        else
        {
            (_statusBadge.Appearance, _statusBadge.Content) = status.State switch
            {
                FanCurveState.Active => (ControlAppearance.Success, Resource.FanCurvePage_Status_Active),
                FanCurveState.InactivePowerMode => (ControlAppearance.Caution, Resource.FanCurvePage_Status_InactivePowerMode),
                FanCurveState.InactiveFanFullSpeed => (ControlAppearance.Caution, Resource.FanCurvePage_Status_InactiveFanFullSpeed),
                FanCurveState.Error => (ControlAppearance.Danger, Resource.FanCurvePage_Status_Error),
                _ => (ControlAppearance.Secondary, Resource.FanCurvePage_Status_Disabled)
            };
        }

        double? liveSpeed = status.State == FanCurveState.Active && status.Speed >= 0
            ? status.Speed
            : null;
        _curveControl.SetLiveIndicator(status.Temperature, liveSpeed, status.State == FanCurveState.Active);
    }

    private string FormatTemperature(int celsius)
    {
        if (_applicationSettings.Store.TemperatureUnit == TemperatureUnit.F)
        {
            var fahrenheit = celsius * 9.0 / 5.0 + 32;
            return $"{fahrenheit:0} {Resource.Fahrenheit}";
        }

        return $"{celsius} {Resource.Celsius}";
    }

    private void UpdateModeVisibility(FanCurveMode mode)
    {
        var isCurve = mode == FanCurveMode.Curve;
        _curveCard.Visibility = isCurve ? Visibility.Visible : Visibility.Collapsed;
        _hysteresisCard.Visibility = isCurve ? Visibility.Visible : Visibility.Collapsed;
        _fixedSpeedCard.Visibility = isCurve ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateFixedSpeedLabel(int speed)
    {
        var level = FanCurve.SpeedToLevel(speed);
        var cpuRpm = FanCurve.EstimateRpm(_fanTableData, FanTableType.CPU, level);
        var gpuRpm = FanCurve.EstimateRpm(_fanTableData, FanTableType.GPU, level);

        if (cpuRpm.HasValue && gpuRpm.HasValue)
            _fixedSpeedValueText.Text = $"{speed}% (Lv.{level} · ~{cpuRpm}/{gpuRpm} {Resource.RPM})";
        else if (cpuRpm.HasValue)
            _fixedSpeedValueText.Text = $"{speed}% (Lv.{level} · ~{cpuRpm} {Resource.RPM})";
        else
            _fixedSpeedValueText.Text = $"{speed}% (Lv.{level})";
    }

    private void SetPendingChanges(bool pending)
    {
        _hasPendingCurveChanges = pending;
        _unsavedChangesText.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        _revertCurveButton.IsEnabled = pending;
        _applyCurveButton.IsEnabled = pending;
    }

    private void SyncPointEditorAndPills()
    {
        RebuildPointPills();
        SyncSelectedPointBoxes();
    }

    private void RebuildPointPills()
    {
        var points = _curveControl.Points;
        var selected = _curveControl.SelectedIndex;

        if (_pointsPanel.Children.Count == points.Count)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (_pointsPanel.Children[i] is Button existing)
                {
                    var pt = points[i];
                    existing.Content = $"#{i + 1}  {pt.Temperature}°C · {pt.Speed}%";
                    existing.Appearance = i == selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
                }
            }

            return;
        }

        _pointsPanel.Children.Clear();

        for (var i = 0; i < points.Count; i++)
        {
            var index = i;
            var pt = points[i];
            var pill = new Button
            {
                Content = $"#{index + 1}  {pt.Temperature}°C · {pt.Speed}%",
                Appearance = index == selected ? ControlAppearance.Primary : ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12
            };
            pill.Click += (_, _) =>
            {
                _isTypingInPointBox = false;
                _curveControl.SelectedIndex = index;
            };
            _pointsPanel.Children.Add(pill);
        }
    }

    private void SyncSelectedPointBoxes()
    {
        _isUpdatingPointBoxes = true;
        try
        {
            var point = _curveControl.SelectedPoint;
            var hasPoint = point.HasValue;

            _pointTempBox.IsEnabled = hasPoint;
            _pointSpeedBox.IsEnabled = hasPoint;
            _addPointButton.IsEnabled = _curveControl.Points.Count < FanCurve.MAX_POINTS;
            _removePointButton.IsEnabled = hasPoint && _curveControl.Points.Count > FanCurve.MIN_POINTS;

            if (point.HasValue)
            {
                var (minTemp, maxTemp, minSpeed, maxSpeed) = _curveControl.GetSelectedPointBounds();
                _pointTempBox.Minimum = minTemp;
                _pointTempBox.Maximum = maxTemp;
                _pointSpeedBox.Minimum = minSpeed;
                _pointSpeedBox.Maximum = maxSpeed;

                if (!_isTypingInPointBox)
                {
                    _pointTempBox.Value = point.Value.Temperature;
                    _pointSpeedBox.Value = point.Value.Speed;
                }
            }
        }
        finally
        {
            _isUpdatingPointBoxes = false;
        }
    }

    private void CommitPointBoxes()
    {
        _isTypingInPointBox = false;

        if (_curveControl.SelectedPoint is not { } current)
            return;

        var temp = int.TryParse(_pointTempBox.Text, out var parsedTemp) ? parsedTemp : current.Temperature;
        var speed = int.TryParse(_pointSpeedBox.Text, out var parsedSpeed) ? parsedSpeed : current.Speed;

        _curveControl.UpdateSelectedPoint(temp, speed);
        SyncPointEditorAndPills();
    }

    private async Task SaveSettingsAsync(bool includeCurvePoints, bool restartController = false)
    {
        if (includeCurvePoints)
        {
            _settings.Store.Points = [.. _curveControl.Points];
            SetPendingChanges(false);
        }

        _settings.SynchronizeStore();

        if (restartController)
            await _controller.StartStopIfNeededAsync();
        else if (_settings.Store.Enabled)
            _controller.RequestImmediateRefresh();
    }

    private async void EnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        var enabled = _enableToggle.IsChecked ?? false;
        _settings.Store.Enabled = enabled;
        // If user edited the curve and turns on fan control, save their edited curve together.
        await SaveSettingsAsync(includeCurvePoints: enabled && _hasPendingCurveChanges, restartController: true);
    }

    private async void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || !_modeComboBox.TryGetSelectedItem(out FanCurveMode mode))
            return;

        _settings.Store.Mode = mode;
        UpdateModeVisibility(mode);
        await SaveSettingsAsync(includeCurvePoints: false);
    }

    private async void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || !_sourceComboBox.TryGetSelectedItem(out FanCurveTemperatureSource source))
            return;

        _settings.Store.Source = source;
        await SaveSettingsAsync(includeCurvePoints: false);
    }

    private async void FixedSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var speed = (int)Math.Round(e.NewValue);
        UpdateFixedSpeedLabel(speed);

        if (_isLoading)
            return;

        _settings.Store.FixedSpeed = speed;
        await SaveSettingsAsync(includeCurvePoints: false);
    }

    private void QuietPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        _curveControl.SetPoints(FanCurve.QuietPoints);
        SetPendingChanges(true);
        SyncPointEditorAndPills();
    }

    private void BalancedPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        _curveControl.SetPoints(FanCurve.DefaultPoints);
        SetPendingChanges(true);
        SyncPointEditorAndPills();
    }

    private void PerformancePresetButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        _curveControl.SetPoints(FanCurve.PerformancePoints);
        SetPendingChanges(true);
        SyncPointEditorAndPills();
    }

    private void CurveControl_CurveChanged(object? sender, EventArgs e)
    {
        if (!_isLoading)
            SetPendingChanges(true);
        SyncPointEditorAndPills();
    }

    private void CurveControl_CurveCommitted(object? sender, EventArgs e)
    {
        if (!_isLoading)
            SetPendingChanges(true);
        SyncPointEditorAndPills();
    }

    private void CurveControl_SelectedPointChanged(object? sender, EventArgs e)
    {
        _isTypingInPointBox = false;
        SyncPointEditorAndPills();
    }

    private void PointBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => _isTypingInPointBox = true;

    private void PointBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPointBoxes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _isTypingInPointBox = false;
            SyncSelectedPointBoxes();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
            _isTypingInPointBox = true;
    }

    private void PointBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isUpdatingPointBoxes)
            return;

        CommitPointBoxes();
    }

    private void PointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _isUpdatingPointBoxes || _isTypingInPointBox)
            return;

        // Spin buttons change text without typing; reflect immediately on the graph.
        if (int.TryParse(_pointTempBox.Text, out var temp) && int.TryParse(_pointSpeedBox.Text, out var speed))
            _curveControl.UpdateSelectedPoint(temp, speed);
    }

    private void AddPointButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        if (_curveControl.AddPoint())
        {
            SetPendingChanges(true);
            SyncPointEditorAndPills();
        }
    }

    private void RemovePointButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        if (_curveControl.RemoveSelectedPoint())
        {
            SetPendingChanges(true);
            SyncPointEditorAndPills();
        }
    }

    private void RevertCurveButton_Click(object sender, RoutedEventArgs e)
    {
        _isTypingInPointBox = false;
        _curveControl.SetPoints(_settings.Store.Points);
        SetPendingChanges(false);
        SyncPointEditorAndPills();
    }

    private async void ApplyCurveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTypingInPointBox)
            CommitPointBoxes();

        await SaveSettingsAsync(includeCurvePoints: true);
        await SnackbarHelper.ShowAsync(Resource.FanCurvePage_Applied_Title);
    }

    private void ParameterBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => _isTypingInParameterBox = true;

    private async void ParameterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await CommitParameterBoxesAsync();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
            _isTypingInParameterBox = true;
    }

    private async void ParameterBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        await CommitParameterBoxesAsync();
    }

    private async void ParameterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _isTypingInParameterBox)
            return;

        await CommitParameterBoxesAsync();
    }

    private async Task CommitParameterBoxesAsync()
    {
        _isTypingInParameterBox = false;
        var changed = false;

        if (int.TryParse(_hysteresisBox.Text, out var hysteresis))
        {
            hysteresis = Math.Clamp(hysteresis, 0, 20);
            if (_settings.Store.Hysteresis != hysteresis)
            {
                _settings.Store.Hysteresis = hysteresis;
                _curveControl.Hysteresis = hysteresis;
                changed = true;
            }
        }

        if (int.TryParse(_intervalBox.Text, out var interval))
        {
            interval = Math.Clamp(interval, 1, 30);
            if (_settings.Store.Interval != interval)
            {
                _settings.Store.Interval = interval;
                changed = true;
            }
        }

        if (int.TryParse(_criticalTempBox.Text, out var critical))
        {
            critical = Math.Clamp(critical, 70, 105);
            if (_settings.Store.CriticalTemperature != critical)
            {
                _settings.Store.CriticalTemperature = critical;
                _curveControl.CriticalTemperature = critical;
                changed = true;
            }
        }

        if (changed)
            await SaveSettingsAsync(includeCurvePoints: false);
    }

    private async void SwitchToCustomModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _powerModeFeature.SetStateAsync(PowerModeState.GodMode);
            _controller.RequestImmediateRefresh();
        }
        catch (PowerModeUnavailableWithoutACException ex)
        {
            await SnackbarHelper.ShowAsync(
                Resource.PowerModeUnavailableWithoutACException_Title,
                string.Format(Resource.PowerModeUnavailableWithoutACException_Message, ex.PowerMode.GetDisplayName()),
                SnackbarType.Warning);
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to switch to Custom power mode.", ex);

            await SnackbarHelper.ShowAsync(Resource.GodModeSettingsWindow_Error_Apply_Title, ex.Message, SnackbarType.Error);
        }
    }

    private static string GetModeDisplayName(FanCurveMode mode) => mode switch
    {
        FanCurveMode.Fixed => Resource.FanCurvePage_Mode_Fixed,
        _ => Resource.FanCurvePage_Mode_Curve
    };

    private static string GetSourceDisplayName(FanCurveTemperatureSource source) => source switch
    {
        FanCurveTemperatureSource.CPU => Resource.FanCurvePage_Source_CPU,
        FanCurveTemperatureSource.GPU => Resource.FanCurvePage_Source_GPU,
        _ => Resource.FanCurvePage_Source_Max
    };
}
