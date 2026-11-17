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

public sealed partial class StaffAttendanceRow : ObservableObject
{
    public StaffAttendanceRow(StaffAttendanceItem item)
    {
        Item = item;
        CheckIn = item.CheckIn?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        CheckOut = item.CheckOut?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
    }

    public StaffAttendanceItem Item { get; }
    public bool IsMarked => Item.Status is not null;

    [ObservableProperty] public partial string CheckIn { get; set; }
    [ObservableProperty] public partial string CheckOut { get; set; }
}

public sealed partial class StaffViewModel(IStaffService staff, ISettingsService settings) : PageViewModel
{
    public ObservableCollection<Staff> Items { get; } = [];
    public ObservableCollection<StaffAttendanceRow> Attendance { get; } = [];

    [ObservableProperty] public partial string Tab { get; set; } = "list";
    [ObservableProperty] public partial bool IncludeInactive { get; set; }
    [ObservableProperty] public partial Staff? Selected { get; set; }
    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Payroll { get; set; } = "";

    public override async Task LoadAsync()
    {
        Tab = App.State.Get("staff.tab", "list");
        await ReloadAsync();
        if (Parameter is int staffId)
        {
            Parameter = null;
            Tab = "list";
            Selected = Items.FirstOrDefault(s => s.Id == staffId);
        }
    }

    partial void OnTabChanged(string value) => App.State.Set("staff.tab", value);
    partial void OnIncludeInactiveChanged(bool value) => _ = ReloadAsync();
    partial void OnDateChanged(DateTime value) => _ = ReloadAsync();

    private Task ReloadAsync() => Run(async () =>
    {
        var list = await staff.ListAsync(IncludeInactive);
        Items.ReplaceWith(list);
        string currency = (await settings.GetAsync()).Currency;
        Payroll = Loc.T("Staff.Payroll", list.Count(s => s.IsActive), Money.Format(list.Where(s => s.IsActive).Sum(s => s.MonthlySalary), currency));
        Attendance.ReplaceWith((await staff.GetAttendanceAsync(Date.ToDateOnly())).Select(a => new StaffAttendanceRow(a)));
    });

    [RelayCommand]
    private async Task Edit(Staff? employee)
    {
        var copy = employee is null
            ? new Staff { HireDate = DateOnly.FromDateTime(DateTime.Today), Role = StaffRole.Educator }
            : new Staff
            {
                Id = employee.Id, FirstName = employee.FirstName, LastName = employee.LastName, Role = employee.Role, Phone = employee.Phone, Address = employee.Address,
                GroupId = employee.GroupId, MonthlySalary = employee.MonthlySalary, HireDate = employee.HireDate, Notes = employee.Notes, IsActive = employee.IsActive,
            };
        if (App.Dialogs.Show<StaffDialogViewModel>(out _, copy)) await ReloadAsync();
    }

    [RelayCommand]
    private async Task ToggleActive(Staff employee)
    {
        if (employee.IsActive && !App.Dialogs.Confirm(Loc.T("Common.Disable"), Loc.T("Common.DisableConfirm", employee.FullName), danger: true)) return;
        if (await Run(() => staff.SetActiveAsync(employee.Id, !employee.IsActive))) await ReloadAsync();
    }

    [RelayCommand]
    private void ShiftDay(string days) => Date = Date.AddDays(int.Parse(days));

    [RelayCommand]
    private Task MarkPresent(StaffAttendanceRow row) => MarkAsync(row, StaffAttendanceStatus.Present);

    [RelayCommand]
    private Task MarkAbsent(StaffAttendanceRow row) => MarkAsync(row, StaffAttendanceStatus.Absent);

    [RelayCommand]
    private Task MarkLeave(StaffAttendanceRow row) => MarkAsync(row, StaffAttendanceStatus.Leave);

    private async Task MarkAsync(StaffAttendanceRow row, StaffAttendanceStatus status)
    {
        TimeOnly? checkIn = null, checkOut = null;
        if (status == StaffAttendanceStatus.Present)
        {
            if (row.CheckIn.Trim().Length > 0)
            {
                if (!CollectionExtensions.TryParseTime(row.CheckIn, out var typed)) { App.Toasts.Error(Loc.T("Error.TimeInvalid")); return; }
                checkIn = typed;
            }
            else checkIn = Date.Date == DateTime.Today ? TimeOnly.FromDateTime(DateTime.Now) : (await settings.GetAsync()).OpeningTime;
            if (row.CheckOut.Trim().Length > 0)
            {
                if (!CollectionExtensions.TryParseTime(row.CheckOut, out var typed)) { App.Toasts.Error(Loc.T("Error.TimeInvalid")); return; }
                checkOut = typed;
            }
        }
        if (await Run(() => staff.MarkAttendanceAsync(row.Item.StaffId, Date.ToDateOnly(), status, checkIn, checkOut))) await ReloadAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        string? path = App.Dialogs.SaveFile($"staff-{DateTime.Today:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Common.LastName"), Loc.T("Common.FirstName"), Loc.T("Staff.Role"), Loc.T("Common.Group"), Loc.T("Common.Phone"), Loc.T("Staff.Salary"), Loc.T("Staff.HireDate"));
        foreach (var s in Items) csv.Row(s.LastName, s.FirstName, Loc.Enum(s.Role), s.Group?.Name, s.Phone, s.MonthlySalary, s.HireDate);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed partial class StaffDialogViewModel : DialogViewModel
{
    private readonly IStaffService _staff;
    private readonly IGroupService _groups;

    public StaffDialogViewModel(Staff staff, IStaffService staffService, IGroupService groups)
    {
        Staff = staff;
        _staff = staffService;
        _groups = groups;
        HireDate = staff.HireDate?.ToDateTime();
        Title = Loc.T("Staff.File");
    }

    public Staff Staff { get; }
    public List<Option<StaffRole>> Roles { get; } = Loc.Options<StaffRole>();
    public ObservableCollection<Option<int?>> Groups { get; } = [];

    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial DateTime? HireDate { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        Groups.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat((await _groups.ListAsync()).Select(g => new Option<int?>(g.Id, g.Name))));
        Group = Groups.FirstOrDefault(g => g.Value == Staff.GroupId) ?? Groups[0];
    });

    [RelayCommand]
    private async Task Save()
    {
        Staff.GroupId = Group?.Value;
        Staff.HireDate = HireDate?.ToDateOnly();
        if (await Run(() => _staff.SaveAsync(Staff))) Close(true);
    }
}

public sealed partial class ExpensesViewModel(IExpenseService expenses, ISettingsService settings) : PageViewModel
{
    private bool _ready;

    public ObservableCollection<Expense> Items { get; } = [];
    public List<Option<ExpenseCategory?>> Categories { get; } = Loc.OptionsWithAll<ExpenseCategory>();

    [ObservableProperty] public partial DateTime? From { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] public partial DateTime? To { get; set; } = DateTime.Today;
    [ObservableProperty] public partial Option<ExpenseCategory?>? Category { get; set; }
    [ObservableProperty] public partial Expense? Selected { get; set; }
    [ObservableProperty] public partial string Received { get; set; } = "–";
    [ObservableProperty] public partial string Spent { get; set; } = "–";
    [ObservableProperty] public partial string Net { get; set; } = "–";

    public override Task LoadAsync()
    {
        var saved = App.State.GetEnum<ExpenseCategory>("expenses.category");
        Category = Categories.FirstOrDefault(c => Equals(c.Value, saved)) ?? Categories[0];
        _ready = true;
        return ReloadAsync();
    }

    partial void OnFromChanged(DateTime? value) => _ = ReloadAsync();
    partial void OnToChanged(DateTime? value) => _ = ReloadAsync();
    partial void OnCategoryChanged(Option<ExpenseCategory?>? value) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        if (!_ready || From is null || To is null) return;
        App.State.Set("expenses.category", Category?.Value?.ToString());
        await Run(async () =>
        {
            var from = From.Value.ToDateOnly();
            var to = To.Value.ToDateOnly();
            Items.ReplaceWith(await expenses.ListAsync(from, to, Category?.Value));
            var profit = await expenses.GetProfitAsync(from, to);
            string currency = (await settings.GetAsync()).Currency;
            Received = Money.Format(profit.Received, currency);
            Spent = Money.Format(profit.Spent, currency);
            Net = Money.Format(profit.Net, currency);
        });
    }

    [RelayCommand]
    private async Task Edit(Expense? expense)
    {
        var copy = expense is null
            ? new Expense { Date = DateOnly.FromDateTime(DateTime.Today), Category = Category?.Value ?? ExpenseCategory.Supplies }
            : new Expense { Id = expense.Id, Date = expense.Date, Category = expense.Category, Description = expense.Description, Amount = expense.Amount, StaffId = expense.StaffId };
        if (App.Dialogs.Show<ExpenseDialogViewModel>(out _, copy)) await ReloadAsync();
    }

    [RelayCommand]
    private async Task Delete(Expense expense)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Delete"), Loc.T("Common.DeleteConfirm"), danger: true)) return;
        if (await Run(() => expenses.DeleteAsync(expense.Id))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task Salaries()
    {
        var month = (From ?? DateTime.Today).ToDateOnly();
        if (!App.Dialogs.Confirm(Loc.T("Expenses.Salaries"), Loc.T("Expenses.SalariesConfirm", month.ToString("MMMM yyyy")))) return;
        int count = 0;
        if (!await Run(async () => count = await expenses.RecordSalariesAsync(month.Year, month.Month))) return;
        App.Toasts.Success(Loc.T("Expenses.SalariesDone", count));
        await ReloadAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        string? path = App.Dialogs.SaveFile($"expenses-{DateTime.Today:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Common.Date"), Loc.T("Expenses.Category"), Loc.T("Common.Description"), Loc.T("Common.Amount"));
        foreach (var e in Items) csv.Row(e.Date, Loc.Enum(e.Category), e.Description, e.Amount);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed partial class ExpenseDialogViewModel : DialogViewModel
{
    private readonly IExpenseService _expenses;

    public ExpenseDialogViewModel(Expense expense, IExpenseService expenses)
    {
        Expense = expense;
        _expenses = expenses;
        Date = expense.Date.ToDateTime();
        Title = Loc.T("Expenses.Expense");
    }

    public Expense Expense { get; }
    public List<Option<ExpenseCategory>> Categories { get; } = Loc.Options<ExpenseCategory>();

    [ObservableProperty] public partial DateTime? Date { get; set; }

    [RelayCommand]
    private async Task Save()
    {
        Expense.Date = (Date ?? DateTime.Today).ToDateOnly();
        if (await Run(() => _expenses.SaveAsync(Expense))) Close(true);
    }
}
