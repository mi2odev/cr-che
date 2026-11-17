using System.Windows;
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
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => App.Themes.Toggle();
}
