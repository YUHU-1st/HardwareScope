using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HardwareScope.Desktop;

public sealed record ChartPoint(DateTimeOffset Time, double? Value);
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<ChartPoint> Points);

public sealed class TrendChart : FrameworkElement
{
    private static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register("AxisBrush", typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register("LabelBrush", typeof(Brush), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public TrendChart()
    {
        SetResourceReference(AxisBrushProperty, "Stroke");
        SetResourceReference(LabelBrushProperty, "Muted");
    }
    private IReadOnlyList<ChartSeries> series = [];
    private string unit = "%";
    private bool percent;
    public void SetData(IReadOnlyList<ChartSeries> values, string valueUnit = "%", bool fixedPercent = true)
    {
        series = values;
        unit = valueUnit;
        percent = fixedPercent;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 100 || ActualHeight < 100) return;
        var muted = (Brush)GetValue(LabelBrushProperty);
        var w = ActualWidth;
        var h = ActualHeight;
        var left = 51d;
        var right = w - 16;
        var top = 35d;
        var bottom = h - 31;
        var values = series.SelectMany(s => s.Points).Where(p => p.Value is double v && double.IsFinite(v)).ToArray();
        double minValue = percent ? 0 : Math.Min(0, values.Length > 0 ? values.Min(p => p.Value!.Value) : 0);
        double maxValue = percent ? 100 : Math.Max(1, values.Length > 0 ? values.Max(p => p.Value!.Value) * 1.15 : 1);
        var axis = new Pen((Brush)GetValue(AxisBrushProperty), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = top + (bottom - top) * i / 4;
            dc.DrawLine(axis, new Point(left, y), new Point(right, y));
            DrawText(dc, (maxValue - (maxValue - minValue) * i / 4).ToString(maxValue < 10 ? "0.0" : "0", CultureInfo.CurrentCulture), new Point(5, y - 8), muted, 11);
        }
        var legendX = left;
        foreach (var s in series)
        {
            var color = new SolidColorBrush(s.Color);
            dc.DrawEllipse(color, null, new Point(legendX + 4, 12), 3, 3);
            var latest = s.Points.LastOrDefault()?.Value;
            var label = s.Name + (latest is double v ? "  " + v.ToString("0.#") + unit : "  —");
            var text = CreateText(label, color, 12);
            dc.DrawText(text, new Point(legendX + 14, 3));
            legendX += text.Width + 37;
        }
        var times = series.SelectMany(s => s.Points).Select(p => p.Time).ToArray();
        if (values.Length == 0 || times.Length < 2)
        {
            DrawText(dc, values.Length == 0 ? "此指标尚无可用数据" : "正在积累历史采样…", new Point(left + 15, (top + bottom) / 2 - 10), muted, 13);
            return;
        }
        var end = times.Max();
        var start = times.Min();
        var duration = Math.Max(1, (end - start).TotalSeconds);
        foreach (var s in series)
        {
            var brush = new SolidColorBrush(s.Color);
            var pen = new Pen(brush, 2);
            Point? previous = null;
            foreach (var p in s.Points)
            {
                if (p.Value is not double v || !double.IsFinite(v)) { previous = null; continue; }
                var position = new Point(left + (right - left) * (p.Time - start).TotalSeconds / duration, bottom - (bottom - top) * Math.Clamp((v - minValue) / (maxValue - minValue), 0, 1));
                if (previous is Point before) dc.DrawLine(pen, before, position);
                previous = position;
            }
            if (previous is Point last) dc.DrawEllipse(brush, null, last, 3, 3);
        }
        DrawText(dc, start.LocalDateTime.ToString("HH:mm:ss"), new Point(left, bottom + 11), muted, 11);
        DrawText(dc, end.LocalDateTime.ToString("HH:mm:ss"), new Point(Math.Max(left, right - 55), bottom + 11), muted, 11);
    }

    private FormattedText CreateText(string value, Brush brush, double size) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void DrawText(DrawingContext dc, string value, Point point, Brush brush, double size) => dc.DrawText(CreateText(value, brush, size), point);
}
