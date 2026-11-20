using System.Windows;
using System.Windows.Controls;

namespace Creche.App.Services;

/// <summary>Small keyboard-friendliness helpers for lists.</summary>
public static class FocusHelper
{
    /// <summary>
    /// <c>loc:FocusHelper.ScrollSelectedIntoView="True"</c> keeps the selected row visible while it is moved with the
    /// arrow keys from another control (the command palette's search box).
    /// </summary>
    public static readonly DependencyProperty ScrollSelectedIntoViewProperty = DependencyProperty.RegisterAttached(
        "ScrollSelectedIntoView", typeof(bool), typeof(FocusHelper), new PropertyMetadata(false, OnScrollSelectedIntoViewChanged));

    public static bool GetScrollSelectedIntoView(DependencyObject element) => (bool)element.GetValue(ScrollSelectedIntoViewProperty);

    public static void SetScrollSelectedIntoView(DependencyObject element, bool value) => element.SetValue(ScrollSelectedIntoViewProperty, value);

    private static void OnScrollSelectedIntoViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        list.SelectionChanged -= OnSelectionChanged;
        if (e.NewValue is true) list.SelectionChanged += OnSelectionChanged;
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }
}
