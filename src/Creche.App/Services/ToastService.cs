using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Creche.App.Services;

public enum ToastKind { Info, Success, Warning, Error }

public sealed record Toast(string Message, ToastKind Kind)
{
    public string Icon => Kind switch
    {
        ToastKind.Success => "check-circle",
        ToastKind.Warning or ToastKind.Error => "alert",
        _ => "info",
    };
}

/// <summary>Small, self-dismissing notifications shown in the corner of the main window.</summary>
public sealed class ToastService
{
    public ObservableCollection<Toast> Items { get; } = [];

    /// <summary>Clicking a toast closes it straight away.</summary>
    public ICommand DismissCommand => field ??= new RelayCommand<Toast>(t => { if (t is not null) Dismiss(t); });

    public void Info(string message) => Show(message, ToastKind.Info);
    public void Success(string message) => Show(message, ToastKind.Success);
    public void Warning(string message) => Show(message, ToastKind.Warning);
    public void Error(string message) => Show(message, ToastKind.Error);

    public void Show(string message, ToastKind kind)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, kind));
            return;
        }
        var toast = new Toast(message, kind);
        Items.Add(toast);
        while (Items.Count > 4) Items.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 7 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Items.Remove(toast);
        };
        timer.Start();
    }

    public void Dismiss(Toast toast) => Items.Remove(toast);
}
