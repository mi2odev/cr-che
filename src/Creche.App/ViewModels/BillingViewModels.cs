using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Entities;
using Creche.Domain.Enums;

namespace Creche.App.ViewModels;

public sealed partial class BillingViewModel(IBillingService billing, ISettingsService settings, ICurrentUser user) : PageViewModel
{
    private bool _ready;
    private string _currency = "";

    public ObservableCollection<InvoiceListItem> Invoices { get; } = [];
    public ObservableCollection<ExtraChargeItem> Charges { get; } = [];
    public ObservableCollection<FeePlan> Plans { get; } = [];
    public List<Option<InvoiceStatus?>> Statuses { get; } = Loc.OptionsWithAll<InvoiceStatus>();
    public bool CanManagePlans => user.Can(Permission.FeePlansManage);
    public bool CanPay => user.Can(Permission.Payments);

    [ObservableProperty] public partial string Tab { get; set; } = "invoices";
    [ObservableProperty] public partial DateOnly Month { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] public partial bool AllPeriods { get; set; }
    [ObservableProperty] public partial Option<InvoiceStatus?>? Status { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial InvoiceListItem? Selected { get; set; }
    [ObservableProperty] public partial string Totals { get; set; } = "";

    public override async Task LoadAsync()
    {
        Tab = App.State.Get("billing.tab", "invoices");
        var saved = App.State.GetEnum<InvoiceStatus>("billing.status");
        Status = Statuses.FirstOrDefault(s => Equals(s.Value, saved)) ?? Statuses[0];
        AllPeriods = App.State.GetBool("billing.allPeriods");
        _currency = (await settings.GetAsync()).Currency;
        _ready = true;
        await ReloadAsync();
    }

    partial void OnTabChanged(string value) => App.State.Set("billing.tab", value);
    partial void OnMonthChanged(DateOnly value) => _ = ReloadInvoicesAsync();
    partial void OnAllPeriodsChanged(bool value) => _ = ReloadInvoicesAsync();
    partial void OnStatusChanged(Option<InvoiceStatus?>? value) => _ = ReloadInvoicesAsync();
    partial void OnSearchChanged(string value) => _ = ReloadInvoicesAsync();

    private async Task ReloadAsync()
    {
        await ReloadInvoicesAsync();
        await Run(async () =>
        {
            Charges.ReplaceWith(await billing.ListExtraChargesAsync(pendingOnly: true));
            Plans.ReplaceWith(await billing.ListFeePlansAsync(includeInactive: true));
        });
    }

    private async Task ReloadInvoicesAsync()
    {
        if (!_ready) return;
        App.State.Set("billing.status", Status?.Value?.ToString());
        App.State.Set("billing.allPeriods", AllPeriods);
        await Run(async () =>
        {
            var list = await billing.ListInvoicesAsync(new InvoiceFilter(AllPeriods ? null : Month, Status: Status?.Value, Search: Search,
                IncludeCancelled: Status?.Value == InvoiceStatus.Cancelled));
            // "This month" also shows the one-off invoices (registration fees) issued during that month.
            if (!AllPeriods)
            {
                var oneOff = await billing.ListInvoicesAsync(new InvoiceFilter(Status: Status?.Value, Search: Search));
                var end = Month.AddMonths(1);
                list.AddRange(oneOff.Where(i => i.Period is null && i.IssueDate >= Month && i.IssueDate < end));
            }
            Invoices.ReplaceWith(list);
            Totals = Loc.T("Billing.Totals", list.Count, Money.Format(list.Sum(i => i.Total), _currency), Money.Format(list.Sum(i => i.Remaining), _currency));
        });
    }

    [RelayCommand]
    private void ShiftMonth(string months) => Month = Month.AddMonths(int.Parse(months));

    [RelayCommand]
    private async Task Generate()
    {
        string label = Month.ToString("MMMM yyyy");
        if (!App.Dialogs.Confirm(Loc.T("Billing.Generate"), Loc.T("Billing.GenerateConfirm", label))) return;
        GenerationResult? result = null;
        if (!await Run(async () => result = await billing.GenerateMonthlyInvoicesAsync(Month.Year, Month.Month)) || result is null) return;
        App.Toasts.Success(Loc.T("Billing.Generated", result.Created, Money.Format(result.TotalAmount, _currency), result.Skipped));
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task Open(InvoiceListItem? invoice)
    {
        invoice ??= Selected;
        if (invoice is null) return;
        App.Dialogs.Show<InvoiceDialogViewModel>(out _, invoice.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task AddCharge()
    {
        if (App.Dialogs.Show<ExtraChargeDialogViewModel>(out _)) await ReloadAsync();
    }

    [RelayCommand]
    private async Task CancelCharge(ExtraChargeItem charge)
    {
        if (!App.Dialogs.Confirm(Loc.T("Common.Cancel"), Loc.T("Billing.CancelChargeConfirm", charge.ChildName), danger: true)) return;
        if (await Run(() => billing.CancelExtraChargeAsync(charge.Id))) await ReloadAsync();
    }

    [RelayCommand]
    private async Task EditPlan(FeePlan? plan)
    {
        var copy = plan is null
            ? new FeePlan()
            : new FeePlan { Id = plan.Id, Name = plan.Name, Type = plan.Type, MonthlyAmount = plan.MonthlyAmount, HourlyRate = plan.HourlyRate, IncludesMeals = plan.IncludesMeals, MealsAmount = plan.MealsAmount, IsActive = plan.IsActive };
        if (App.Dialogs.Show<FeePlanDialogViewModel>(out _, copy)) await ReloadAsync();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        string? path = App.Dialogs.SaveFile($"invoices-{Month:yyyy-MM}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter().Row(Loc.T("Billing.Invoice"), Loc.T("Common.Family"), Loc.T("Common.Child"), Loc.T("Billing.Period"), Loc.T("Billing.IssueDate"),
            Loc.T("Billing.DueDate"), Loc.T("Common.Total"), Loc.T("Billing.Paid"), Loc.T("Billing.Remaining"), Loc.T("Common.Status"));
        foreach (var i in Invoices)
            csv.Row(i.Number, i.FamilyName, i.ChildName, i.Period?.ToString("yyyy-MM"), i.IssueDate, i.DueDate, i.Total, i.PaidAmount, i.Remaining, Loc.Enum(i.Status));
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public sealed record InvoiceLineRow(string Label, decimal Amount);

public sealed partial class InvoiceDialogViewModel : DialogViewModel
{
    private readonly int _invoiceId;
    private readonly IBillingService _billing;
    private readonly ISettingsService _settings;
    private readonly ICurrentUser _user;

    public InvoiceDialogViewModel(int invoiceId, IBillingService billing, ISettingsService settings, ICurrentUser user)
    {
        _invoiceId = invoiceId;
        _billing = billing;
        _settings = settings;
        _user = user;
        Title = Loc.T("Billing.Invoice");
    }

    public ObservableCollection<InvoiceLineRow> Lines { get; } = [];
    public ObservableCollection<PaymentAllocation> Allocations { get; } = [];

    [ObservableProperty] public partial Invoice? Invoice { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; }
    [ObservableProperty] public partial bool CanPay { get; set; }

    public override Task LoadAsync() => Run(async () =>
    {
        var invoice = await _billing.GetInvoiceAsync(_invoiceId) ?? throw new Domain.BusinessRuleException("NotFound", "The invoice no longer exists.");
        Invoice = invoice;
        Title = $"{Loc.T("Billing.Invoice")} {invoice.Number}";
        Lines.ReplaceWith(invoice.Lines.Select(l => new InvoiceLineRow($"{Loc.Enum(l.Kind)}  {l.Description}".Trim(), l.Amount)));
        Allocations.ReplaceWith(invoice.Allocations.OrderBy(a => a.Payment!.Date));
        bool open = invoice.Status is InvoiceStatus.Unpaid or InvoiceStatus.PartiallyPaid;
        CanCancel = invoice.Status == InvoiceStatus.Unpaid && invoice.PaidAmount == 0m && _user.Can(Permission.Billing);
        CanPay = open && _user.Can(Permission.Payments);
    });

    [RelayCommand]
    private async Task Print()
    {
        if (Invoice is null) return;
        var settings = await _settings.GetAsync();
        var invoice = Invoice;
        App.Printer.Preview($"{Loc.T("Billing.Invoice")} {invoice.Number}", () => PrintDocuments.Invoice(invoice, settings));
    }

    [RelayCommand]
    private async Task Pay()
    {
        if (Invoice is null) return;
        if (App.Dialogs.Show<PaymentDialogViewModel>(out _, Invoice.FamilyId)) await LoadAsync();
    }

    [RelayCommand]
    private async Task CancelInvoice()
    {
        if (Invoice is null || !App.Dialogs.Confirm(Loc.T("Billing.CancelInvoice"), Loc.T("Billing.CancelInvoiceConfirm", Invoice.Number), danger: true)) return;
        if (await Run(() => _billing.CancelInvoiceAsync(_invoiceId))) Close(true);
    }
}

public sealed partial class ExtraChargeDialogViewModel : DialogViewModel
{
    private readonly IBillingService _billing;
    private readonly IChildService _children;

    public ExtraChargeDialogViewModel(IBillingService billing, IChildService children)
    {
        _billing = billing;
        _children = children;
        Title = Loc.T("Billing.AddCharge");
        Kinds = new[] { ChargeKind.LatePickup, ChargeKind.Meals, ChargeKind.Activity, ChargeKind.Transport, ChargeKind.Other }
            .Select(k => new Option<ChargeKind>(k, Loc.Enum(k))).ToList();
    }

    public ObservableCollection<ChildListItem> Children { get; } = [];
    public List<Option<ChargeKind>> Kinds { get; }

    [ObservableProperty] public partial ChildListItem? Child { get; set; }
    [ObservableProperty] public partial ChargeKind Kind { get; set; } = ChargeKind.Activity;
    [ObservableProperty] public partial DateTime? Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial decimal Amount { get; set; }

    public override Task LoadAsync() => Run(async () => Children.ReplaceWith(await _children.ListAsync(new ChildFilter(Status: ChildStatus.Active))));

    [RelayCommand]
    private async Task Save()
    {
        if (Child is null)
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Child")));
            return;
        }
        if (await Run(() => _billing.AddExtraChargeAsync(Child.Id, (Date ?? DateTime.Today).ToDateOnly(), Kind, Description, Amount))) Close(true);
    }
}

public sealed partial class FeePlanDialogViewModel : DialogViewModel
{
    private readonly IBillingService _billing;

    public FeePlanDialogViewModel(FeePlan plan, IBillingService billing)
    {
        Plan = plan;
        _billing = billing;
        Title = Loc.T("Billing.FeePlan");
    }

    public FeePlan Plan { get; }
    public List<Option<FeePlanType>> Types { get; } = Loc.Options<FeePlanType>();

    [RelayCommand]
    private async Task Save()
    {
        if (await Run(() => _billing.SaveFeePlanAsync(Plan))) Close(true);
    }
}

public sealed partial class PaymentsViewModel(IPaymentService payments, ISettingsService settings) : PageViewModel
{
    private bool _ready;
    private string _currency = "";

    public ObservableCollection<PaymentListItem> Payments { get; } = [];
    public ObservableCollection<DebtorItem> Debtors { get; } = [];

    [ObservableProperty] public partial string Tab { get; set; } = "history";
    [ObservableProperty] public partial DateTime? From { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] public partial DateTime? To { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial PaymentListItem? Selected { get; set; }
    [ObservableProperty] public partial string Totals { get; set; } = "";
    [ObservableProperty] public partial string DebtTotal { get; set; } = "";

    public override async Task LoadAsync()
    {
        Tab = App.State.Get("payments.tab", "history");
        _currency = (await settings.GetAsync()).Currency;
        _ready = true;
        await ReloadAsync();
        if (Parameter is int paymentId)
        {
            Parameter = null;
            Tab = "history";
            await PrintAsync(paymentId);
        }
    }

    partial void OnTabChanged(string value) => App.State.Set("payments.tab", value);
    partial void OnFromChanged(DateTime? value) => _ = ReloadAsync();
    partial void OnToChanged(DateTime? value) => _ = ReloadAsync();
    partial void OnSearchChanged(string value) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        if (!_ready) return;
        await Run(async () =>
        {
            bool searching = !string.IsNullOrWhiteSpace(Search);
            // A search looks through the whole history, not only the selected period.
            var list = await payments.ListPaymentsAsync(searching ? null : From?.ToDateOnly(), searching ? null : To?.ToDateOnly(), search: Search);
            Payments.ReplaceWith(list);
            Totals = Loc.T("Payments.Totals", list.Count(p => !p.IsVoided), Money.Format(list.Where(p => !p.IsVoided).Sum(p => p.Amount), _currency));
            var debtors = await payments.GetDebtorsAsync();
            Debtors.ReplaceWith(debtors);
            DebtTotal = Loc.T("Payments.DebtTotal", debtors.Count, Money.Format(debtors.Sum(d => d.AmountDue), _currency));
        });
    }

    [RelayCommand]
    private async Task Add()
    {
        if (App.Dialogs.Show<PaymentDialogViewModel>(out _, 0)) await ReloadAsync();
    }

    [RelayCommand]
    private async Task PayDebtor(DebtorItem debtor)
    {
        if (App.Dialogs.Show<PaymentDialogViewModel>(out _, debtor.FamilyId)) await ReloadAsync();
    }

    [RelayCommand]
    private Task Print(PaymentListItem? payment)
    {
        payment ??= Selected;
        return payment is null ? Task.CompletedTask : PrintAsync(payment.Id);
    }

    private Task PrintAsync(int paymentId) => Run(() => ReceiptPrinter.PreviewAsync(paymentId));

    [RelayCommand]
    private async Task Void(PaymentListItem payment)
    {
        if (payment.IsVoided) return;
        string? reason = App.Dialogs.Prompt(Loc.T("Payments.Void"), Loc.T("Payments.VoidReason", payment.ReceiptNumber), required: true);
        if (reason is null) return;
        if (await Run(() => payments.VoidPaymentAsync(payment.Id, reason))) await ReloadAsync();
    }

    [RelayCommand]
    private void OpenFamily(DebtorItem debtor) => App.Shell?.Navigate("families", debtor.FamilyId);

    [RelayCommand]
    private async Task PrintDebtors()
    {
        var config = await settings.GetAsync();
        var list = Debtors.ToList();
        App.Printer.Preview(Loc.T("Payments.Debtors"), () => PrintDocuments.Debtors(list, config));
    }

    [RelayCommand]
    private void ExportCsv()
    {
        bool debtors = Tab == "debtors";
        string? path = App.Dialogs.SaveFile($"{(debtors ? "debtors" : "payments")}-{DateTime.Today:yyyy-MM-dd}.csv", "CSV|*.csv");
        if (path is null) return;
        var csv = new CsvWriter();
        if (debtors)
        {
            csv.Row(Loc.T("Common.Family"), Loc.T("Families.Guardian"), Loc.T("Common.Phone"), Loc.T("Payments.AmountDue"), Loc.T("Payments.Since"), Loc.T("Payments.DaysLate"), Loc.T("Payments.OpenInvoices"));
            foreach (var d in Debtors) csv.Row(d.FamilyName, d.Guardian, d.Phone, d.AmountDue, d.OldestDueDate, d.DaysLate, d.OpenInvoices);
        }
        else
        {
            csv.Row(Loc.T("Payments.Receipt"), Loc.T("Common.Date"), Loc.T("Common.Family"), Loc.T("Common.Amount"), Loc.T("Payments.Method"), Loc.T("Payments.Reference"), Loc.T("Common.Status"));
            foreach (var p in Payments) csv.Row(p.ReceiptNumber, p.Date, p.FamilyName, p.Amount, Loc.Enum(p.Method), p.Reference, p.IsVoided ? Loc.T("Payments.Voided") : "");
        }
        csv.Save(path);
        App.Toasts.Success(Loc.T("Common.Exported"));
    }
}

public static class ReceiptPrinter
{
    public static async Task PreviewAsync(int paymentId)
    {
        var services = App.Services;
        var payments = (IPaymentService)services.GetService(typeof(IPaymentService))!;
        var settings = await ((ISettingsService)services.GetService(typeof(ISettingsService))!).GetAsync();
        var receipt = await payments.GetReceiptAsync(paymentId);
        if (receipt is null) return;
        App.Printer.Preview($"{Loc.T("Print.Receipt")} {receipt.ReceiptNumber}", () => PrintDocuments.Receipt(receipt, settings));
    }
}

public sealed partial class PaymentDialogViewModel : DialogViewModel
{
    private readonly int _familyId;
    private readonly IPaymentService _payments;
    private readonly IFamilyService _families;
    private readonly ISettingsService _settings;
    private string _currency = "";

    public PaymentDialogViewModel(int familyId, IPaymentService payments, IFamilyService families, ISettingsService settings)
    {
        _familyId = familyId;
        _payments = payments;
        _families = families;
        _settings = settings;
        Title = Loc.T("Payments.New");
    }

    public ObservableCollection<FamilyListItem> Families { get; } = [];
    public List<Option<PaymentMethod>> Methods { get; } = Loc.Options<PaymentMethod>();

    [ObservableProperty] public partial FamilyListItem? Family { get; set; }
    [ObservableProperty] public partial decimal Amount { get; set; }
    [ObservableProperty] public partial PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    [ObservableProperty] public partial DateTime? Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string Reference { get; set; } = "";
    [ObservableProperty] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial bool PrintReceipt { get; set; } = true;
    [ObservableProperty] public partial string Due { get; set; } = "";
    [ObservableProperty] public partial string Hint { get; set; } = "";

    public override Task LoadAsync() => Run(async () =>
    {
        _currency = (await _settings.GetAsync()).Currency;
        PrintReceipt = App.State.GetBool("payments.printReceipt", true);
        var list = await _families.ListAsync();
        Families.ReplaceWith(list.OrderByDescending(f => f.Balance > 0m).ThenBy(f => f.Name));
        Family = Families.FirstOrDefault(f => f.Id == _familyId);
    });

    partial void OnFamilyChanged(FamilyListItem? value)
    {
        Due = value is null ? "" : Money.Format(value.Balance, _currency);
        if (value is not null && Amount == 0m) Amount = value.Balance;
        UpdateHint();
    }

    partial void OnAmountChanged(decimal value) => UpdateHint();

    private void UpdateHint()
    {
        if (Family is null || Amount <= 0m) Hint = "";
        else if (Amount < Family.Balance) Hint = Loc.T("Payments.HintPartial", Money.Format(Family.Balance - Amount, _currency));
        else if (Amount > Family.Balance) Hint = Loc.T("Payments.HintAdvance", Money.Format(Amount - Family.Balance, _currency));
        else Hint = Loc.T("Payments.HintFull");
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Family is null)
        {
            App.Toasts.Error(Loc.T("Error.FieldRequired", Loc.T("Common.Family")));
            return;
        }
        Payment? payment = null;
        var request = new PaymentRequest(Family.Id, Amount, Method, (Date ?? DateTime.Today).ToDateOnly(), Reference, Notes);
        if (!await Run(async () => payment = await _payments.RecordPaymentAsync(request)) || payment is null) return;
        App.State.Set("payments.printReceipt", PrintReceipt);
        App.Toasts.Success(Loc.T("Payments.Recorded", payment.ReceiptNumber));
        if (PrintReceipt) await Run(() => ReceiptPrinter.PreviewAsync(payment.Id));
        Close(true);
    }
}
