using System.Windows;
using System.Windows.Controls;

namespace Creche.App.Services;

/// <summary>Lets a PasswordBox push its value to a view model: <c>loc:PasswordHelper.Password="{Binding Password, Mode=TwoWay}"</c>.</summary>
public static class PasswordHelper
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordHelper),
        
        // Default is null (not "") so the very first binding update, even to an empty string, attaches the handler.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(PasswordHelper), new PropertyMetadata(false));

    public static string? GetPassword(DependencyObject element) => (string?)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string? value) => element.SetValue(PasswordProperty, value);

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(HookedProperty))
        {
            box.SetValue(HookedProperty, true);
            box.PasswordChanged += (_, _) =>
            {
                if (GetPassword(box) != box.Password) SetPassword(box, box.Password);
            };
        }
        string value = e.NewValue as string ?? "";
        if (box.Password != value) box.Password = value;
    }
}
