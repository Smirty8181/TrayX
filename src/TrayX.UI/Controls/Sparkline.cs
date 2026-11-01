using System.Windows;
using System.Windows.Media;

namespace TrayX.UI.Controls;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBaselineProperty = DependencyProperty.Register(
        nameof(ShowBaseline),
        typeof(bool),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool ShowBaseline
    {
        get => (bool)GetValue(ShowBaselineProperty);
        set => SetValue(ShowBaselineProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        var values = Values;
        var width = ActualWidth;
        var height = ActualHeight;
        if (values is null || values.Count < 2 || width <= 2 || height <= 2)
        {
            return;
        }

        var stroke = Stroke ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
        var fill = CreateFill(stroke);
        var maximum = Maximum > 0 ? Maximum : Math.Max(1, values.Max());
        var padding = 3d;
        var usable = Math.Max(1, height - padding * 2);
        var step = width / (values.Count - 1);

        var points = new Point[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = Math.Clamp(values[index], 0, maximum);
            points[index] = new Point(index * step, padding + usable - value / maximum * usable);
        }

        if (ShowBaseline)
        {
            var baselinePen = new Pen(new SolidColorBrush(Color.FromArgb(0x2A, 0x8C, 0x96, 0xB3)), 1)
            {
                DashStyle = new DashStyle(new[] { 3d, 4d }, 0)
            };
            baselinePen.Freeze();
            var y = padding + usable;
            context.DrawLine(baselinePen, new Point(0, y), new Point(width, y));
        }

        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
            foreach (var point in points)
            {
                geometry.LineTo(point, isStroked: false, isSmoothJoin: false);
            }

            geometry.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
        }

        area.Freeze();
        context.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(points[0], isFilled: false, isClosed: false);
            geometry.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: true);
        }

        line.Freeze();
        var pen = new Pen(stroke, 1.8)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, line);
    }

    private static Brush CreateFill(Brush stroke)
    {
        var color = stroke is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x7C, 0x5C, 0xFF);
        var start = Color.FromArgb(0x44, color.R, color.G, color.B);
        var end = Color.FromArgb(0x00, color.R, color.G, color.B);
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
