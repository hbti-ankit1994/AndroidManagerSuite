using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AndroidManagerSuite.App.Controls;

public sealed class DonutGauge : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(string),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowCenterTextProperty = DependencyProperty.Register(
        nameof(ShowCenterText),
        typeof(bool),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public bool ShowCenterText
    {
        get => (bool)GetValue(ShowCenterTextProperty);
        set => SetValue(ShowCenterTextProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, size / 2 - 18);
        var thickness = Math.Max(10, radius * 0.18);
        var percent = Math.Clamp(Value, 0, 100);

        var trackPen = new Pen(new SolidColorBrush(Color.FromRgb(37, 47, 67)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var valuePen = new Pen(AccentBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);
        if (percent > 0)
        {
            DrawArc(drawingContext, valuePen, center, radius, -90, 360 * percent / 100);
        }

        if (ShowCenterText)
        {
            DrawCenteredText(drawingContext, $"{percent:0}%", 24, FontWeights.SemiBold, -14, Brushes.White);
            DrawCenteredText(drawingContext, Title, 12, FontWeights.SemiBold, 12, new SolidColorBrush(Color.FromRgb(197, 213, 232)));
            DrawCenteredText(drawingContext, Detail, 11, FontWeights.Normal, 28, new SolidColorBrush(Color.FromRgb(125, 148, 175)));
        }
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point center, double radius, double startAngle, double sweepAngle)
    {
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();

        using (var stream = geometry.Open())
        {
            stream.BeginFigure(start, false, false);
            stream.ArcTo(end, new Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        var radians = Math.PI * angle / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private void DrawCenteredText(DrawingContext context, string text, double fontSize, FontWeight weight, double yOffset, Brush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(10, ActualWidth - 36),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1
        };

        context.PushClip(new RectangleGeometry(new Rect(8, 0, Math.Max(1, ActualWidth - 16), ActualHeight)));
        context.DrawText(formattedText, new Point(18, (ActualHeight - formattedText.Height) / 2 + yOffset));
        context.Pop();
    }
}
