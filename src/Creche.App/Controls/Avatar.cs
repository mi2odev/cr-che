using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Creche.App.Services;

namespace Creche.App.Controls;

/// <summary>Round photo; falls back to the person's initials on a tinted disc.</summary>
public sealed class Avatar : FrameworkElement
{
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Avatar), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(Avatar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    // Photos and initials must never be mirrored in right-to-left layouts.
    public Avatar() => FlowDirection = FlowDirection.LeftToRight;

    public string? FileName
    {
        get => (string?)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsNaN(Width) ? 36 : Width, double.IsNaN(Height) ? 36 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var image = ImageStore.Load(FileName, (int)Math.Max(64, size * 2));
        if (image is not null)
        {
            // Centre-crop to a square so the photo is not stretched.
            double w = image.Width, h = image.Height, side = Math.Min(w, h);
            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.UniformToFill,
                Viewbox = new Rect((w - side) / 2 / w, (h - side) / 2 / h, side / w, side / h),
            };
            dc.DrawEllipse(brush, null, center, size / 2, size / 2);
            return;
        }

        dc.DrawEllipse(Background, null, center, size / 2, size / 2);
        string initials = Initials(DisplayName);
        if (initials.Length == 0) return;
        var text = new FormattedText(initials, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.38, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string first = parts[0][..1];
        string second = parts.Length > 1 ? parts[^1][..1] : "";
        return (first + second).ToUpper(CultureInfo.CurrentCulture);
    }
}
