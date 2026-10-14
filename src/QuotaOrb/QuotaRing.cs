using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace QuotaOrb;

/// <summary>A snapshot indicator: never animates, estimates or changes quota data.</summary>
public sealed class QuotaRing : FrameworkElement
{
    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(
        nameof(Remaining), typeof(double?), typeof(QuotaRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double? Remaining { get => (double?)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var scale = Math.Min(ActualWidth, ActualHeight) / 50;
        var radius = Math.Max(0, 22 * scale);
        if (radius <= 0) return;
        var known = Remaining is { } number && double.IsFinite(number);
        var percent = known ? Math.Clamp(Remaining!.Value, 0, 100) : 0;
        var color = !known ? "#929BAA" : percent < 20 ? "#ED9B9F" : percent < 50 ? "#E5C775" : "#85BBFF";
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(70, 76, 87)), 3 * scale), center, radius, radius);
        var pen = new Pen(brush, 3 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (known && percent >= 100) drawing.DrawEllipse(null, pen, center, radius, radius);
        else if (known && percent > 0)
        {
            var angle = percent / 100 * 2 * Math.PI;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                path.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                    new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            drawing.DrawGeometry(null, pen, geometry);
        }
        var label = known ? MainWindow.Percent(percent) : "未知";
        SetValue(AutomationProperties.NameProperty, "剩余额度 " + label);
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            (label.Length > 5 ? 10 : 12) * scale, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}

public sealed class AccountCardWidthConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        var usable = Math.Max(200, (value is double width ? width : 340) - 2);
        return Math.Floor(usable / (usable >= 600 ? 2 : 1)) - 10;
    }
    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
