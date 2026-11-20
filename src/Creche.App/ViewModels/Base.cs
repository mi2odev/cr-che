using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Domain;
using Microsoft.Extensions.Logging;

namespace Creche.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    private int _busy;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Runs a piece of UI work: shows the busy state, and turns broken business rules and unexpected errors
    /// into toasts instead of crashes. Returns true when the work completed.
    /// </summary>
    protected async Task<bool> Run(Func<Task> action)
    {
        try
        {
            _busy++;
            IsBusy = true;
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (BusinessRuleException ex)
        {
            App.Toasts.Error(Loc.Error(ex));
            return false;
        }
        catch (Exception ex)
        {
            App.Log.LogError(ex, "Unexpected error in {ViewModel}", GetType().Name);
            App.Toasts.Error(Loc.T("Error.Unexpected", ex.Message));
            return false;
        }
        finally
        {
            _busy--;
            IsBusy = _busy > 0;
        }
    }

    /// <summary>
    /// For operations that place a child in a group: when the group is full and the crèche only warns,
    /// asks for confirmation and retries with "allow over capacity".
    /// </summary>
    protected Task<bool> RunWithCapacity(Func<bool, Task> action) => Run(async () =>
    {
        try
        {
            await action(false);
        }
        catch (GroupFullWarningException ex)
        {
            if (!App.Dialogs.Confirm(Loc.T("Groups.FullTitle"), Loc.Error(ex) + "\n" + Loc.T("Groups.FullConfirm")))
                throw new OperationCanceledException();
            await action(true);
        }
    });
}

/// <summary>A screen shown in the main area of the shell.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Optional argument passed by whoever navigated here (for example a child id to open).</summary>
    public object? Parameter { get; set; }

    /// <summary>Parameter value asking a page to open its "new record" form as soon as it is shown.</summary>
    public const string NewAction = "new";

    /// <summary>True (once) when the page was opened by a quick action asking for a new record.</summary>
    protected bool TakeNewAction()
    {
        if (Parameter as string != NewAction) return false;
        Parameter = null;
        return true;
    }

    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>F5 reloads whichever page is shown.</summary>
    public IAsyncRelayCommand RefreshCommand => field ??= new AsyncRelayCommand(LoadAsync);
}

/// <summary>A modal dialog hosted by <see cref="DialogService"/>.</summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    public bool Result { get; private set; }
    public event Action? CloseRequested;

    public virtual Task LoadAsync() => Task.CompletedTask;

    protected void Close(bool result)
    {
        Result = result;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}

public static class CollectionExtensions
{
    /// <summary>Replaces the content in place so grids keep their sort order and scroll position.</summary>
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    public static DateOnly ToDateOnly(this DateTime date) => DateOnly.FromDateTime(date);

    public static DateTime ToDateTime(this DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        text = (text ?? "").Trim().Replace('h', ':').Replace('.', ':');
        if (text.Length is 1 or 2 && int.TryParse(text, out int hour) && hour is >= 0 and < 24)
        {
            time = new TimeOnly(hour, 0);
            return true;
        }
        return TimeOnly.TryParseExact(text, ["H:mm", "HH:mm", "Hmm", "HHmm"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out time);
    }
}
