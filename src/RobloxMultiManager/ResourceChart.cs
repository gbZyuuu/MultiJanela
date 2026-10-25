using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace RobloxMultiManager;

public sealed class ResourceChart : FrameworkElement
{
    private double?[] _values = [];
    private double _maximum;
    public void SetValues(IEnumerable<double?> values, double? fixedMaximum = null)
    { _values = values.TakeLast(180).ToArray(); _maximum = fixedMaximum ?? Math.Max(1, _values.Max() ?? 1); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var foreground = TryFindResource("Accent") as System.Windows.Media.Brush ?? Brushes.MediumPurple;
        var line = new Pen(foreground, 2);
        var grid = new Pen(TryFindResource("HairlineSoft") as System.Windows.Media.Brush ?? Brushes.Gray, 1);
        for (var i = 1; i < 4; i++) context.DrawLine(grid, new Point(0, ActualHeight * i / 4), new Point(ActualWidth, ActualHeight * i / 4));
        if (_values.Length < 2) return;
        Point? previous = null;
        for (var i = 0; i < _values.Length; i++)
        {
            if (_values[i] is not double value || !double.IsFinite(value)) { previous = null; continue; }
            var point = new Point(i * ActualWidth / (_values.Length - 1), ActualHeight - Math.Clamp(value / Math.Max(1, _maximum), 0, 1) * ActualHeight);
            if (previous is Point start) context.DrawLine(line, start, point);
            previous = point;
        }
    }
}
