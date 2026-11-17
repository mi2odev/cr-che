using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Entities;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public sealed partial class ChildrenViewModel(IChildService children, IGroupService groups) : PageViewModel
{
    private bool _ready;

    public ObservableCollection<ChildListItem> Items { get; } = [];
    public List<Option<ChildStatus?>> Statuses { get; } = Loc.OptionsWithAll<ChildStatus>();
    public ObservableCollection<Option<int?>> Groups { get; } = [];

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial Option<ChildStatus?>? Status { get; set; }
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial bool IncludeArchived { get; set; }
    [ObservableProperty] public partial bool CardView { get; set; }
    [ObservableProperty] public partial ChildListItem? Selected { get; set; }
    [ObservableProperty] public partial string Count { get; set; } = "";

    public override async Task LoadAsync()
    {
        await Run(async () =>
        {
            var list = await groups.ListAsync();
            Groups.ReplaceWith(new[] { new Option<int?>(null, Loc.T("Common.All")) }.Concat(list.Select(g => new Option<int?>(g.Id, g.Name))));
            // Restore the filters and the view the user left last time.
            var savedStatus = App.State.GetEnum<ChildStatus>("children.status");
            Status = Statuses.FirstOrDefault(s => Equals(s.Value, savedStatus ?? (App.State.Get("children.status") == "all" ? null : ChildStatus.Active))) ?? Statuses[0];
            int? savedGroup = App.State.GetInt("children.group");
            Group = Groups.FirstOrDefault(g => g.Value == savedGroup) ?? Groups[0];
            IncludeArchived = App.State.GetBool("children.archived");
            CardView = App.State.GetBool("children.cards");
            _ready = true;
        });
        await ReloadAsync();
        if (Parameter is int childId)
        {
            Parameter = null;
            OpenFile(childId);
        }
    }

    partial void OnSearchChanged(string value) => _ = ReloadAsync();
    partial void OnStatusChanged(Option<ChildStatus?>? value) => _ = ReloadAsync();
    partial void OnGroupChanged(Option<int?>? value) => _ = ReloadAsync();
    partial void OnIncludeArchivedChanged(bool value) => _ = ReloadAsync();
    partial void OnCardViewChanged(bool value) => App.State.Set("children.cards", value);

    private async Task ReloadAsync()
    {
        if (!_ready) return;
        App.State.Set("children.status", Status?.Value?.ToString() ?? "all");
        App.State.Set("children.group", Group?.Value);
        App.State.Set("children.archived", IncludeArchived);
        await Run(async () =>
        {
            Items.ReplaceWith(await children.ListAsync(new ChildFilter(Search, Status?.Value, Group?.Value, IncludeArchived)));
            Count = Loc.T("Common.Count", Items.Count);
        });
    }

    [RelayCommand]
    private void Open(ChildListItem? child)
    {
        child ??= Selected;
        if (child is not null) OpenFile(child.Id);
    }

    private void OpenFile(int childId)
    {
        var dialog = App.Dialogs.Create<ChildDialogViewModel>(childId);
        App.Dialogs.Show(dialog);
        if (dialog.OpenFamilyId is { } familyId) App.Shell?.Navigate("families", familyId);
        else _ = ReloadAsync();
    }

    [RelayCommand]
    private void Enrol()
    {
        if (App.Dialogs.Show<EnrolDialogViewModel>(out _)) _ = ReloadAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        string? path = App.Dialogs.SaveFile($"children-{DateTime.Today:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Common.LastName"), Loc.T("Common.FirstName"), Loc.T("Children.BirthDate"), Loc.T("Children.Sex"),
            Loc.T("Common.Status"), Loc.T("Common.Group"), Loc.T("Common.Family"), Loc.T("Common.Phone"), Loc.T("Children.EnrolmentDate"), Loc.T("Children.FeePlan"));
        foreach (var c in Items)
            csv.Row(c.LastName, c.FirstName, c.DateOfBirth, Loc.Enum(c.Sex), Loc.Enum(c.Status), c.GroupName, c.FamilyName, c.PrimaryPhone, c.EnrolmentDate, c.FeePlanName);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed partial class ChildDialogViewModel : DialogViewModel
{
    private readonly int _childId;
    private readonly IChildService _children;
    private readonly IGroupService _groups;
    private readonly IBillingService _billing;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ICurrentUser _user;

    public ChildDialogViewModel(int childId, IChildService children, IGroupService groups, IBillingService billing, ISettingsService settings, IAppPaths paths, ICurrentUser user)
    {
        _childId = childId;
        _children = children;
        _groups = groups;
        _billing = billing;
        _settings = settings;
        _paths = paths;
        _user = user;
        Title = Loc.T("Children.File");
    }

    public List<Option<Sex>> Sexes { get; } = Loc.Options<Sex>();
    public ObservableCollection<Option<int?>> Groups { get; } = [];
    public ObservableCollection<Option<int?>> Plans { get; } = [];
    public ObservableCollection<Guardian> Guardians { get; } = [];
    public ObservableCollection<Child> Siblings { get; } = [];
    public ObservableCollection<AuthorizedPickup> Pickups { get; } = [];
    public ObservableCollection<Vaccination> Vaccinations { get; } = [];
    public ObservableCollection<ChildDocument> Documents { get; } = [];
    public bool CanEnrol => _user.Can(Permission.Enrolment);

    /// <summary>Set when the user asked to jump to the family: the caller navigates after the dialog closes.</summary>
    public int? OpenFamilyId { get; private set; }

    [ObservableProperty] public partial Child? Child { get; set; }
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial Option<int?>? Plan { get; set; }
    [ObservableProperty] public partial string MonthlyFee { get; set; } = "";
    [ObservableProperty] public partial string NewVaccineName { get; set; } = "";
    [ObservableProperty] public partial DateTime? NewVaccineDate { get; set; }
    [ObservableProperty] public partial string Tab { get; set; } = "general";
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsWaiting { get; set; }
    [ObservableProperty] public partial bool HasLeft { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        var child = await _children.GetAsync(_childId) ?? throw new Domain.BusinessRuleException("NotFound", "The child no longer exists.");
        var groups = await _groups.ListAsync();
        var plans = await _billing.ListFeePlansAsync(includeInactive: true);
        string currency = (await _settings.GetAsync()).Currency;

        Groups.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat(groups.Select(g => new Option<int?>(g.Id, $"{g.Name}  ({g.Occupied}/{g.Capacity})"))));
        Plans.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat(plans.Where(p => p.IsActive || p.Id == child.FeePlanId).Select(p => new Option<int?>(p.Id, p.Name))));
        Child = child;
        Title = child.FullName;
        Group = Groups.FirstOrDefault(g => g.Value == child.GroupId) ?? Groups[0];
        Plan = Plans.FirstOrDefault(p => p.Value == child.FeePlanId) ?? Plans[0];
        IsActive = child.Status == ChildStatus.Active;
        IsWaiting = child.Status == ChildStatus.WaitingList;
        HasLeft = child.Status == ChildStatus.Left;
        Guardians.ReplaceWith(child.Family?.Guardians.OrderByDescending(g => g.IsPrimary) ?? Enumerable.Empty<Guardian>());
        Siblings.ReplaceWith(child.Family?.Children.Where(c => c.Id != child.Id).OrderBy(c => c.DateOfBirth) ?? Enumerable.Empty<Child>());
        Pickups.ReplaceWith(child.AuthorizedPickups);
        Vaccinations.ReplaceWith(child.Vaccinations);
        Documents.ReplaceWith(child.Documents);

        var fee = await _children.GetMonthlyFeeAsync(_childId);
        MonthlyFee = fee is null ? "—"
            : fee.SiblingDiscount > 0m ? Loc.T("Children.FeeWithDiscount", Money.Format(fee.Total, currency), fee.DiscountPercent.ToString("0.##"))
            : Money.Format(fee.Total, currency);
    });

    [RelayCommand]
    private async Task Save()
    {
        if (Child is null) return;
        Child.GroupId = Group?.Value;
        Child.FeePlanId = Plan?.Value;
        if (await RunWithCapacity(allow => _children.SaveAsync(Child, allow)))
        {
            App.Toasts.Success(Loc.T("Common.Saved"));
            Close(true);
        }
    }

    [RelayCommand]
    private async Task AddVaccination()
    {
        if (string.IsNullOrWhiteSpace(NewVaccineName)) return;
        await Run(async () =>
        {
            Vaccinations.Add(await _children.SaveVaccinationAsync(new Vaccination { ChildId = _childId, Name = NewVaccineName, Date = NewVaccineDate?.ToDateOnly() }));
            NewVaccineName = "";
            NewVaccineDate = null;
        });
    }

    [RelayCommand]
    private Task RemoveVaccination(Vaccination vaccination) => Run(async () =>
    {
        await _children.DeleteVaccinationAsync(vaccination.Id);
        Vaccinations.Remove(vaccination);
    });

    [RelayCommand]
    private async Task EditPickup(AuthorizedPickup? pickup)
    {
        var copy = pickup is null
            ? new AuthorizedPickup { ChildId = _childId }
            : new AuthorizedPickup
            {
                Id = pickup.Id, ChildId = pickup.ChildId, FullName = pickup.FullName, Phone = pickup.Phone, RelationshipNote = pickup.RelationshipNote,
                IdNumber = pickup.IdNumber, CanPickUp = pickup.CanPickUp, IsEmergencyContact = pickup.IsEmergencyContact,
            };
        if (!App.Dialogs.Show(new PickupDialogViewModel(copy))) return;
        await Run(async () =>
        {
            var saved = await _children.SavePickupAsync(copy);
            if (pickup is not null) Pickups.Remove(pickup);
            Pickups.Add(saved);
        });
    }

    [RelayCommand]
    private async Task ArchivePickup(AuthorizedPickup pickup)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", pickup.FullName), danger: true)) return;
        await Run(async () =>
        {
            await _children.ArchivePickupAsync(pickup.Id);
            Pickups.Remove(pickup);
        });
    }

    [RelayCommand]
    private async Task AddDocument()
    {
        string? path = App.Dialogs.OpenFile(Loc.T("Children.DocumentFilter") + "|*.pdf;*.jpg;*.jpeg;*.png;*.doc;*.docx;*.xls;*.xlsx;*.txt|*.*|*.*");
        if (path is null) return;
        string? title = App.Dialogs.Prompt(Loc.T("Children.AddDocument"), Loc.T("Common.Title"), Path.GetFileNameWithoutExtension(path), required: true);
        if (title is null) return;
        await Run(async () => Documents.Add(await _children.AddDocumentAsync(_childId, title, path)));
    }

    [RelayCommand]
    private void OpenDocument(ChildDocument document)
    {
        string path = Path.Combine(_paths.DocumentsFolder, Path.GetFileName(document.FileName));
        if (!File.Exists(path))
        {
            App.Toasts.Error(Loc.T("Error.FileNotFound"));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Toasts.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ArchiveDocument(ChildDocument document)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", document.Title), danger: true)) return;
        await Run(async () =>
        {
            await _children.ArchiveDocumentAsync(document.Id);
            Documents.Remove(document);
        });
    }

    [RelayCommand]
    private async Task PrintForm()
    {
        if (Child is null) return;
        var settings = await _settings.GetAsync();
        var fresh = await _children.GetAsync(_childId) ?? Child;
        App.Printer.Preview($"{Loc.T("Print.EnrolmentForm")} - {fresh.FullName}", () => PrintDocuments.EnrolmentForm(fresh, settings));
    }

    [RelayCommand]
    private void AttendanceSheet() => App.Dialogs.Show<MonthlySheetViewModel>(out _, _childId);

    [RelayCommand]
    private async Task EnrolmentAction(string mode)
    {
        if (Child is null) return;
        var action = Enum.Parse<EnrolmentAction>(mode);
        if (App.Dialogs.Show<EnrolActionViewModel>(out _, action, _childId, Child.FullName)) await LoadAsync();
    }

    [RelayCommand]
    private async Task Archive()
    {
        if (Child is null || !App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", Child.FullName), danger: true)) return;
        if (await Run(() => _children.ArchiveAsync(_childId))) Close(true);
    }

    [RelayCommand]
    private void OpenFamily()
    {
        OpenFamilyId = Child?.FamilyId;
        Close(false);
    }
}

public sealed partial class PickupDialogViewModel : DialogViewModel
{
    public PickupDialogViewModel(AuthorizedPickup pickup)
    {
        Pickup = pickup;
        Title = Loc.T("Children.Pickup");
    }

    public AuthorizedPickup Pickup { get; }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Pickup.FullName))
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Name")));
            return;
        }
        Close(true);
    }
}

public sealed partial class MonthlySheetViewModel : DialogViewModel
{
    private readonly int _childId;
    private readonly IAttendanceService _attendance;
    private readonly ISettingsService _settings;

    public MonthlySheetViewModel(int childId, IAttendanceService attendance, ISettingsService settings)
    {
        _childId = childId;
        _attendance = attendance;
        _settings = settings;
        Title = Loc.T("Attendance.MonthlySheet");
        Month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
    }

    [ObservableProperty] public partial DateOnly Month { get; set; }
    [ObservableProperty] public partial MonthlySheet? Sheet { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        Sheet = await _attendance.GetMonthlySheetAsync(_childId, Month.Year, Month.Month);
        Title = $"{Loc.T("Attendance.MonthlySheet")} — {Sheet.ChildName}";
    });

    [RelayCommand]
    private Task Shift(string months)
    {
        Month = Month.AddMonths(int.Parse(months));
        return LoadAsync();
    }

    [RelayCommand]
    private async Task Print()
    {
        if (Sheet is null) return;
        var settings = await _settings.GetAsync();
        var sheet = Sheet;
        App.Printer.Preview($"{Loc.T("Print.AttendanceSheet")} - {sheet.ChildName} - {Month:yyyy-MM}", () => PrintDocuments.AttendanceSheet(sheet, settings));
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Sheet is null) return;
        string? path = App.Dialogs.SaveFile($"attendance-{Sheet.ChildName}-{Month:yyyy-MM}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Common.Date"), Loc.T("Common.Status"), Loc.T("Attendance.CheckIn"), Loc.T("Attendance.CheckOut"),
            Loc.T("Attendance.DroppedBy"), Loc.T("Attendance.PickedBy"), Loc.T("Attendance.Hours"), Loc.T("Attendance.Reason"));
        foreach (var d in Sheet.Days)
            csv.Row(d.Date, d.Status is { } s ? Loc.Enum(s) : "", d.CheckIn, d.CheckOut, d.DroppedOffBy, d.PickedUpBy, d.Hours, d.AbsenceReason);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}
