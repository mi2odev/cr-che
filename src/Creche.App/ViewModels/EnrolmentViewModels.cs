using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Services;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public enum EnrolmentAction { Admit, ReEnrol, Leave }

public sealed partial class EnrolmentViewModel(IChildService children, ISettingsService settings) : PageViewModel
{
    public ObservableCollection<ChildListItem> WaitingList { get; } = [];
    public ObservableCollection<ChildListItem> ToReEnrol { get; } = [];
    public ObservableCollection<ChildListItem> Departures { get; } = [];

    [ObservableProperty] public partial string SchoolYear { get; set; } = "";
    [ObservableProperty] public partial string Tab { get; set; } = "waiting";

    public override Task LoadAsync() => Run(async () =>
    {
        Tab = App.State.Get("enrolment.tab", "waiting");
        SchoolYear = (await settings.GetAsync()).SchoolYear;
        WaitingList.ReplaceWith(await children.ListAsync(new ChildFilter(Status: ChildStatus.WaitingList)));
        var active = await children.ListAsync(new ChildFilter(Status: ChildStatus.Active));
        ToReEnrol.ReplaceWith(active.Where(c => c.SchoolYear != SchoolYear));
        var left = await children.ListAsync(new ChildFilter(Status: ChildStatus.Left));
        Departures.ReplaceWith(left.OrderByDescending(c => c.LeaveDate).Take(100));
    });

    partial void OnTabChanged(string value) => App.State.Set("enrolment.tab", value);

    [RelayCommand]
    private async Task Enrol()
    {
        if (App.Dialogs.Show<EnrolDialogViewModel>(out _)) await LoadAsync();
    }

    [RelayCommand]
    private Task Admit(ChildListItem child) => ActAsync(EnrolmentAction.Admit, child);

    [RelayCommand]
    private Task ReEnrol(ChildListItem child) => ActAsync(EnrolmentAction.ReEnrol, child);

    [RelayCommand]
    private Task Leave(ChildListItem child) => ActAsync(EnrolmentAction.Leave, child);

    private async Task ActAsync(EnrolmentAction action, ChildListItem child)
    {
        if (App.Dialogs.Show<EnrolActionViewModel>(out _, action, child.Id, child.FullName)) await LoadAsync();
    }

    [RelayCommand]
    private void Open(ChildListItem? child)
    {
        if (child is not null) App.Shell?.Navigate("children", child.Id);
    }
}

public sealed partial class EnrolDialogViewModel : DialogViewModel
{
    private readonly IEnrolmentService _enrolment;
    private readonly IFamilyService _families;
    private readonly IGroupService _groups;
    private readonly IBillingService _billing;
    private readonly ISettingsService _settings;

    public EnrolDialogViewModel(IEnrolmentService enrolment, IFamilyService families, IGroupService groups, IBillingService billing, ISettingsService settings)
    {
        _enrolment = enrolment;
        _families = families;
        _groups = groups;
        _billing = billing;
        _settings = settings;
        Title = Loc.T("Enrolment.New");
    }

    public EnrolmentRequest Request { get; } = new() { EnrolmentDate = DateOnly.FromDateTime(DateTime.Today) };
    public List<Option<Sex>> Sexes { get; } = Loc.Options<Sex>();
    public List<Option<Relationship>> Relationships { get; } = Loc.Options<Relationship>();
    public ObservableCollection<Option<int?>> Families { get; } = [];
    public ObservableCollection<Option<int?>> Groups { get; } = [];
    public ObservableCollection<Option<int?>> Plans { get; } = [];

    [ObservableProperty] public partial DateTime? BirthDate { get; set; }
    [ObservableProperty] public partial DateTime? EnrolmentDate { get; set; } = DateTime.Today;
    [ObservableProperty] public partial bool ExistingFamily { get; set; }
    [ObservableProperty] public partial Option<int?>? Family { get; set; }
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial Option<int?>? Plan { get; set; }
    [ObservableProperty] public partial bool WaitingList { get; set; }
    [ObservableProperty] public partial decimal RegistrationFee { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        var settings = await _settings.GetAsync();
        RegistrationFee = settings.RegistrationFee;
        Families.ReplaceWith((await _families.ListAsync()).Select(f => new Option<int?>(f.Id, $"{f.Name} — {f.PrimaryGuardian}")));
        Groups.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat((await _groups.ListAsync()).Select(g => new Option<int?>(g.Id, $"{g.Name}  ({g.Occupied}/{g.Capacity})"))));
        Plans.ReplaceWith(new[] { new Option<int?>(null, "—") }.Concat((await _billing.ListFeePlansAsync()).Select(p => new Option<int?>(p.Id, p.Name))));
        Group = Groups[0];
        Plan = Plans.Count > 1 ? Plans[1] : Plans[0];
    });

    [RelayCommand]
    private async Task Save()
    {
        if (BirthDate is null)
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Children.BirthDate")));
            return;
        }
        if (ExistingFamily && Family is null)
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Family")));
            return;
        }
        Request.DateOfBirth = BirthDate.Value.ToDateOnly();
        Request.EnrolmentDate = (EnrolmentDate ?? DateTime.Today).ToDateOnly();
        Request.FamilyId = ExistingFamily ? Family?.Value : null;
        Request.GroupId = Group?.Value;
        Request.FeePlanId = Plan?.Value;
        Request.WaitingList = WaitingList;
        Request.RegistrationFee = RegistrationFee;
        bool done = await RunWithCapacity(async allow =>
        {
            Request.AllowOverCapacity = allow;
            await _enrolment.EnrolAsync(Request);
        });
        if (!done) return;
        App.Toasts.Success(Loc.T(WaitingList ? "Enrolment.AddedToWaitingList" : "Enrolment.Enrolled", $"{Request.FirstName} {Request.LastName}"));
        Close(true);
    }
}

public sealed partial class EnrolActionViewModel : DialogViewModel
{
    private readonly int _childId;
    private readonly IEnrolmentService _enrolment;
    private readonly IGroupService _groups;
    private readonly IBillingService _billing;
    private readonly ISettingsService _settings;

    public EnrolActionViewModel(EnrolmentAction action, int childId, string childName, IEnrolmentService enrolment, IGroupService groups, IBillingService billing, ISettingsService settings)
    {
        Action = action;
        _childId = childId;
        ChildName = childName;
        _enrolment = enrolment;
        _groups = groups;
        _billing = billing;
        _settings = settings;
        Title = Loc.T($"Enrolment.{action}");
    }

    public EnrolmentAction Action { get; }
    public string ChildName { get; }
    public bool IsLeave => Action == EnrolmentAction.Leave;
    public bool IsReEnrol => Action == EnrolmentAction.ReEnrol;
    public bool IsAdmit => Action == EnrolmentAction.Admit;
    public bool ShowsPlacement => !IsLeave;
    public bool ShowsDate => !IsReEnrol;
    public ObservableCollection<Option<int?>> Groups { get; } = [];
    public ObservableCollection<Option<int?>> Plans { get; } = [];

    [ObservableProperty] public partial DateTime? Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Reason { get; set; } = "";
    [ObservableProperty] public partial string SchoolYear { get; set; } = "";
    [ObservableProperty] public partial Option<int?>? Group { get; set; }
    [ObservableProperty] public partial Option<int?>? Plan { get; set; }
    [ObservableProperty] public partial decimal Fee { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        if (IsLeave) return;
        var settings = await _settings.GetAsync();
        SchoolYear = settings.SchoolYear;
        Fee = IsReEnrol ? settings.ReEnrolmentFee : settings.RegistrationFee;
        string keep = Loc.T("Enrolment.KeepCurrent");
        Groups.ReplaceWith(new[] { new Option<int?>(null, keep) }.Concat((await _groups.ListAsync()).Select(g => new Option<int?>(g.Id, $"{g.Name}  ({g.Occupied}/{g.Capacity})"))));
        Plans.ReplaceWith(new[] { new Option<int?>(null, keep) }.Concat((await _billing.ListFeePlansAsync()).Select(p => new Option<int?>(p.Id, p.Name))));
        Group = Groups[0];
        Plan = Plans[0];
    });

    [RelayCommand]
    private async Task Save()
    {
        var date = (Date ?? DateTime.Today).ToDateOnly();
        bool done = Action switch
        {
            EnrolmentAction.Leave => await Run(() => _enrolment.LeaveAsync(_childId, date, Reason)),
            EnrolmentAction.Admit => await RunWithCapacity(allow => _enrolment.AdmitFromWaitingListAsync(_childId, Group?.Value, Plan?.Value, date, Fee, allow)),
            _ => await RunWithCapacity(allow => _enrolment.ReEnrolAsync(_childId, SchoolYear, Group?.Value, Plan?.Value, Fee, allow)),
        };
        if (!done) return;
        App.Toasts.Success(Loc.T("Common.Saved"));
        Close(true);
    }
}
