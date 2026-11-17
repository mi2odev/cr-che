using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Entities;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public sealed partial class FamiliesViewModel(IFamilyService families) : PageViewModel
{
    public ObservableCollection<FamilyListItem> Items { get; } = [];

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IncludeArchived { get; set; }
    [ObservableProperty] public partial FamilyListItem? Selected { get; set; }
    [ObservableProperty] public partial string Count { get; set; } = "";

    public override async Task LoadAsync()
    {
        await ReloadAsync();
        if (Parameter is int familyId)
        {
            Parameter = null;
            OpenFile(familyId);
        }
    }

    partial void OnSearchChanged(string value) => _ = ReloadAsync();
    partial void OnIncludeArchivedChanged(bool value) => _ = ReloadAsync();

    private Task ReloadAsync() => Run(async () =>
    {
        Items.ReplaceWith(await families.ListAsync(Search, IncludeArchived));
        Count = Loc.T("Common.Count", Items.Count);
    });

    [RelayCommand]
    private void Open(FamilyListItem? family)
    {
        family ??= Selected;
        if (family is not null) OpenFile(family.Id);
    }

    [RelayCommand]
    private void Add() => OpenFile(0);

    private void OpenFile(int familyId)
    {
        var dialog = App.Dialogs.Create<FamilyDialogViewModel>(familyId);
        App.Dialogs.Show(dialog);
        if (dialog.OpenChildId is { } childId) App.Shell?.Navigate("children", childId);
        else _ = ReloadAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        string? path = App.Dialogs.SaveFile($"families-{DateTime.Today:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Common.Family"), Loc.T("Families.Guardian"), Loc.T("Common.Phone"), Loc.T("Common.Address"), Loc.T("Nav.children"), Loc.T("Payments.AmountDue"));
        foreach (var f in Items) csv.Row(f.Name, f.PrimaryGuardian, f.Phone, f.Address, f.ChildrenNames, f.Balance);
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed partial class FamilyDialogViewModel : DialogViewModel
{
    private readonly IFamilyService _families;
    private readonly IPaymentService _payments;
    private readonly ISettingsService _settings;
    private readonly ICurrentUser _user;
    private int _familyId;

    public FamilyDialogViewModel(int familyId, IFamilyService families, IPaymentService payments, ISettingsService settings, ICurrentUser user)
    {
        _familyId = familyId;
        _families = families;
        _payments = payments;
        _settings = settings;
        _user = user;
        Title = Loc.T("Families.File");
    }

    public ObservableCollection<Guardian> Guardians { get; } = [];
    public ObservableCollection<Child> Children { get; } = [];
    public bool CanPay => _user.Can(Permission.Payments);
    public int? OpenChildId { get; private set; }

    [ObservableProperty] public partial Family Family { get; set; } = new();
    [ObservableProperty] public partial bool IsExisting { get; set; }
    [ObservableProperty] public partial string Due { get; set; } = "";
    [ObservableProperty] public partial string Credit { get; set; } = "";
    [ObservableProperty] public partial bool OwesMoney { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        IsExisting = _familyId != 0;
        if (!IsExisting) return;
        var family = await _families.GetAsync(_familyId) ?? throw new Domain.BusinessRuleException("NotFound", "The family no longer exists.");
        Family = family;
        Title = $"{Loc.T("Common.Family")} {family.Name}";
        Guardians.ReplaceWith(family.Guardians.OrderByDescending(g => g.IsPrimary));
        Children.ReplaceWith(family.Children.OrderBy(c => c.DateOfBirth));
        var balance = await _payments.GetFamilyBalanceAsync(_familyId);
        string currency = (await _settings.GetAsync()).Currency;
        Due = Money.Format(balance.Due, currency);
        Credit = balance.Credit > 0m ? Money.Format(balance.Credit, currency) : "";
        OwesMoney = balance.Due > 0m;
    });

    [RelayCommand]
    private async Task Save()
    {
        bool created = _familyId == 0;
        if (!await Run(async () => _familyId = (await _families.SaveAsync(Family)).Id)) return;
        App.Toasts.Success(Loc.T("Common.Saved"));
        if (created) await LoadAsync();   // stay open so guardians can be added
        else Close(true);
    }

    [RelayCommand]
    private async Task EditGuardian(Guardian? guardian)
    {
        var copy = guardian is null
            ? new Guardian { FamilyId = _familyId, LastName = Family.Name, IsPrimary = Guardians.Count == 0 }
            : new Guardian
            {
                Id = guardian.Id, FamilyId = guardian.FamilyId, FirstName = guardian.FirstName, LastName = guardian.LastName, Relationship = guardian.Relationship,
                Phone = guardian.Phone, Phone2 = guardian.Phone2, Email = guardian.Email, Address = guardian.Address, Job = guardian.Job,
                IsPrimary = guardian.IsPrimary, CanPickUp = guardian.CanPickUp, IsEmergencyContact = guardian.IsEmergencyContact,
            };
        if (!App.Dialogs.Show(new GuardianDialogViewModel(copy))) return;
        if (await Run(() => _families.SaveGuardianAsync(copy))) await LoadAsync();
    }

    [RelayCommand]
    private async Task ArchiveGuardian(Guardian guardian)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", guardian.FullName), danger: true)) return;
        if (await Run(() => _families.ArchiveGuardianAsync(guardian.Id))) await LoadAsync();
    }

    [RelayCommand]
    private async Task Pay()
    {
        if (App.Dialogs.Show<PaymentDialogViewModel>(out _, _familyId)) await LoadAsync();
    }

    [RelayCommand]
    private async Task Archive()
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", Family.Name), danger: true)) return;
        if (await Run(() => _families.ArchiveAsync(_familyId))) Close(true);
    }

    [RelayCommand]
    private void OpenChild(Child child)
    {
        OpenChildId = child.Id;
        Close(false);
    }
}

public sealed partial class GuardianDialogViewModel : DialogViewModel
{
    public GuardianDialogViewModel(Guardian guardian)
    {
        Guardian = guardian;
        Title = Loc.T("Families.Guardian");
    }

    public Guardian Guardian { get; }
    public List<Option<Relationship>> Relationships { get; } = Loc.Options<Relationship>();

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Guardian.FirstName) || string.IsNullOrWhiteSpace(Guardian.LastName))
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Name")));
            return;
        }
        Close(true);
    }
}

public sealed record GroupCard(GroupInfo Info, string Ages, string Occupancy, string Free, double Ratio);

public sealed partial class GroupsViewModel(IGroupService groups, ICurrentUser user) : PageViewModel
{
    public ObservableCollection<GroupCard> Items { get; } = [];
    public ObservableCollection<ChildListItem> Children { get; } = [];
    public bool CanManage => user.Can(Permission.GroupsManage);

    [ObservableProperty] public partial GroupCard? Selected { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        int? keep = Selected?.Info.Id;
        var list = await groups.ListAsync();
        Items.ReplaceWith(list.Select(g => new GroupCard(g,
            g.MaxAgeMonths > 0 ? Loc.T("Groups.AgeRange", g.MinAgeMonths, g.MaxAgeMonths) : Loc.T("Groups.AgeFrom", g.MinAgeMonths),
            $"{g.Occupied} / {g.Capacity}", g.IsFull ? Loc.T("Groups.Full") : Loc.T("Groups.FreePlaces", g.Free),
            g.Capacity == 0 ? 0 : (double)g.Occupied / g.Capacity)));
        Selected = Items.FirstOrDefault(g => g.Info.Id == keep) ?? Items.FirstOrDefault();
    });

    partial void OnSelectedChanged(GroupCard? value) => _ = Run(async () =>
        Children.ReplaceWith(value is null ? [] : await groups.GetChildrenAsync(value.Info.Id)));

    [RelayCommand]
    private async Task Edit(GroupCard? card)
    {
        var group = card is null
            ? new Group { Capacity = 10, MaxAgeMonths = 36 }
            : new Group { Id = card.Info.Id, Name = card.Info.Name, MinAgeMonths = card.Info.MinAgeMonths, MaxAgeMonths = card.Info.MaxAgeMonths, Capacity = card.Info.Capacity };
        if (App.Dialogs.Show<GroupDialogViewModel>(out _, group)) await LoadAsync();
    }

    [RelayCommand]
    private async Task Archive(GroupCard card)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Archive"), Loc.T("Common.ArchiveConfirm", card.Info.Name), danger: true)) return;
        if (await Run(() => groups.ArchiveAsync(card.Info.Id))) await LoadAsync();
    }

    [RelayCommand]
    private void OpenChild(ChildListItem? child)
    {
        if (child is not null && App.Shell?.CanNavigate("children") == true) App.Shell.Navigate("children", child.Id);
    }
}

public sealed partial class GroupDialogViewModel : DialogViewModel
{
    private readonly IGroupService _groups;

    public GroupDialogViewModel(Group group, IGroupService groups)
    {
        Group = group;
        _groups = groups;
        Title = Loc.T(group.Id == 0 ? "Groups.New" : "Groups.Edit");
    }

    public Group Group { get; }

    [RelayCommand]
    private async Task Save()
    {
        if (await Run(() => _groups.SaveAsync(Group))) Close(true);
    }
}
