using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Entities;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public sealed partial class RollCallRow : ObservableObject
{
    public RollCallRow(RollCallItem item)
    {
        Item = item;
        CheckIn = item.CheckIn?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        CheckOut = item.CheckOut?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        DroppedOffBy = item.DroppedOffBy ?? "";
        PickedUpBy = item.PickedUpBy ?? "";
    }

    public RollCallItem Item { get; }
    public bool IsPresent => Item.Status == AttendanceStatus.Present;
    public bool IsAbsent => Item.Status == AttendanceStatus.Absent;
    public bool IsMarked => Item.Status is not null;
    public bool IsCheckedOut => Item.CheckOut is not null;
    public bool HasAllergies => !string.IsNullOrWhiteSpace(Item.Allergies);

    [ObservableProperty] public partial string CheckIn { get; set; }
    [ObservableProperty] public partial string CheckOut { get; set; }
    [ObservableProperty] public partial string DroppedOffBy { get; set; }
    [ObservableProperty] public partial string PickedUpBy { get; set; }
}

/// <summary>One-screen roll call: one row per child, one click to check in, check out or mark absent.</summary>
public sealed partial class AttendanceViewModel(IAttendanceService attendance, ISettingsService settings, ICurrentUser user) : PageViewModel
{
    private bool _ready;

    public ObservableCollection<Option<int?>> Groups { get; } = [];
    public ObservableCollection<RollCallRow> Rows { get; } = [];

    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool HasNoGroup { get; set; }

    public override async Task LoadAsync()
    {
        await Run(async () =>
        {
            var groups = await attendance.GetAccessibleGroupsAsync();
            var options = groups.Select(g => new Option<int?>(g.Id, g.Name)).ToList();
            // Only users who are not tied to a group may look at every group at once.
            if (user.CanAccessGroup(null)) options.Insert(0, new Option<int?>(null, Loc.T("Common.All")));
            Groups.ReplaceWith(options);
            HasNoGroup = options.Count == 0;
            int? saved = App.State.GetInt("attendance.group");
            Group = Groups.FirstOrDefault(g => g.Value == saved && saved is not null) ?? Groups.FirstOrDefault(g => g.Value is not null) ?? Groups.FirstOrDefault();
            _ready = true;
        });
        await ReloadAsync();
    }

    partial void OnDateChanged(DateTime value) => _ = ReloadAsync();
    partial void OnGroupChanged(Option<int?>? value) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        if (!_ready || Group is null) return;
        App.State.Set("attendance.group", Group.Value);
        await Run(async () =>
        {
            var items = await attendance.GetRollCallAsync(Group.Value, Date.ToDateOnly());
            Rows.ReplaceWith(items.Select(i => new RollCallRow(i)));
            Summary = Loc.T("Attendance.Summary", items.Count(i => i.Status == AttendanceStatus.Present),
                items.Count(i => i.Status == AttendanceStatus.Absent), items.Count(i => i.Status is null));
        });
    }

    [RelayCommand]
    private void ShiftDay(string days) => Date = Date.AddDays(int.Parse(days));

    [RelayCommand]
    private void Today() => Date = DateTime.Today;

    private async Task<TimeOnly?> ResolveTimeAsync(string text, bool closing)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (CollectionExtensions.TryParseTime(text, out var typed)) return typed;
            App.Toasts.Error(Loc.T("Error.TimeInvalid"));
            return null;
        }
        if (Date.Date == DateTime.Today) return TimeOnly.FromDateTime(DateTime.Now);
        var config = await settings.GetAsync();
        return closing ? config.ClosingTime : config.OpeningTime;
    }

    [RelayCommand]
    private async Task Present(RollCallRow row)
    {
        if (await ResolveTimeAsync(row.CheckIn, closing: false) is not { } time) return;
        if (await Run(() => attendance.CheckInAsync(row.Item.ChildId, Date.ToDateOnly(), time, row.DroppedOffBy))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task CheckOut(RollCallRow row)
    {
        if (await ResolveTimeAsync(row.CheckOut, closing: true) is not { } time) return;
        if (await Run(() => attendance.CheckOutAsync(row.Item.ChildId, Date.ToDateOnly(), time, row.PickedUpBy))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task Absent(RollCallRow row)
    {
        string? reason = App.Dialogs.Prompt(Loc.T("Attendance.MarkAbsent"), Loc.T("Attendance.Reason"), row.Item.AbsenceReason ?? "");
        if (reason is null) return;
        if (await Run(() => attendance.MarkAbsentAsync(row.Item.ChildId, Date.ToDateOnly(), reason))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task Undo(RollCallRow row)
    {
        if (await Run(() => attendance.ClearAsync(row.Item.ChildId, Date.ToDateOnly()))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task CheckInAll()
    {
        if (Group?.Value is not { } groupId)
        {
            App.Toasts.Warning(Loc.T("Attendance.PickGroup"));
            return;
        }
        if (await ResolveTimeAsync("", closing: false) is not { } time) return;
        int count = 0;
        if (await Run(async () => count = await attendance.CheckInAllAsync(groupId, Date.ToDateOnly(), time)))
        {
            App.Toasts.Success(Loc.T("Attendance.CheckedInAll", count));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void Sheet(RollCallRow row) => App.Dialogs.Show<MonthlySheetViewModel>(out _, row.Item.ChildId);
}

public sealed partial class ActivityLogViewModel(IActivityLogService log, IAttendanceService attendance, ICurrentUser user) : PageViewModel
{
    private bool _ready;

    public ObservableCollection<Option<int?>> Groups { get; } = [];
    public ObservableCollection<NamedItem> Children { get; } = [];
    public ObservableCollection<ActivityItem> Entries { get; } = [];
    public List<Option<ActivityType>> Types { get; } = Loc.Options<ActivityType>();

    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial NamedItem? Child { get; set; }
    [ObservableProperty] public partial ActivityType Type { get; set; } = ActivityType.Meal;
    [ObservableProperty] public partial string Time { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";

    public override async Task LoadAsync()
    {
        await Run(async () =>
        {
            var options = (await attendance.GetAccessibleGroupsAsync()).Select(g => new Option<int?>(g.Id, g.Name)).ToList();
            if (user.CanAccessGroup(null)) options.Insert(0, new Option<int?>(null, Loc.T("Common.All")));
            Groups.ReplaceWith(options);
            int? saved = App.State.GetInt("activity.group");
            Group = Groups.FirstOrDefault(g => g.Value == saved && saved is not null) ?? Groups.FirstOrDefault();
            _ready = true;
        });
        await ReloadAsync(reloadChildren: true);
    }

    partial void OnDateChanged(DateTime value) => _ = ReloadAsync(false);
    partial void OnGroupChanged(Option<int?>? value) => _ = ReloadAsync(true);

    private async Task ReloadAsync(bool reloadChildren)
    {
        if (!_ready || Group is null) return;
        App.State.Set("activity.group", Group.Value);
        await Run(async () =>
        {
            if (reloadChildren)
            {
                Children.ReplaceWith(await log.GetChildrenAsync(Group.Value));
                Child = Children.FirstOrDefault();
            }
            Entries.ReplaceWith(await log.ListAsync(Date.ToDateOnly(), Group.Value));
        });
    }

    [RelayCommand]
    private void ShiftDay(string days) => Date = Date.AddDays(int.Parse(days));

    [RelayCommand]
    private async Task Add()
    {
        if (Child is null)
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Child")));
            return;
        }
        TimeOnly? time = TimeOnly.FromDateTime(DateTime.Now);
        if (!string.IsNullOrWhiteSpace(Time))
        {
            if (!CollectionExtensions.TryParseTime(Time, out var typed))
            {
                App.Toasts.Error(Loc.T("Error.TimeInvalid"));
                return;
            }
            time = typed;
        }
        var entry = new ActivityLogEntry { ChildId = Child.Id, Date = Date.ToDateOnly(), Time = time, Type = Type, Description = Description };
        if (!await Run(() => log.SaveAsync(entry))) return;
        Description = "";
        Time = "";
        await ReloadAsync(false);
    }

    [RelayCommand]
    private async Task Delete(ActivityItem item)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Delete"), Loc.T("Common.DeleteConfirm"), danger: true)) return;
        if (await Run(() => log.DeleteAsync(item.Id))) await ReloadAsync(false);
    }
}
