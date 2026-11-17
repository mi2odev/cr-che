using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public sealed partial class ReportsViewModel(IReportService reports, ISettingsService settings) : PageViewModel
{
    private bool _ready;
    private CrecheSettings _settings = new();

    public ObservableCollection<LabelBar> ExpensesByCategory { get; } = [];
    public ObservableCollection<LabelBar> ChildrenPerGroup { get; } = [];
    public ObservableCollection<LabelBar> RevenueByMethod { get; } = [];
    public ObservableCollection<DebtorItem> Debtors { get; } = [];

    [ObservableProperty] public partial string Preset { get; set; } = "month";
    [ObservableProperty] public partial DateTime? From { get; set; }
    [ObservableProperty] public partial DateTime? To { get; set; }
    [ObservableProperty] public partial ReportData? Data { get; set; }
    [ObservableProperty] public partial string Revenue { get; set; } = "–";
    [ObservableProperty] public partial string Invoiced { get; set; } = "–";
    [ObservableProperty] public partial string Unpaid { get; set; } = "–";
    [ObservableProperty] public partial string Expenses { get; set; } = "–";
    [ObservableProperty] public partial string NetProfit { get; set; } = "–";
    [ObservableProperty] public partial string AttendanceRate { get; set; } = "–";
    [ObservableProperty] public partial string Enrolments { get; set; } = "–";
    [ObservableProperty] public partial string Departures { get; set; } = "–";

    public override async Task LoadAsync()
    {
        _settings = await settings.GetAsync();
        _ready = true;
        Preset = App.State.Get("reports.preset", "month");
        ApplyPreset(Preset);
        await ReloadAsync();
    }

    partial void OnPresetChanged(string value)
    {
        if (!_ready) return;
        App.State.Set("reports.preset", value);
        ApplyPreset(value);
        _ = ReloadAsync();
    }

    partial void OnFromChanged(DateTime? value)
    {
        if (Preset == "custom") _ = ReloadAsync();
    }

    partial void OnToChanged(DateTime? value)
    {
        if (Preset == "custom") _ = ReloadAsync();
    }

    private void ApplyPreset(string preset)
    {
        var today = DateTime.Today;
        switch (preset)
        {
            case "day": From = today; To = today; break;
            case "week":
                int offset = ((int)today.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
                From = today.AddDays(-offset); To = From.Value.AddDays(6); break;
            case "month": From = new DateTime(today.Year, today.Month, 1); To = From.Value.AddMonths(1).AddDays(-1); break;
            case "year": From = new DateTime(today.Year, 1, 1); To = new DateTime(today.Year, 12, 31); break;
            default: From ??= new DateTime(today.Year, today.Month, 1); To ??= today; break;
        }
    }

    private async Task ReloadAsync()
    {
        if (!_ready || From is null || To is null) return;
        await Run(async () =>
        {
            var data = await reports.GetAsync(From.Value.ToDateOnly(), To.Value.ToDateOnly());
            string c = _settings.Currency;
            Data = data;
            Revenue = Money.Format(data.Revenue, c);
            Invoiced = Money.Format(data.Invoiced, c);
            Unpaid = Money.Format(data.Unpaid, c);
            Expenses = Money.Format(data.Expenses, c);
            NetProfit = Money.Format(data.NetProfit, c);
            AttendanceRate = $"{data.AttendanceRate:0.#} %";
            Enrolments = data.Enrolments.ToString();
            Departures = data.Departures.ToString();
            decimal maxExpense = data.ExpensesByCategory.Select(e => e.Amount).DefaultIfEmpty(0m).Max();
            ExpensesByCategory.ReplaceWith(data.ExpensesByCategory.Select(e => new LabelBar(Loc.Enum(e.Category), Money.Format(e.Amount, c), Ratio(e.Amount, maxExpense))));
            decimal maxRevenue = data.RevenueByMethod.Select(e => e.Amount).DefaultIfEmpty(0m).Max();
            RevenueByMethod.ReplaceWith(data.RevenueByMethod.Select(e => new LabelBar(Loc.Enum(e.Method), Money.Format(e.Amount, c), Ratio(e.Amount, maxRevenue))));
            ChildrenPerGroup.ReplaceWith(data.ChildrenPerGroup.Select(g => new LabelBar(g.GroupName, g.Capacity > 0 ? $"{g.Children} / {g.Capacity}" : g.Children.ToString(),
                g.Capacity > 0 ? (double)g.Children / g.Capacity : 1)));
            Debtors.ReplaceWith(data.Debtors);
        });
    }

    private static double Ratio(decimal value, decimal max) => max <= 0m ? 0 : (double)(value / max);

    [RelayCommand]
    private void Print()
    {
        if (Data is not { } data) return;
        var config = _settings;
        App.Printer.Preview($"{Loc.T("Reports.Title")} {data.From:yyyy-MM-dd} {data.To:yyyy-MM-dd}", () => PrintDocuments.Report(data, config));
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Data is not { } r) return;
        string? path = App.Dialogs.SaveFile($"report-{r.From:yyyy-MM-dd}-{r.To:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter();
        csv.Row(Loc.T("Reports.Title"), r.From, r.To).Blank();
        csv.Row(Loc.T("Reports.Invoiced"), r.Invoiced).Row(Loc.T("Reports.Revenue"), r.Revenue).Row(Loc.T("Reports.Expenses"), r.Expenses)
            .Row(Loc.T("Reports.NetProfit"), r.NetProfit).Row(Loc.T("Reports.Unpaid"), r.Unpaid)
            .Row(Loc.T("Reports.AttendanceRate"), r.AttendanceRate).Row(Loc.T("Attendance.PresentDays"), r.PresentDays).Row(Loc.T("Attendance.AbsentDays"), r.AbsentDays)
            .Row(Loc.T("Reports.Enrolments"), r.Enrolments).Row(Loc.T("Reports.Departures"), r.Departures).Row(Loc.T("Dashboard.ActiveChildren"), r.ActiveChildren).Blank();
        csv.Row(Loc.T("Reports.ChildrenPerGroup")).Row(Loc.T("Common.Group"), Loc.T("Nav.children"), Loc.T("Groups.Capacity"));
        foreach (var g in r.ChildrenPerGroup) csv.Row(g.GroupName, g.Children, g.Capacity);
        csv.Blank().Row(Loc.T("Reports.ExpensesByCategory"));
        foreach (var e in r.ExpensesByCategory) csv.Row(Loc.Enum(e.Category), e.Amount);
        csv.Blank().Row(Loc.T("Reports.RevenueByMethod"));
        foreach (var e in r.RevenueByMethod) csv.Row(Loc.Enum(e.Method), e.Amount);
        csv.Blank().Row(Loc.T("Payments.Debtors")).Row(Loc.T("Common.Family"), Loc.T("Common.Phone"), Loc.T("Payments.AmountDue"), Loc.T("Payments.Since"), Loc.T("Payments.DaysLate"));
        foreach (var d in r.Debtors) csv.Row(d.FamilyName, d.Phone, d.AmountDue, d.OldestDueDate, d.DaysLate);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed partial class UsersViewModel(IUserService users) : PageViewModel
{
    public ObservableCollection<UserInfo> Items { get; } = [];

    [ObservableProperty] public partial UserInfo? Selected { get; set; }

    public override Task LoadAsync() => Run(async () => Items.ReplaceWith(await users.ListAsync()));

    [RelayCommand]
    private async Task Edit(UserInfo? user)
    {
        var edit = user is null
            ? new UserEdit()
            : new UserEdit { Id = user.Id, Username = user.Username, DisplayName = user.DisplayName, Role = user.Role, GroupId = user.GroupId, IsActive = user.IsActive };
        if (App.Dialogs.Show<UserDialogViewModel>(out _, edit)) await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleActive(UserInfo user)
    {
        if (user.IsActive && !App.Dialogs.Confirm(Loc.T("Common.Disable"), Loc.T("Common.DisableConfirm", user.DisplayName), danger: true)) return;
        if (await Run(() => users.SetActiveAsync(user.Id, !user.IsActive))) await LoadAsync();
    }
}

public sealed partial class UserDialogViewModel : DialogViewModel
{
    private readonly IUserService _users;
    private readonly IGroupService _groups;

    public UserDialogViewModel(UserEdit edit, IUserService users, IGroupService groups)
    {
        Edit = edit;
        _users = users;
        _groups = groups;
        Role = edit.Role;
        Title = Loc.T(edit.Id == 0 ? "Users.New" : "Users.Edit");
    }

    public UserEdit Edit { get; }
    public bool IsNew => Edit.Id == 0;
    public List<Option<UserRole>> Roles { get; } = Loc.Options<UserRole>();
    public ObservableCollection<Option<int?>> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEducator))]
    public partial UserRole Role { get; set; }

    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";

    public bool IsEducator => Role == UserRole.Educator;

    public override Task LoadAsync() => Run(async () =>
    {
        Groups.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat((await _groups.ListAsync()).Select(g => new Option<int?>(g.Id, g.Name))));
        Group = Groups.FirstOrDefault(g => g.Value == Edit.GroupId) ?? Groups[0];
    });

    [RelayCommand]
    private async Task Save()
    {
        Edit.Role = Role;
        Edit.GroupId = Group?.Value;
        Edit.NewPassword = Password.Length == 0 ? null : Password;
        if (IsEducator && Edit.GroupId is null)
        {
            App.Toasts.Error(Loc.T("Users.EducatorNeedsGroup"));
            return;
        }
        if (await Run(() => _users.SaveAsync(Edit))) Close(true);
    }
}

public sealed partial class SettingsViewModel(ISettingsService settingsService, IBackupService backups, IAppPaths paths, ICurrentUser user) : PageViewModel
{
    private bool _ready;

    public bool IsAdmin => user.Can(Permission.Settings);
    public bool CanBackup => user.Can(Permission.Backup);
    public IReadOnlyList<Option<string>> Languages => Loc.Languages;
    public List<Option<string>> Themes { get; } = [new(ThemeService.Dark, Loc.T("Settings.ThemeDark")), new(ThemeService.Light, Loc.T("Settings.ThemeLight"))];
    public List<string> Papers { get; } = ["A5", "A4"];
    public ObservableCollection<BackupInfo> Backups { get; } = [];
    public string DataFolder => paths.DataFolder;

    [ObservableProperty] public partial CrecheSettings Settings { get; set; } = new();
    [ObservableProperty] public partial string OpeningTime { get; set; } = "";
    [ObservableProperty] public partial string ClosingTime { get; set; } = "";
    [ObservableProperty] public partial Option<string>? Language { get; set; }
    [ObservableProperty] public partial Option<string>? Theme { get; set; }
    [ObservableProperty] public partial BackupInfo? SelectedBackup { get; set; }
    [ObservableProperty] public partial string Tab { get; set; } = "appearance";

    public override Task LoadAsync() => Run(async () =>
    {
        Settings = await settingsService.GetAsync();
        OpeningTime = Settings.OpeningTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClosingTime = Settings.ClosingTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        Language = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language);
        Theme = Themes.FirstOrDefault(t => t.Value == App.Themes.Current);
        string tab = App.State.Get("settings.tab", IsAdmin ? "creche" : "appearance");
        Tab = IsAdmin ? tab : "appearance";
        if (CanBackup) Backups.ReplaceWith(backups.ListBackups());
        _ready = true;
    });

    partial void OnTabChanged(string value)
    {
        if (_ready) App.State.Set("settings.tab", value);
    }

    partial void OnThemeChanged(Option<string>? value)
    {
        if (_ready && value is not null && value.Value != App.Themes.Current) App.Themes.Apply(value.Value);
    }

    partial void OnLanguageChanged(Option<string>? value)
    {
        if (!_ready || value is null || value.Value == Loc.Instance.Language) return;
        App.State.Set("language", value.Value);
        Loc.Instance.SetLanguage(value.Value);   // the shell re-creates this page in the new language
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!CollectionExtensions.TryParseTime(OpeningTime, out var opening) || !CollectionExtensions.TryParseTime(ClosingTime, out var closing))
        {
            App.Toasts.Error(Loc.T("Error.TimeInvalid"));
            return;
        }
        Settings.OpeningTime = opening;
        Settings.ClosingTime = closing;
        if (!await Run(() => settingsService.SaveAsync(Settings))) return;
        if (App.Shell is { } shell) await shell.RefreshBrandingAsync();
        App.Toasts.Success(Loc.T("Common.Saved"));
    }

    [RelayCommand]
    private void ChangePassword() => App.Dialogs.Show<ChangePasswordViewModel>(out _, false);

    [RelayCommand]
    private async Task BackupNow()
    {
        BackupInfo? info = null;
        if (!await Run(async () => info = await backups.CreateBackupAsync()) || info is null) return;
        Backups.ReplaceWith(backups.ListBackups());
        App.Toasts.Success(Loc.T("Settings.BackupDone", info.FileName));
    }

    [RelayCommand]
    private Task Restore(BackupInfo? backup)
    {
        backup ??= SelectedBackup;
        return backup is null ? Task.CompletedTask : RestoreFromAsync(backup.Path);
    }

    [RelayCommand]
    private Task RestoreFile()
    {
        string? path = App.Dialogs.OpenFile(Loc.T("Settings.BackupFilter") + "|*.db");
        return path is null ? Task.CompletedTask : RestoreFromAsync(path);
    }

    private async Task RestoreFromAsync(string path)
    {
        if (!App.Dialogs.Confirm(Loc.T("Settings.Restore"), Loc.T("Settings.RestoreConfirm", Path.GetFileName(path)), danger: true)) return;
        BackupInfo? safety = null;
        if (!await Run(async () => safety = await backups.RestoreAsync(path)) || safety is null) return;
        App.Dialogs.Message(Loc.T("Settings.Restore"), Loc.T("Settings.RestoreDone", safety.FileName));
        ((App)WpfApp.Current).Restart();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(paths.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Toasts.Error(ex.Message);
        }
    }
}
