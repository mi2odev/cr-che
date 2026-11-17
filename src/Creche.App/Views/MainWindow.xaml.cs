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
