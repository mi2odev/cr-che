using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;

namespace Creche.App.ViewModels;

public sealed record GroupMeter(string Name, string Occupancy, string Free, double Ratio, bool IsFull, string? Educators);

public sealed record AlertRow(string Text, string Tone, string? NavKey)
{
    public bool CanOpen => NavKey is not null;
}

public sealed record LabelBar(string Label, string Value, double Ratio);

public sealed partial class DashboardViewModel(IDashboardService dashboard, ISettingsService settings) : PageViewModel
{
    public ObservableCollection<GroupMeter> Groups { get; } = [];
    public ObservableCollection<BirthdayItem> Birthdays { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];

    [ObservableProperty] public partial DateOnly Date { get; set; }
    [ObservableProperty] public partial string Present { get; set; } = "–";
    [ObservableProperty] public partial string Absent { get; set; } = "–";
    [ObservableProperty] public partial string NotMarked { get; set; } = "–";
    [ObservableProperty] public partial string ActiveChildren { get; set; } = "–";
    [ObservableProperty] public partial string FreePlaces { get; set; } = "–";
    [ObservableProperty] public partial string FeesDue { get; set; } = "–";
    [ObservableProperty] public partial string FeesOverdue { get; set; } = "–";
    [ObservableProperty] public partial string Received { get; set; } = "–";

    public override Task LoadAsync() => Run(async () =>
    {
        var data = await dashboard.GetAsync();
        string currency = (await settings.GetAsync()).Currency;
        Date = data.Date;
        Present = data.PresentToday.ToString();
        Absent = data.AbsentToday.ToString();
        NotMarked = data.NotMarkedToday.ToString();
        ActiveChildren = data.ActiveChildren.ToString();
        FreePlaces = data.Groups.Sum(g => g.Free).ToString();
        FeesDue = Money.Format(data.FeesDue, currency);
        FeesOverdue = Money.Format(data.FeesOverdue, currency);
        Received = Money.Format(data.ReceivedThisMonth, currency);

        Groups.ReplaceWith(data.Groups.Select(g => new GroupMeter(g.Name, $"{g.Occupied} / {g.Capacity}",
            g.IsFull ? Loc.T("Groups.Full") : Loc.T("Groups.FreePlaces", g.Free),
            g.Capacity == 0 ? 0 : (double)g.Occupied / g.Capacity, g.IsFull, g.Educators)));
        Birthdays.ReplaceWith(data.Birthdays);
        Alerts.ReplaceWith(data.Alerts.Select(ToRow));
    });

    private static AlertRow ToRow(AlertItem alert) => alert.Kind switch
    {
        AlertKind.OverdueFees => new(Loc.T("Alert.OverdueFees", alert.Count), "Danger", "payments"),
        AlertKind.GroupFull => new(Loc.T("Alert.GroupFull", alert.Text), "Warning", "groups"),
        AlertKind.WaitingList => new(Loc.T("Alert.WaitingList", alert.Count), "Info", "enrolment"),
        AlertKind.InvoicesNotGenerated => new(Loc.T("Alert.InvoicesNotGenerated", alert.Text), "Warning", "billing"),
        AlertKind.AttendanceNotTaken => new(Loc.T("Alert.AttendanceNotTaken", alert.Count), "Info", "attendance"),
        AlertKind.NoRecentBackup => new(Loc.T("Alert.NoRecentBackup"), "Warning", "settings"),
        _ => new(alert.Text, "Info", null),
    };

    [RelayCommand]
    private void OpenAlert(AlertRow? alert)
    {
        if (alert?.NavKey is { } key && App.Shell is { } shell && shell.CanNavigate(key)) shell.Navigate(key);
    }

    [RelayCommand]
    private void OpenChild(BirthdayItem? item)
    {
        if (item is not null) App.Shell?.Navigate("children", item.ChildId);
    }
}
