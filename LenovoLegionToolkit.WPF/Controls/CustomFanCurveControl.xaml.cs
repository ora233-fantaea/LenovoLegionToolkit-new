using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;

namespace LenovoLegionToolkit.WPF.Controls;

public partial class CustomFanCurveControl
{
    private const double PADDING_X = 16;
    private const double PADDING_Y = 16;
    private const double HANDLE_RADIUS = 7;
    private const double SELECTED_HANDLE_RADIUS = 8.5;
    private const double HIT_TEST_RADIUS = 22;
    private const int SNAP_TEMP = 5;
    private const int SNAP_SPEED = 5;

    private readonly List<FanCurvePoint> _points = [.. FanCurve.DefaultPoints];

    private FanTableData[]? _tableData;
    private int _selectedIndex = -1;
    private int _draggingIndex = -1;
    private int _hoveredIndex = -1;
    private Point? _cursorCanvasPos;
    private int _criticalTemperature = 95;
    private int _liveTemperature = -1;
    private double? _liveSpeed;
    private bool _isLiveActive;

    public IReadOnlyList<FanCurvePoint> Points => _points;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = value >= 0 && value < _points.Count ? value : -1;
            if (_selectedIndex == clamped)
                return;

            _selectedIndex = clamped;
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public FanCurvePoint? SelectedPoint => _selectedIndex >= 0 && _selectedIndex < _points.Count ? _points[_selectedIndex] : null;

    public int CriticalTemperature
    {
        get => _criticalTemperature;
        set
        {
            var clamped = Math.Clamp(value, 70, 105);
            if (_criticalTemperature == clamped)
                return;

            _criticalTemperature = clamped;
            Redraw();
        }
    }

    public event EventHandler? CurveChanged;
    public event EventHandler? CurveCommitted;
    public event EventHandler? SelectedPointChanged;

    public CustomFanCurveControl()
    {
        InitializeComponent();

        Loaded += (_, _) => Redraw();
        if (IoCContainer.TryResolve<ThemeManager>() is { } themeManager)
            themeManager.ThemeApplied += (_, _) => Dispatcher.InvokeAsync(Redraw);
    }

    public void SetPoints(IEnumerable<FanCurvePoint> points, int selectedIndex = -1)
    {
        _points.Clear();
        _points.AddRange(FanCurve.Normalize(points));

        _selectedIndex = selectedIndex >= 0 && selectedIndex < _points.Count
            ? selectedIndex
            : (_points.Count > 0 ? 0 : -1);
        _draggingIndex = -1;
        _hoveredIndex = -1;

        Redraw();
        SelectedPointChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetFanTableData(FanTableData[]? tableData)
    {
        _tableData = tableData;
        Redraw();
    }

    public void SetLiveIndicator(int temperature, double? speed, bool isActive)
    {
        _liveTemperature = temperature;
        _liveSpeed = speed;
        _isLiveActive = isActive;
        Redraw();
    }

    public (int minTemp, int maxTemp, int minSpeed, int maxSpeed) GetSelectedPointBounds()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return (FanCurve.MIN_TEMPERATURE, FanCurve.MAX_TEMPERATURE, 0, 100);

        return GetPointBounds(_selectedIndex);
    }

    public void UpdateSelectedPoint(int temperature, int speed)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return;

        if (!ApplyPointEditNonDestructive(_selectedIndex, temperature, speed))
            return;

        Redraw();
        CurveChanged?.Invoke(this, EventArgs.Empty);
        CurveCommitted?.Invoke(this, EventArgs.Empty);
    }

    public bool AddPoint()
    {
        if (_points.Count >= FanCurve.MAX_POINTS)
            return false;

        var bestGap = -1;
        var insertTemp = 65;

        for (var i = 0; i < _points.Count - 1; i++)
        {
            var gap = _points[i + 1].Temperature - _points[i].Temperature;
            if (gap > bestGap && gap >= 2)
            {
                bestGap = gap;
                insertTemp = (_points[i].Temperature + _points[i + 1].Temperature) / 2;
            }
        }

        if (bestGap < 2)
        {
            if (_points[0].Temperature - FanCurve.MIN_TEMPERATURE >= 2)
                insertTemp = (_points[0].Temperature + FanCurve.MIN_TEMPERATURE) / 2;
            else if (FanCurve.MAX_TEMPERATURE - _points[^1].Temperature >= 2)
                insertTemp = (_points[^1].Temperature + FanCurve.MAX_TEMPERATURE) / 2;
            else
                return false;
        }

        var speed = (int)Math.Round(FanCurve.GetSpeed(_points, insertTemp));
        return AddPointAt(insertTemp, speed);
    }

    public bool RemoveSelectedPoint()
    {
        if (_points.Count <= FanCurve.MIN_POINTS || _selectedIndex < 0 || _selectedIndex >= _points.Count)
            return false;

        _points.RemoveAt(_selectedIndex);
        _selectedIndex = Math.Clamp(_selectedIndex, 0, _points.Count - 1);
        _hoveredIndex = -1;
        _draggingIndex = -1;

        Redraw();
        SelectedPointChanged?.Invoke(this, EventArgs.Empty);
        CurveChanged?.Invoke(this, EventArgs.Empty);
        CurveCommitted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private (int minTemp, int maxTemp, int minSpeed, int maxSpeed) GetPointBounds(int index)
    {
        var minTemp = index > 0 ? _points[index - 1].Temperature + 1 : FanCurve.MIN_TEMPERATURE;
        var maxTemp = index < _points.Count - 1 ? _points[index + 1].Temperature - 1 : FanCurve.MAX_TEMPERATURE;
        var minSpeed = index > 0 ? _points[index - 1].Speed : 0;
        var maxSpeed = index < _points.Count - 1 ? _points[index + 1].Speed : 100;
        return (minTemp, maxTemp, minSpeed, maxSpeed);
    }

    private bool AddPointAt(int temperature, int speed)
    {
        if (_points.Count >= FanCurve.MAX_POINTS)
            return false;

        temperature = Math.Clamp(temperature, FanCurve.MIN_TEMPERATURE, FanCurve.MAX_TEMPERATURE);
        speed = Math.Clamp(speed, 0, 100);

        if (_points.Any(p => p.Temperature == temperature))
            return false;

        var insertIndex = _points.FindIndex(p => p.Temperature > temperature);
        if (insertIndex < 0)
            insertIndex = _points.Count;

        var minSpeed = insertIndex > 0 ? _points[insertIndex - 1].Speed : 0;
        var maxSpeed = insertIndex < _points.Count ? _points[insertIndex].Speed : 100;
        speed = Math.Clamp(speed, minSpeed, maxSpeed);

        _points.Insert(insertIndex, new FanCurvePoint(temperature, speed));
        _selectedIndex = insertIndex;

        Redraw();
        SelectedPointChanged?.Invoke(this, EventArgs.Empty);
        CurveChanged?.Invoke(this, EventArgs.Empty);
        CurveCommitted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Clamps the edited point strictly between its neighbors' temperature and speed bounds
    /// so dragging one point never pushes or ruins neighboring points.
    /// </summary>
    private bool ApplyPointEditNonDestructive(int index, int temperature, int speed)
    {
        var (minTemp, maxTemp, minSpeed, maxSpeed) = GetPointBounds(index);

        var clampedTemp = Math.Clamp(temperature, minTemp, maxTemp);
        var clampedSpeed = Math.Clamp(speed, minSpeed, maxSpeed);
        var updated = new FanCurvePoint(clampedTemp, clampedSpeed);

        if (_points[index] == updated)
            return false;

        _points[index] = updated;
        return true;
    }

    private static int Snap(int value, int step) => (int)Math.Round(value / (double)step) * step;

    private static bool IsFineAdjustHeld() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return;

        if (e.Key is Key.Delete or Key.Back)
        {
            RemoveSelectedPoint();
            e.Handled = true;
            return;
        }

        var current = _points[_selectedIndex];
        var stepTemp = IsFineAdjustHeld() ? 1 : SNAP_TEMP;
        var stepSpeed = IsFineAdjustHeld() ? 1 : SNAP_SPEED;

        switch (e.Key)
        {
            case Key.Left:
                UpdateSelectedPoint(current.Temperature - stepTemp, current.Speed);
                e.Handled = true;
                break;
            case Key.Right:
                UpdateSelectedPoint(current.Temperature + stepTemp, current.Speed);
                e.Handled = true;
                break;
            case Key.Up:
                UpdateSelectedPoint(current.Temperature, current.Speed + stepSpeed);
                e.Handled = true;
                break;
            case Key.Down:
                UpdateSelectedPoint(current.Temperature, current.Speed - stepSpeed);
                e.Handled = true;
                break;
        }
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        var pos = e.GetPosition(_canvas);
        var hitIndex = HitTestPoint(pos);

        if (hitIndex >= 0)
        {
            _selectedIndex = hitIndex;
            _draggingIndex = hitIndex;
            _hoveredIndex = hitIndex;
            _canvas.Cursor = Cursors.SizeAll;
            _canvas.CaptureMouse();
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Only double-click on empty plot area adds a new point — single click never spawns accidental points.
        if (e.ClickCount == 2 && _points.Count < FanCurve.MAX_POINTS)
        {
            var (rawTemp, rawSpeed) = CanvasToPoint(pos);
            var temp = IsFineAdjustHeld() ? rawTemp : Snap(rawTemp, SNAP_TEMP);
            var speed = IsFineAdjustHeld() ? rawSpeed : Snap(rawSpeed, SNAP_SPEED);
            AddPointAt(temp, speed);
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        _cursorCanvasPos = pos;

        if (_draggingIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            var (rawTemp, rawSpeed) = CanvasToPoint(pos);
            var temp = IsFineAdjustHeld() ? rawTemp : Snap(rawTemp, SNAP_TEMP);
            var speed = IsFineAdjustHeld() ? rawSpeed : Snap(rawSpeed, SNAP_SPEED);

            if (ApplyPointEditNonDestructive(_draggingIndex, temp, speed))
            {
                CurveChanged?.Invoke(this, EventArgs.Empty);
                SelectedPointChanged?.Invoke(this, EventArgs.Empty);
            }

            Redraw();
            return;
        }

        var hitIndex = HitTestPoint(pos);
        _hoveredIndex = hitIndex;
        _canvas.Cursor = hitIndex >= 0 ? Cursors.Hand : Cursors.Arrow;
        Redraw();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingIndex < 0)
            return;

        _draggingIndex = -1;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        _canvas.Cursor = _hoveredIndex >= 0 ? Cursors.Hand : Cursors.Arrow;
        Redraw();
        CurveCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        var hitIndex = HitTestPoint(pos);
        if (hitIndex < 0 || _points.Count <= FanCurve.MIN_POINTS)
            return;

        _selectedIndex = hitIndex;
        RemoveSelectedPoint();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_draggingIndex >= 0)
            return;

        _cursorCanvasPos = null;
        _hoveredIndex = -1;
        _canvas.Cursor = Cursors.Arrow;
        Redraw();
    }

    private int HitTestPoint(Point pos)
    {
        var bestIndex = -1;
        var bestDistSq = HIT_TEST_RADIUS * HIT_TEST_RADIUS;

        for (var i = 0; i < _points.Count; i++)
        {
            var pt = PointToCanvas(_points[i].Temperature, _points[i].Speed);
            var dx = pt.X - pos.X;
            var dy = pt.Y - pos.Y;
            var distSq = dx * dx + dy * dy;

            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private Point PointToCanvas(double temperature, double speed)
    {
        var width = Math.Max(1, _canvas.ActualWidth - 2 * PADDING_X);
        var height = Math.Max(1, _canvas.ActualHeight - 2 * PADDING_Y);

        var xRatio = (temperature - FanCurve.MIN_TEMPERATURE) / (FanCurve.MAX_TEMPERATURE - FanCurve.MIN_TEMPERATURE);
        var yRatio = speed / 100.0;

        var x = PADDING_X + Math.Clamp(xRatio, 0, 1) * width;
        var y = PADDING_Y + (1.0 - Math.Clamp(yRatio, 0, 1)) * height;
        return new Point(x, y);
    }

    private (int temperature, int speed) CanvasToPoint(Point pos)
    {
        var width = Math.Max(1, _canvas.ActualWidth - 2 * PADDING_X);
        var height = Math.Max(1, _canvas.ActualHeight - 2 * PADDING_Y);

        var xRatio = Math.Clamp((pos.X - PADDING_X) / width, 0, 1);
        var yRatio = Math.Clamp(1.0 - (pos.Y - PADDING_Y) / height, 0, 1);

        var temp = (int)Math.Round(FanCurve.MIN_TEMPERATURE + xRatio * (FanCurve.MAX_TEMPERATURE - FanCurve.MIN_TEMPERATURE));
        var speed = (int)Math.Round(yRatio * 100);
        return (temp, speed);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();

        if (_canvas.ActualWidth <= 2 * PADDING_X || _canvas.ActualHeight <= 2 * PADDING_Y || _points.Count == 0)
            return;

        var accentBrush = GetBrush("PaletteLightBlueBrush", Colors.DeepSkyBlue);
        var gridBrush = GetBrush("TextFillColorDisabledBrush", Colors.Gray);
        var criticalBrush = GetBrush("SystemFillColorCriticalBrush", Colors.OrangeRed);
        var liveBrush = GetBrush("PaletteOrangeBrush", Colors.Orange);
        var primaryTextBrush = GetBrush("TextFillColorPrimaryBrush", Colors.White);
        var secondaryTextBrush = GetBrush("TextFillColorSecondaryBrush", Colors.LightGray);
        var surfaceBrush = GetBrush("SolidBackgroundFillColorBaseBrush", Color.FromRgb(32, 32, 32));
        var borderBrush = GetBrush("ControlElevationBorderBrush", Colors.Gray);

        // 1. Grid lines (10% BIOS levels horizontally, 5°C/10°C vertically)
        for (var speed = 0; speed <= 100; speed += 10)
        {
            var p1 = PointToCanvas(FanCurve.MIN_TEMPERATURE, speed);
            var p2 = PointToCanvas(FanCurve.MAX_TEMPERATURE, speed);
            _canvas.Children.Add(new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = speed % 20 == 0 ? 0.28 : 0.11,
                IsHitTestVisible = false
            });
        }

        for (var temp = FanCurve.MIN_TEMPERATURE; temp <= FanCurve.MAX_TEMPERATURE; temp += 5)
        {
            var p1 = PointToCanvas(temp, 0);
            var p2 = PointToCanvas(temp, 100);
            _canvas.Children.Add(new Line
            {
                X1 = p1.X,
                Y1 = p1.Y,
                X2 = p2.X,
                Y2 = p2.Y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = temp % 10 == 0 ? 0.25 : 0.09,
                IsHitTestVisible = false
            });
        }

        // 2. Subtle cursor / drag crosshair (Afterburner style)
        if (_draggingIndex >= 0 && _draggingIndex < _points.Count)
        {
            var dp = _points[_draggingIndex];
            DrawCrosshair(PointToCanvas(dp.Temperature, dp.Speed), accentBrush, 0.55);
        }
        else if (_cursorCanvasPos is { } cur)
        {
            var clampedX = Math.Clamp(cur.X, PADDING_X, _canvas.ActualWidth - PADDING_X);
            var clampedY = Math.Clamp(cur.Y, PADDING_Y, _canvas.ActualHeight - PADDING_Y);
            DrawCrosshair(new Point(clampedX, clampedY), gridBrush, 0.35);
        }

        // 3. Build full curve polyline including flat extensions to MIN_TEMPERATURE and MAX_TEMPERATURE
        var curvePoints = new List<Point>
        {
            PointToCanvas(FanCurve.MIN_TEMPERATURE, _points[0].Speed)
        };
        foreach (var pt in _points)
            curvePoints.Add(PointToCanvas(pt.Temperature, pt.Speed));
        curvePoints.Add(PointToCanvas(FanCurve.MAX_TEMPERATURE, _points[^1].Speed));

        // 4. Area fill under curve
        var bottomLeft = PointToCanvas(FanCurve.MIN_TEMPERATURE, 0);
        var bottomRight = PointToCanvas(FanCurve.MAX_TEMPERATURE, 0);
        var fillPoints = new PointCollection { bottomLeft };
        foreach (var cp in curvePoints)
            fillPoints.Add(cp);
        fillPoints.Add(bottomRight);

        var fillColor = accentBrush is SolidColorBrush scb ? scb.Color : Colors.DeepSkyBlue;
        _canvas.Children.Add(new Polygon
        {
            Points = fillPoints,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                Color.FromArgb(70, fillColor.R, fillColor.G, fillColor.B),
                Color.FromArgb(8, fillColor.R, fillColor.G, fillColor.B),
                90)
        });

        // 5. Curve stroke
        var figure = new PathFigure { StartPoint = curvePoints[0] };
        foreach (var cp in curvePoints.Skip(1))
            figure.Segments.Add(new LineSegment(cp, true));

        _canvas.Children.Add(new Path
        {
            Stroke = accentBrush,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Data = new PathGeometry { Figures = [figure] }
        });

        // 6. Critical temperature threshold vertical line
        if (_criticalTemperature is >= FanCurve.MIN_TEMPERATURE and <= FanCurve.MAX_TEMPERATURE)
        {
            var cTop = PointToCanvas(_criticalTemperature, 100);
            var cBottom = PointToCanvas(_criticalTemperature, 0);
            _canvas.Children.Add(new Line
            {
                X1 = cTop.X,
                Y1 = cTop.Y,
                X2 = cBottom.X,
                Y2 = cBottom.Y,
                Stroke = criticalBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
                Opacity = 0.85,
                IsHitTestVisible = false
            });

            var critLabel = new TextBlock
            {
                Text = $"{_criticalTemperature}{Resource.Celsius}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = criticalBrush,
                IsHitTestVisible = false
            };
            critLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelLeft = cTop.X + 4 + critLabel.DesiredSize.Width > _canvas.ActualWidth - 4
                ? Math.Max(4, cTop.X - critLabel.DesiredSize.Width - 4)
                : cTop.X + 4;
            Canvas.SetLeft(critLabel, labelLeft);
            Canvas.SetTop(critLabel, PADDING_Y + 2);
            _canvas.Children.Add(critLabel);
        }

        // 7. Live temperature & active target dot
        if (_liveTemperature > 0)
        {
            var clampedTemp = Math.Clamp(_liveTemperature, FanCurve.MIN_TEMPERATURE, FanCurve.MAX_TEMPERATURE);
            var targetSpeed = _liveSpeed ?? (_liveTemperature >= _criticalTemperature ? 100 : FanCurve.GetSpeed(_points, _liveTemperature));
            var top = PointToCanvas(clampedTemp, 100);
            var bottom = PointToCanvas(clampedTemp, 0);
            var dotPos = PointToCanvas(clampedTemp, targetSpeed);

            _canvas.Children.Add(new Line
            {
                X1 = top.X,
                Y1 = top.Y,
                X2 = bottom.X,
                Y2 = bottom.Y,
                Stroke = liveBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 3],
                Opacity = _isLiveActive ? 0.9 : 0.45,
                IsHitTestVisible = false
            });

            var halo = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = liveBrush,
                Opacity = _isLiveActive ? 0.28 : 0.12,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(halo, dotPos.X - 9);
            Canvas.SetTop(halo, dotPos.Y - 9);
            _canvas.Children.Add(halo);

            var liveDot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = liveBrush,
                Stroke = surfaceBrush,
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(liveDot, dotPos.X - 4.5);
            Canvas.SetTop(liveDot, dotPos.Y - 4.5);
            _canvas.Children.Add(liveDot);
        }

        // 8. Control point handles with clear hover/selected halos
        for (var i = 0; i < _points.Count; i++)
        {
            var pt = PointToCanvas(_points[i].Temperature, _points[i].Speed);
            var isSelected = i == _selectedIndex;
            var isHovered = i == _hoveredIndex || i == _draggingIndex;
            var radius = isSelected || isHovered ? SELECTED_HANDLE_RADIUS : HANDLE_RADIUS;

            if (isHovered || isSelected)
            {
                var ringRadius = radius + 6;
                var ring = new Ellipse
                {
                    Width = ringRadius * 2,
                    Height = ringRadius * 2,
                    Fill = accentBrush,
                    Opacity = isHovered ? 0.28 : 0.16,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ring, pt.X - ringRadius);
                Canvas.SetTop(ring, pt.Y - ringRadius);
                _canvas.Children.Add(ring);
            }

            var handle = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = isSelected ? accentBrush : surfaceBrush,
                Stroke = isSelected ? primaryTextBrush : accentBrush,
                StrokeThickness = isSelected ? 2 : 2.5,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(handle, pt.X - radius);
            Canvas.SetTop(handle, pt.Y - radius);
            _canvas.Children.Add(handle);
        }

        // 9. Floating callout for dragged/hovered point (or subtle coordinate readout when hovering empty plot)
        var calloutIndex = _draggingIndex >= 0 ? _draggingIndex : _hoveredIndex;
        if (calloutIndex >= 0 && calloutIndex < _points.Count)
        {
            var p = _points[calloutIndex];
            var level = FanCurve.SpeedToLevel(p.Speed);
            var header = $"#{calloutIndex + 1}  {p.Temperature}{Resource.Celsius} → {p.Speed}% (Lv.{level})";

            var cpuRpm = FanCurve.EstimateRpm(_tableData, FanTableType.CPU, level);
            var gpuRpm = FanCurve.EstimateRpm(_tableData, FanTableType.GPU, level);
            string? rpmText = null;
            if (cpuRpm.HasValue && gpuRpm.HasValue)
                rpmText = $"CPU ~{cpuRpm} {Resource.RPM} / GPU ~{gpuRpm} {Resource.RPM}";
            else if (cpuRpm.HasValue)
                rpmText = $"~{cpuRpm} {Resource.RPM}";

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryTextBrush
            });

            if (rpmText is not null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = rpmText,
                    FontSize = 11,
                    Foreground = secondaryTextBrush,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var callout = new Border
            {
                Background = surfaceBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Child = stack,
                IsHitTestVisible = false
            };

            callout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var anchor = PointToCanvas(p.Temperature, p.Speed);

            var left = anchor.X - callout.DesiredSize.Width / 2;
            left = Math.Clamp(left, 6, Math.Max(6, _canvas.ActualWidth - callout.DesiredSize.Width - 6));

            var top = anchor.Y - callout.DesiredSize.Height - 16;
            if (top < 6)
                top = anchor.Y + 16;

            Canvas.SetLeft(callout, left);
            Canvas.SetTop(callout, top);
            _canvas.Children.Add(callout);
        }
        else if (_cursorCanvasPos is { } cur)
        {
            var (rawTemp, rawSpeed) = CanvasToPoint(cur);
            var temp = IsFineAdjustHeld() ? rawTemp : Snap(rawTemp, SNAP_TEMP);
            var speed = IsFineAdjustHeld() ? rawSpeed : Snap(rawSpeed, SNAP_SPEED);
            var hint = new Border
            {
                Background = surfaceBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Opacity = 0.85,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{temp}{Resource.Celsius} · {speed}%",
                    FontSize = 11,
                    Foreground = secondaryTextBrush
                }
            };
            Canvas.SetLeft(hint, PADDING_X + 4);
            Canvas.SetTop(hint, PADDING_Y + 4);
            _canvas.Children.Add(hint);
        }
    }

    private void DrawCrosshair(Point pt, Brush brush, double opacity)
    {
        _canvas.Children.Add(new Line
        {
            X1 = PADDING_X,
            Y1 = pt.Y,
            X2 = _canvas.ActualWidth - PADDING_X,
            Y2 = pt.Y,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            Opacity = opacity,
            IsHitTestVisible = false
        });
        _canvas.Children.Add(new Line
        {
            X1 = pt.X,
            Y1 = PADDING_Y,
            X2 = pt.X,
            Y2 = _canvas.ActualHeight - PADDING_Y,
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            Opacity = opacity,
            IsHitTestVisible = false
        });
    }

    private static Brush GetBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }
}
