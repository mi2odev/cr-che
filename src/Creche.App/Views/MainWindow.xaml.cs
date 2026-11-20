using System.Windows;

namespace Creche.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Creche.App.Services.Loc.Apply(this);
        // Restore the last window size and state.
        if (App.State.GetDouble("window.width") is { } width && App.State.GetDouble("window.height") is { } height
            && width >= MinWidth && height >= MinHeight && width <= SystemParameters.VirtualScreenWidth && height <= SystemParameters.VirtualScreenHeight)
        {
            Width = width;
            Height = height;
        }
        if (App.State.GetBool("window.maximized")) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => App.Themes.ApplyTitleBar(this);
        // Mouse "back" button and the keyboard Back key return to the previous page.
        CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.NavigationCommands.BrowseBack,
            (_, _) => App.Shell?.GoBackCommand.Execute(null)));
        Closing += (_, _) =>
        {
            App.State.Set("window.maximized", WindowState == WindowState.Maximized);
            if (WindowState == WindowState.Normal)
            {
                App.State.Set("window.width", Width);
                App.State.Set("window.height", Height);
            }
        };
        Closed += (_, _) => ((App)WpfApp.Current).OnWindowClosed(this);
    }
}
