using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HardwareScope.Desktop;

// Native vector chrome: a cut top-right / bottom-left corner, with no animated effects.
public sealed class ChamferBorder : Border
{
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(nameof(Cut), typeof(double), typeof(ChamferBorder), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Cut { get => (double)GetValue(CutProperty); set => SetValue(CutProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var inset = BorderThickness.Left / 2;
        var w = Math.Max(0, ActualWidth - inset * 2);
        var h = Math.Max(0, ActualHeight - inset * 2);
        var cut = Math.Min(Cut, Math.Min(w, h) / 2);
        var shape = new StreamGeometry();
        using (var geometry = shape.Open())
        {
            geometry.BeginFigure(new Point(inset, inset), true, true);
            geometry.PolyLineTo([new(w - cut + inset, inset), new(w + inset, cut + inset), new(w + inset, h + inset), new(cut + inset, h + inset), new(inset, h - cut + inset)], true, false);
        }
        shape.Freeze();
        dc.DrawGeometry(Background, BorderBrush is null || BorderThickness.Left == 0 ? null : new Pen(BorderBrush, BorderThickness.Left), shape);
    }
}
