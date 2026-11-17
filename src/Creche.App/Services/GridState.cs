using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Creche.App.Services;

/// <summary>
/// Attached behaviour: <c>loc:GridState.Key="children"</c> remembers a DataGrid's sort column and direction
/// in ui-state.json and restores them next time.
/// </summary>
public static class GridState
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(GridState), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not string key || key.Length == 0) return;
        grid.Loaded += (_, _) => Restore(grid, key);
        grid.Sorting += (_, _) => grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Save(grid, key));
    }

    private static void Restore(DataGrid grid, string key)
    {
        string? saved = App.State.Get($"sort.{key}");
        if (string.IsNullOrEmpty(saved)) return;
        var parts = saved.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<ListSortDirection>(parts[1], out var direction)) return;
        var column = grid.Columns.FirstOrDefault(c => c.SortMemberPath == parts[0]);
        if (column is null) return;
        grid.Items.SortDescriptions.Clear();
        grid.Items.SortDescriptions.Add(new SortDescription(parts[0], direction));
        foreach (var other in grid.Columns) other.SortDirection = null;
        column.SortDirection = direction;
    }

    private static void Save(DataGrid grid, string key)
    {
        var column = grid.Columns.FirstOrDefault(c => c.SortDirection is not null);
        App.State.Set($"sort.{key}", column is null ? null : $"{column.SortMemberPath}|{column.SortDirection}");
    }
}
