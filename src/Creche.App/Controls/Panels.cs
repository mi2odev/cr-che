using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Creche.App.Controls;

/// <summary>
/// Responsive grid: as many equal-width columns as fit <see cref="MinColumnWidth"/>, rows as tall as their tallest item.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ResponsiveGrid), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return Math.Max(1, Math.Min(MaxColumns, InternalChildren.Count));
        int columns = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        return Math.Clamp(columns, 1, Math.Max(1, MaxColumns));
    }

    private List<UIElement> VisibleChildren()
    {
        var list = new List<UIElement>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) list.Add(child);
        return list;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        int columns = ColumnsFor(availableSize.Width);
        double columnWidth = double.IsInfinity(availableSize.Width)
            ? MinColumnWidth
            : Math.Max(0, (availableSize.Width - Spacing * (columns - 1)) / columns);

        double height = 0;
        for (int i = 0; i < children.Count; i += columns)
        {
            double rowHeight = 0;
            for (int j = i; j < Math.Min(i + columns, children.Count); j++)
            {
                children[j].Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[j].DesiredSize.Height);
            }
            height += rowHeight + (i > 0 ? Spacing : 0);
        }
        double width = double.IsInfinity(availableSize.Width) ? columns * columnWidth + Spacing * (columns - 1) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        int columns = ColumnsFor(finalSize.Width);
        double columnWidth = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        double y = 0;
        for (int i = 0; i < children.Count; i += columns)
        {
            int end = Math.Min(i + columns, children.Count);
            double rowHeight = 0;
            for (int j = i; j < end; j++) rowHeight = Math.Max(rowHeight, children[j].DesiredSize.Height);
            for (int j = i; j < end; j++)
                children[j].Arrange(new Rect((j - i) * (columnWidth + Spacing), y, columnWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}

/// <summary>A labelled form field: caption above any input.</summary>
public sealed class Field : ContentControl
{
    static Field() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Field), new FrameworkPropertyMetadata(typeof(Field)));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Field));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}

/// <summary>Dashboard tile: icon, caption and one big figure.</summary>
public sealed class StatCard : Control
{
    static StatCard() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatCard), new FrameworkPropertyMetadata(typeof(StatCard)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(StatCard), new PropertyMetadata(""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(StatCard));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Optional small line under the figure ("of 42", "this month"...).</summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}

/// <summary>Friendly "nothing here yet" block: icon, title, explanation and an optional action (the content).</summary>
public sealed class EmptyState : ContentControl
{
    static EmptyState() => DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyState), new PropertyMetadata("info"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

/// <summary>Thin horizontal bar showing a 0–1 ratio (group occupancy, report bars).</summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width, double.IsNaN(Height) ? 8 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double radius = ActualHeight / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
        double ratio = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (ratio <= 0) return;
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(ActualHeight, ActualWidth * ratio), ActualHeight), radius, radius);
    }
}

/// <summary>Attached property: <c>c:Ico.Kind="plus"</c> puts an icon in front of a button's text.</summary>
public static class Ico
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(Ico), new PropertyMetadata(""));

    public static string GetKind(DependencyObject element) => (string)element.GetValue(KindProperty);

    public static void SetKind(DependencyObject element, string value) => element.SetValue(KindProperty, value);
}

/// <summary>Attached property: <c>c:Hint.Text="Search…"</c> shows a placeholder in an empty text box.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(""));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
