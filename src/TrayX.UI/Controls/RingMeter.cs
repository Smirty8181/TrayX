using System.Windows;
using System.Windows.Media;

namespace TrayX.UI.Controls;

public sealed class RingMeter : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent),
        typeof(double),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness),
        typeof(double),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(Brush),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = Math.Max(2, Thickness);
        if (size <= thickness * 2)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - thickness / 2 - 1;
        var trackBrush = Track ?? new SolidColorBrush(Color.FromArgb(0x55, 0x24, 0x32, 0x55));
        var strokeBrush = Stroke ?? new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));

        var trackPen = new Pen(trackBrush, thickness);
        trackPen.Freeze();
        context.DrawEllipse(null, trackPen, center, radius, radius);

        var percent = Math.Clamp(Percent, 0, 100);
        if (percent <= 0.2)
        {
            return;
        }

        var sweep = percent / 100 * 359.99;
        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + sweep);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(start, isFilled: false, isClosed: false);
            stream.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweep > 180,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        var pen = new Pen(strokeBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
