using System.Windows;
using System.Windows.Input;
using Creche.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Creche.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Creche.App.Services.Loc.Apply(this);
        var viewModel = ActivatorUtilities.CreateInstance<LoginViewModel>(App.Services);
        DataContext = viewModel;
        SourceInitialized += (_, _) => App.Themes.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync();
            if (viewModel.Username.Length == 0) username.Focus();
            else password.Focus();
        };
        Closed += (_, _) => ((App)WpfApp.Current).OnWindowClosed(this);

        // Warn about Caps Lock while typing the password: the most common reason for "wrong password".
        PreviewKeyDown += (_, _) => UpdateCapsLock();
        PreviewKeyUp += (_, _) => UpdateCapsLock();
        password.GotKeyboardFocus += (_, _) => UpdateCapsLock();
        password.LostKeyboardFocus += (_, _) => UpdateCapsLock();
        UpdateThemeIcon();
    }

    private void UpdateCapsLock()
        => capsLock.Visibility = password.IsKeyboardFocused && Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateThemeIcon() => themeIcon.Kind = App.Themes.IsDark ? "sun" : "moon";

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        App.Themes.Toggle();
        UpdateThemeIcon();
    }
}
