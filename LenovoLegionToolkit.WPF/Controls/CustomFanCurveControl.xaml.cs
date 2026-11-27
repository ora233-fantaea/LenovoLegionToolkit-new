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
    private const double HANDLE_RADIUS = 6.5;
    private const double SELECTED_HANDLE_RADIUS = 8;
    private const double HIT_TEST_RADIUS = 18;
    private const double CURVE_HIT_DISTANCE = 9;
    private const int SNAP_TEMP = 5;
    private const int SNAP_SPEED = 5;

    private readonly List<FanCurvePoint> _points = [.. FanCurve.DefaultPoints];

    private FanTableData[]? _tableData;
    private int _selectedIndex = -1;
    private int _draggingIndex = -1;
    private int _hoveredIndex = -1;
    private bool _hoveredCurve;
    private bool _draggingWholeCurve;
    private bool _dragModified;
    private int _wholeCurveDeltaSpeed;
    private Point _dragStartCanvasPos;
    private List<FanCurvePoint>? _dragStartPoints;
    private int _inlineEditingIndex = -1;
    private Point? _cursorCanvasPos;
    private int _criticalTemperature = 95;
    private int _hysteresis = 3;
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
            HideInlineEditor();
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

    public int Hysteresis
    {
        get => _hysteresis;
        set
        {
            var clamped = Math.Clamp(value, 0, 20);
            if (_hysteresis == clamped)
                return;

            _hysteresis = clamped;
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
        HideInlineEditor();

        _points.Clear();
        _points.AddRange(FanCurve.Normalize(points));

        _selectedIndex = selectedIndex >= 0 && selectedIndex < _points.Count
            ? selectedIndex
            : (_points.Count > 0 ? 0 : -1);
        _draggingIndex = -1;
        _draggingWholeCurve = false;
        _dragStartPoints = null;
        _hoveredIndex = -1;
        _hoveredCurve = false;

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

        return GetPointPushBounds(_selectedIndex);
    }

    public void UpdateSelectedPoint(int temperature, int speed)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return;

        if (!ApplyPointEditWithPush(_points, _selectedIndex, temperature, speed))
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

        HideInlineEditor();
        _points.RemoveAt(_selectedIndex);
        _selectedIndex = Math.Clamp(_selectedIndex, 0, _points.Count - 1);
        _hoveredIndex = -1;
        _draggingIndex = -1;
        _draggingWholeCurve = false;
        _dragStartPoints = null;

        Redraw();
        SelectedPointChanged?.Invoke(this, EventArgs.Empty);
        CurveChanged?.Invoke(this, EventArgs.Empty);
        CurveCommitted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private (int minTemp, int maxTemp, int minSpeed, int maxSpeed) GetPointPushBounds(int index)
    {
        var minTemp = FanCurve.MIN_TEMPERATURE + index;
        var maxTemp = FanCurve.MAX_TEMPERATURE - (_points.Count - 1 - index);
        return (minTemp, maxTemp, 0, 100);
    }

    private bool AddPointAt(int temperature, int speed)
    {
        if (_points.Count >= FanCurve.MAX_POINTS)
            return false;

        HideInlineEditor();
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
    /// FanControl-style monotonic push: moving a point smoothly pushes neighboring points
    /// instead of getting blocked against them, preserving strict temperature order and non-decreasing speed.
    /// </summary>
    private static bool ApplyPointEditWithPush(List<FanCurvePoint> target, int index, int temperature, int speed)
    {
        var minTemp = FanCurve.MIN_TEMPERATURE + index;
        var maxTemp = FanCurve.MAX_TEMPERATURE - (target.Count - 1 - index);

        var clampedTemp = Math.Clamp(temperature, minTemp, maxTemp);
        var clampedSpeed = Math.Clamp(speed, 0, 100);

        var original = new List<FanCurvePoint>(target);
        target[index] = new FanCurvePoint(clampedTemp, clampedSpeed);

        // Push left neighbors down / left if needed
        for (var i = index - 1; i >= 0; i--)
        {
            var next = target[i + 1];
            var cur = target[i];
            var newT = cur.Temperature >= next.Temperature ? Math.Max(FanCurve.MIN_TEMPERATURE, next.Temperature - 1) : cur.Temperature;
            var newS = cur.Speed > next.Speed ? next.Speed : cur.Speed;
            target[i] = new FanCurvePoint(newT, newS);
        }

        // Push right neighbors up / right if needed
        for (var i = index + 1; i < target.Count; i++)
        {
            var prev = target[i - 1];
            var cur = target[i];
            var newT = cur.Temperature <= prev.Temperature ? Math.Min(FanCurve.MAX_TEMPERATURE, prev.Temperature + 1) : cur.Temperature;
            var newS = cur.Speed < prev.Speed ? prev.Speed : cur.Speed;
            target[i] = new FanCurvePoint(newT, newS);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (target[i] != original[i])
                return true;
        }

        return false;
    }

    private static int Snap(int value, int step) => (int)Math.Round(value / (double)step) * step;

    private static bool IsGridSnapHeld() => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        HideInlineEditor();
        Redraw();
    }

    private void ShowInlineEditor(int index)
    {
        if (index < 0 || index >= _points.Count)
            return;

        _inlineEditingIndex = index;
        var p = _points[index];
        _inlineTempBox.Text = p.Temperature.ToString();
        _inlineSpeedBox.Text = p.Speed.ToString();
        _inlineEditor.Visibility = Visibility.Visible;

        _inlineEditor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var editorWidth = _inlineEditor.DesiredSize.Width;
        var editorHeight = _inlineEditor.DesiredSize.Height;

        var anchor = PointToCanvas(p.Temperature, p.Speed);
        var left = Math.Clamp(anchor.X - editorWidth / 2, 6, Math.Max(6, _canvas.ActualWidth - editorWidth - 6));
        var top = anchor.Y - editorHeight - 16;
        if (top < 6)
            top = Math.Min(Math.Max(6, _canvas.ActualHeight - editorHeight - 6), anchor.Y + 16);

        Canvas.SetLeft(_inlineEditor, left);
        Canvas.SetTop(_inlineEditor, top);

        _inlineSpeedBox.Focus();
        _inlineSpeedBox.SelectAll();
    }

    private void HideInlineEditor()
    {
        _inlineEditingIndex = -1;
        if (_inlineEditor is not null)
            _inlineEditor.Visibility = Visibility.Collapsed;
    }

    private void CommitInlineEditor()
    {
        if (_inlineEditingIndex < 0 || _inlineEditingIndex >= _points.Count)
        {
            HideInlineEditor();
            return;
        }

        var index = _inlineEditingIndex;
        var current = _points[index];
        var temp = int.TryParse(_inlineTempBox.Text.Trim(), out var parsedTemp) ? parsedTemp : current.Temperature;
        var speed = int.TryParse(_inlineSpeedBox.Text.Trim(), out var parsedSpeed) ? parsedSpeed : current.Speed;

        HideInlineEditor();
        _selectedIndex = index;

        if (ApplyPointEditWithPush(_points, index, temp, speed))
        {
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
            CurveChanged?.Invoke(this, EventArgs.Empty);
            CurveCommitted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Redraw();
        }
    }

    private void InlineEditorApply_Click(object sender, RoutedEventArgs e) => CommitInlineEditor();

    private void InlineEditor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void UserControl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void InlineEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitInlineEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideInlineEditor();
            Focus();
            e.Handled = true;
        }
    }

    private void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (_inlineEditingIndex >= 0)
            return;

        if (_selectedIndex < 0 || _selectedIndex >= _points.Count)
            return;

        if (e.Key is Key.Delete or Key.Back)
        {
            RemoveSelectedPoint();
            e.Handled = true;
            return;
        }

        var current = _points[_selectedIndex];
        var fastStep = IsGridSnapHeld() || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var stepTemp = fastStep ? SNAP_TEMP : 1;
        var stepSpeed = fastStep ? SNAP_SPEED : 1;

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

    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_inlineEditingIndex >= 0 || _draggingIndex >= 0 || _draggingWholeCurve)
            return;

        var pos = e.GetPosition(_canvas);
        var hitIndex = HitTestPoint(pos);
        if (hitIndex < 0)
            return;

        e.Handled = true;
        Focus();

        _selectedIndex = hitIndex;
        var step = IsGridSnapHeld() ? SNAP_SPEED : 1;
        var delta = e.Delta > 0 ? step : -step;
        var cur = _points[hitIndex];

        if (ApplyPointEditWithPush(_points, hitIndex, cur.Temperature, cur.Speed + delta))
        {
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
            CurveChanged?.Invoke(this, EventArgs.Empty);
            CurveCommitted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        Keyboard.Focus(this);
        HideInlineEditor();

        var pos = e.GetPosition(_canvas);
        var hitIndex = HitTestPoint(pos);

        if (hitIndex >= 0)
        {
            _selectedIndex = hitIndex;
            _hoveredIndex = hitIndex;

            // Double-click on a point opens inline numeric editor (FanControl style)
            if (e.ClickCount == 2)
            {
                _draggingIndex = -1;
                _draggingWholeCurve = false;
                _dragModified = false;
                _dragStartPoints = null;
                if (_canvas.IsMouseCaptured)
                    _canvas.ReleaseMouseCapture();

                Redraw();
                SelectedPointChanged?.Invoke(this, EventArgs.Empty);
                ShowInlineEditor(hitIndex);
                return;
            }

            _draggingIndex = hitIndex;
            _draggingWholeCurve = false;
            _dragModified = false;
            _dragStartPoints = [.. _points];
            _canvas.Cursor = Cursors.SizeAll;
            _canvas.CaptureMouse();
            Redraw();
            SelectedPointChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Double-click on empty plot area adds a new point at 1°C/1% precision (Ctrl snaps to 5)
        if (e.ClickCount == 2 && _points.Count < FanCurve.MAX_POINTS)
        {
            var (rawTemp, rawSpeed) = CanvasToPoint(pos);
            var temp = IsGridSnapHeld() ? Snap(rawTemp, SNAP_TEMP) : rawTemp;
            var speed = IsGridSnapHeld() ? Snap(rawSpeed, SNAP_SPEED) : rawSpeed;
            AddPointAt(temp, speed);
            return;
        }

        // Single-click drag on curve line shifts the entire curve vertically
        if (e.ClickCount == 1 && DistanceToCurve(pos) <= CURVE_HIT_DISTANCE)
        {
            _draggingIndex = -1;
            _draggingWholeCurve = true;
            _dragModified = false;
            _wholeCurveDeltaSpeed = 0;
            _dragStartCanvasPos = pos;
            _dragStartPoints = [.. _points];
            _canvas.Cursor = Cursors.SizeNS;
            _canvas.CaptureMouse();
            Redraw();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        _cursorCanvasPos = pos;

        if (_draggingWholeCurve && _dragStartPoints is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            var plotHeight = Math.Max(1, _canvas.ActualHeight - 2 * PADDING_Y);
            var rawDelta = (int)Math.Round((_dragStartCanvasPos.Y - pos.Y) / plotHeight * 100.0);
            var deltaSpeed = IsGridSnapHeld() ? Snap(rawDelta, SNAP_SPEED) : rawDelta;
            _wholeCurveDeltaSpeed = deltaSpeed;

            var changed = false;
            for (var i = 0; i < _points.Count && i < _dragStartPoints.Count; i++)
            {
                var updated = _dragStartPoints[i] with { Speed = Math.Clamp(_dragStartPoints[i].Speed + deltaSpeed, 0, 100) };
                if (_points[i] != updated)
                {
                    _points[i] = updated;
                    changed = true;
                }
            }

            if (changed)
            {
                _dragModified = true;
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }

            Redraw();
            return;
        }

        if (_draggingIndex >= 0 && _dragStartPoints is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            var (rawTemp, rawSpeed) = CanvasToPoint(pos);
            var temp = IsGridSnapHeld() ? Snap(rawTemp, SNAP_TEMP) : rawTemp;
            var speed = IsGridSnapHeld() ? Snap(rawSpeed, SNAP_SPEED) : rawSpeed;

            // Evaluate monotonic push from drag-start snapshot so pulling back restores bumped neighbors
            var working = new List<FanCurvePoint>(_dragStartPoints);
            ApplyPointEditWithPush(working, _draggingIndex, temp, speed);

            var changed = false;
            for (var i = 0; i < _points.Count; i++)
            {
                if (_points[i] != working[i])
                {
                    _points[i] = working[i];
                    changed = true;
                }
            }

            if (changed)
            {
                _dragModified = true;
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }

            Redraw();
            return;
        }

        var hitIndex = HitTestPoint(pos);
        var hitCurve = hitIndex < 0 && DistanceToCurve(pos) <= CURVE_HIT_DISTANCE;
        _hoveredIndex = hitIndex;
        _hoveredCurve = hitCurve;

        if (hitIndex >= 0)
            _canvas.Cursor = Cursors.Hand;
        else if (hitCurve)
            _canvas.Cursor = Cursors.SizeNS;
        else
            _canvas.Cursor = Cursors.Arrow;

        Redraw();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        FinishDrag(releaseCapture: true);
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e) => FinishDrag(releaseCapture: false);

    private void FinishDrag(bool releaseCapture)
    {
        if (_draggingIndex < 0 && !_draggingWholeCurve)
            return;

        var wasModified = _dragModified;
        _draggingIndex = -1;
        _draggingWholeCurve = false;
        _dragModified = false;
        _wholeCurveDeltaSpeed = 0;
        _dragStartPoints = null;

        if (releaseCapture && _canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        _canvas.Cursor = _hoveredIndex >= 0 ? Cursors.Hand : (_hoveredCurve ? Cursors.SizeNS : Cursors.Arrow);
        Redraw();

        if (wasModified)
            CurveCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideInlineEditor();
        var pos = e.GetPosition(_canvas);
        var hitIndex = HitTestPoint(pos);
        if (hitIndex < 0 || _points.Count <= FanCurve.MIN_POINTS)
            return;

        _selectedIndex = hitIndex;
        RemoveSelectedPoint();
    }

    private void Canvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_draggingIndex >= 0 || _draggingWholeCurve)
            return;

        _cursorCanvasPos = null;
        _hoveredIndex = -1;
        _hoveredCurve = false;
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

    private double DistanceToCurve(Point pos)
    {
        if (_points.Count == 0)
            return double.PositiveInfinity;

        var polyline = BuildMainCurveCanvasPoints();
        var minDist = double.PositiveInfinity;

        for (var i = 0; i < polyline.Count - 1; i++)
        {
            var a = polyline[i];
            var b = polyline[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lenSq = dx * dx + dy * dy;

            var t = lenSq > 0 ? Math.Clamp(((pos.X - a.X) * dx + (pos.Y - a.Y) * dy) / lenSq, 0, 1) : 0;
            var projX = a.X + t * dx;
            var projY = a.Y + t * dy;
            var px = pos.X - projX;
            var py = pos.Y - projY;
            var dist = Math.Sqrt(px * px + py * py);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }

    private List<Point> BuildMainCurveCanvasPoints()
    {
        var list = new List<Point>(_points.Count + 2)
        {
            PointToCanvas(FanCurve.MIN_TEMPERATURE, _points[0].Speed)
        };
        foreach (var pt in _points)
            list.Add(PointToCanvas(pt.Temperature, pt.Speed));
        list.Add(PointToCanvas(FanCurve.MAX_TEMPERATURE, _points[^1].Speed));
        return list;
    }

    private List<Point> BuildCoolingCurveCanvasPoints()
    {
        var list = new List<Point>(_points.Count + 2)
        {
            PointToCanvas(FanCurve.MIN_TEMPERATURE, FanCurve.GetSpeed(_points, FanCurve.MIN_TEMPERATURE + _hysteresis))
        };

        foreach (var pt in _points)
        {
            var shiftedTemp = pt.Temperature - _hysteresis;
            if (shiftedTemp is > FanCurve.MIN_TEMPERATURE and < FanCurve.MAX_TEMPERATURE)
                list.Add(PointToCanvas(shiftedTemp, pt.Speed));
        }

        list.Add(PointToCanvas(FanCurve.MAX_TEMPERATURE, FanCurve.GetSpeed(_points, FanCurve.MAX_TEMPERATURE + _hysteresis)));
        return list;
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
        var accentColor = accentBrush is SolidColorBrush scb ? scb.Color : Colors.DeepSkyBlue;

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

        // 2. Build main heating curve polyline
        var curvePoints = BuildMainCurveCanvasPoints();

        // 3. Area fill under curve
        var bottomLeft = PointToCanvas(FanCurve.MIN_TEMPERATURE, 0);
        var bottomRight = PointToCanvas(FanCurve.MAX_TEMPERATURE, 0);
        var fillPoints = new PointCollection { bottomLeft };
        foreach (var cp in curvePoints)
            fillPoints.Add(cp);
        fillPoints.Add(bottomRight);

        _canvas.Children.Add(new Polygon
        {
            Points = fillPoints,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B),
                Color.FromArgb(6, accentColor.R, accentColor.G, accentColor.B),
                90)
        });

        // 4. FanControl-style hysteresis band & dashed cooling-down curve
        if (_hysteresis > 0 && _points.Count >= 2)
        {
            var coolingPoints = BuildCoolingCurveCanvasPoints();

            var bandPoints = new PointCollection();
            foreach (var cp in curvePoints)
                bandPoints.Add(cp);
            for (var i = coolingPoints.Count - 1; i >= 0; i--)
                bandPoints.Add(coolingPoints[i]);

            _canvas.Children.Add(new Polygon
            {
                Points = bandPoints,
                IsHitTestVisible = false,
                Fill = new SolidColorBrush(Color.FromArgb(38, accentColor.R, accentColor.G, accentColor.B))
            });

            var coolingFigure = new PathFigure { StartPoint = coolingPoints[0] };
            foreach (var cp in coolingPoints.Skip(1))
                coolingFigure.Segments.Add(new LineSegment(cp, true));

            _canvas.Children.Add(new Path
            {
                Stroke = accentBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 4],
                Opacity = 0.55,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
                Data = new PathGeometry { Figures = [coolingFigure] }
            });
        }

        // 5. Subtle drag guide crosshair (only while dragging a point so idle canvas stays clean)
        if (_draggingIndex >= 0 && _draggingIndex < _points.Count)
        {
            var dp = _points[_draggingIndex];
            DrawCrosshair(PointToCanvas(dp.Temperature, dp.Speed), accentBrush, 0.55);
        }

        // 6. Main curve stroke (thickens subtly when hovering/dragging the whole curve)
        var figure = new PathFigure { StartPoint = curvePoints[0] };
        foreach (var cp in curvePoints.Skip(1))
            figure.Segments.Add(new LineSegment(cp, true));

        _canvas.Children.Add(new Path
        {
            Stroke = accentBrush,
            StrokeThickness = _draggingWholeCurve || (_hoveredCurve && _hoveredIndex < 0) ? 3.5 : 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Data = new PathGeometry { Figures = [figure] }
        });

        // 7. Critical temperature threshold vertical line
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

        // 8. Live temperature & active target dot
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

        // 9. Control point handles
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

        // 10. Floating callout (whole-curve drag / dragged or hovered point / subtle cursor readout)
        if (_draggingWholeCurve && _cursorCanvasPos is { } wholePos)
        {
            var sign = _wholeCurveDeltaSpeed > 0 ? "+" : string.Empty;
            var badge = new Border
            {
                Background = surfaceBrush,
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{sign}{_wholeCurveDeltaSpeed}%",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primaryTextBrush
                }
            };
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = Math.Clamp(wholePos.X - badge.DesiredSize.Width / 2, 6, Math.Max(6, _canvas.ActualWidth - badge.DesiredSize.Width - 6));
            var top = Math.Clamp(wholePos.Y - badge.DesiredSize.Height - 14, 6, Math.Max(6, _canvas.ActualHeight - badge.DesiredSize.Height - 6));
            Canvas.SetLeft(badge, left);
            Canvas.SetTop(badge, top);
            _canvas.Children.Add(badge);
            return;
        }

        if (_inlineEditingIndex >= 0)
            return;

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
            var temp = IsGridSnapHeld() ? Snap(rawTemp, SNAP_TEMP) : rawTemp;
            var speed = IsGridSnapHeld() ? Snap(rawSpeed, SNAP_SPEED) : rawSpeed;
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
