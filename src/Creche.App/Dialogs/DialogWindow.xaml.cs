using System.Windows;
using System.Windows.Input;
using Creche.App.ViewModels;

namespace Creche.App.Dialogs;

public partial class DialogWindow : Window
{
    public DialogWindow(DialogViewModel viewModel)
    {
        InitializeComponent();
        Creche.App.Services.Loc.Apply(this);
        DataContext = viewModel;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 40;
        viewModel.CloseRequested += () =>
        {
            if (IsVisible) DialogResult = viewModel.Result;
        };
        SourceInitialized += (_, _) => App.Themes.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            await viewModel.LoadAsync();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };
    }
}
