using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Dialogs;
using Creche.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Creche.App.Services;

/// <summary>In-house dialog service: every dialog is a view model shown in one themed modal window.</summary>
public sealed class DialogService(IServiceProvider services)
{
    /// <summary>Creates a dialog view model, injecting services and passing <paramref name="args"/> to its constructor.</summary>
    public T Create<T>(params object[] args) where T : DialogViewModel => ActivatorUtilities.CreateInstance<T>(services, args);

    /// <summary>Shows the dialog modally. Returns true when it was confirmed.</summary>
    public bool Show(DialogViewModel viewModel)
    {
        var window = new DialogWindow(viewModel) { Owner = ActiveWindow() };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    public bool Show<T>(out T viewModel, params object[] args) where T : DialogViewModel
    {
        viewModel = Create<T>(args);
        return Show(viewModel);
    }

    public bool Confirm(string title, string message, bool danger = false, string? confirmText = null)
        => Show(new MessageDialogViewModel(title, message, confirmText ?? Loc.T("Common.Confirm"), showCancel: true, danger));

    public void Message(string title, string message)
        => Show(new MessageDialogViewModel(title, message, Loc.T("Common.Ok"), showCancel: false, danger: false));

    /// <summary>Asks for one line of text. Null when cancelled.</summary>
    public string? Prompt(string title, string label, string initial = "", bool required = false)
    {
        var vm = new PromptDialogViewModel(title, label, initial, required);
        return Show(vm) ? vm.Text.Trim() : null;
    }

    public string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    private static Window? ActiveWindow()
    {
        var windows = WpfApp.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        return windows.FirstOrDefault(w => w.IsActive) ?? windows.LastOrDefault();
    }
}

public sealed partial class MessageDialogViewModel : DialogViewModel
{
    public MessageDialogViewModel(string title, string message, string confirmText, bool showCancel, bool danger)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        ShowCancel = showCancel;
        Danger = danger;
    }

    public string Message { get; }
    public string ConfirmText { get; }
    public bool ShowCancel { get; }
    public bool Danger { get; }

    [RelayCommand]
    private void Ok() => Close(true);
}

public sealed partial class PromptDialogViewModel : DialogViewModel
{
    private readonly bool _required;

    public PromptDialogViewModel(string title, string label, string initial, bool required)
    {
        Title = title;
        Label = label;
        Text = initial;
        _required = required;
    }

    public string Label { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Text { get; set; }

    private bool CanOk() => !_required || !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok() => Close(true);
}
