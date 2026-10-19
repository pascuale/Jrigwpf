using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jrig.Controls;

/// <summary>
/// Rotary knob, value 0..1. Drag up/down to turn (hold Shift for fine control),
/// mouse wheel to nudge, double-click to reset to the default.
/// Drawn directly, so it needs no template.
/// </summary>
public sealed class KnobControl : Control
{
    private const double StartAngle = 135;   // degrees, measured clockwise from "3 o'clock"
    private const double SweepAngle = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(KnobControl),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceValue));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(KnobControl),
        new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(KnobControl),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen TrackPen = MakePen(Color.FromRgb(0x3A, 0x3A, 0x44), 3.5);
    private static readonly Pen BodyPen = MakePen(Color.FromRgb(0x14, 0x14, 0x18), 1.5);
    private static readonly Brush BodyBrush = MakeBodyBrush();

    private bool _dragging;
    private double _lastY;

    public KnobControl()
    {
        Cursor = Cursors.SizeNS;
        Focusable = false;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static object CoerceValue(DependencyObject d, object baseValue) =>
        Math.Clamp((double)baseValue, 0.0, 1.0);

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
        }
        else
        {
            _dragging = true;
            _lastY = e.GetPosition(this).Y;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;

        double y = e.GetPosition(this).Y;
        double delta = _lastY - y;          // dragging up increases the value
        _lastY = y;

        bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        Value += delta * (fine ? 0.0015 : 0.008);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += Math.Sign(e.Delta) * 0.02;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;

        // Invisible hit area so the whole square responds to the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double size = Math.Min(w, h);
        if (size < 12) return;

        var center = new Point(w / 2, h / 2);
        double radius = size / 2 - 4;
        double v = Value;

        // Track (full range) and the lit portion up to the current value.
        dc.DrawGeometry(null, TrackPen, Arc(center, radius, StartAngle, StartAngle + SweepAngle));
        if (v > 0.002)
        {
            var litPen = new Pen(Accent, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, litPen, Arc(center, radius, StartAngle, StartAngle + SweepAngle * v));
        }

        // Knob body.
        double bodyRadius = radius - 7;
        dc.DrawEllipse(BodyBrush, BodyPen, center, bodyRadius, bodyRadius);

        // Pointer line.
        double angle = (StartAngle + SweepAngle * v) * Math.PI / 180.0;
        var inner = new Point(center.X + Math.Cos(angle) * bodyRadius * 0.35,
                              center.Y + Math.Sin(angle) * bodyRadius * 0.35);
        var outer = new Point(center.X + Math.Cos(angle) * (bodyRadius - 3),
                              center.Y + Math.Sin(angle) * (bodyRadius - 3));
        var pointerPen = new Pen(Brushes.White, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pointerPen, inner, outer);
    }

    private static Geometry Arc(Point center, double radius, double startDeg, double endDeg)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(center, radius, startDeg), false, false);
            ctx.ArcTo(
                PointOnCircle(center, radius, endDeg),
                new Size(radius, radius),
                0,
                endDeg - startDeg > 180,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(center.X + Math.Cos(rad) * radius, center.Y + Math.Sin(rad) * radius);
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush MakeBodyBrush()
    {
        var brush = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5E, 0x5E, 0x6B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x24, 0x2B), 1));
        brush.Freeze();
        return brush;
    }
}
