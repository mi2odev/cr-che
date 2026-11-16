using System.Globalization;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class BillingService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, ISettingsService settings, IClock clock)
    : ServiceBase(factory, user), IBillingService
{
    public async Task<List<FeePlan>> ListFeePlansAsync(bool includeInactive = false)
    {
        User.DemandAny(Permission.Billing, Permission.Enrolment, Permission.Children, Permission.FeePlansManage);
        await using var db = await OpenAsync();
        return await db.FeePlans.AsNoTracking().Where(p => includeInactive || p.IsActive).OrderBy(p => p.Type).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<FeePlan> SaveFeePlanAsync(FeePlan plan)
    {
        User.Demand(Permission.FeePlansManage);
        string name = Required(plan.Name, "Name");
        if (plan.MonthlyAmount < 0m || plan.HourlyRate < 0m || plan.MealsAmount < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");
        await using var db = await OpenAsync();
        FeePlan target;
        if (plan.Id == 0)
        {
            target = new FeePlan();
            db.FeePlans.Add(target);
        }
        else
        {
            target = await db.FeePlans.FirstOrDefaultAsync(p => p.Id == plan.Id)
                ?? throw new BusinessRuleException("NotFound", "The fee plan no longer exists.");
        }
        target.Name = name;
        target.Type = plan.Type;
        target.MonthlyAmount = FeeCalculator.Round(plan.MonthlyAmount);
        target.HourlyRate = FeeCalculator.Round(plan.HourlyRate);
        target.IncludesMeals = plan.IncludesMeals;
        target.MealsAmount = FeeCalculator.Round(plan.MealsAmount);
        target.IsActive = plan.IsActive;
        await db.SaveChangesAsync();
        return target;
    }

    public async Task<List<ExtraChargeItem>> ListExtraChargesAsync(bool pendingOnly = true, int? childId = null)
    {
        User.Demand(Permission.Billing);
        await using var db = await OpenAsync();
        var rows = await db.ExtraCharges.AsNoTracking()
            .Where(c => !c.IsCancelled && (!pendingOnly || c.InvoiceId == null) && (childId == null || c.ChildId == childId))
            .OrderByDescending(c => c.Date).ThenByDescending(c => c.Id)
            .Select(c => new { c.Id, c.ChildId, c.Child!.FirstName, c.Child.LastName, c.Date, c.Kind, c.Description, c.Amount, c.InvoiceId, Number = c.Invoice != null ? c.Invoice.Number : null })
            .Take(500).ToListAsync();
        return rows.Select(c => new ExtraChargeItem(c.Id, c.ChildId, $"{c.FirstName} {c.LastName}".Trim(), c.Date, c.Kind, c.Description, c.Amount, c.InvoiceId != null, c.Number)).ToList();
    }

    public async Task<ExtraCharge> AddExtraChargeAsync(int childId, DateOnly date, ChargeKind kind, string description, decimal amount)
    {
        User.Demand(Permission.Billing);
        if (amount <= 0m)
            throw new BusinessRuleException("AmountInvalid", "The amount must be greater than zero.");
        if (kind is ChargeKind.MonthlyFee or ChargeKind.SiblingDiscount or ChargeKind.RegistrationFee)
            throw new BusinessRuleException("ChargeKindInvalid", "This kind of charge is created automatically.");
        await using var db = await OpenAsync();
        if (!await db.Children.AnyAsync(c => c.Id == childId && !c.IsArchived))
            throw new BusinessRuleException("NotFound", "The child no longer exists.");
        var charge = new ExtraCharge { ChildId = childId, Date = date, Kind = kind, Description = (description ?? "").Trim(), Amount = FeeCalculator.Round(amount) };
        db.ExtraCharges.Add(charge);
        await db.SaveChangesAsync();
        return charge;
    }

    public async Task CancelExtraChargeAsync(int id)
    {
        User.Demand(Permission.Billing);
        await using var db = await OpenAsync();
        var charge = await db.ExtraCharges.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The charge no longer exists.");
        if (charge.InvoiceId is not null)
            throw new BusinessRuleException("ChargeAlreadyBilled", "This charge is already on an invoice.");
        charge.IsCancelled = true;
        await db.SaveChangesAsync();
    }

    public async Task<GenerationResult> GenerateMonthlyInvoicesAsync(int year, int month)
    {
        User.Demand(Permission.Billing);
        var config = await settings.GetAsync();
        var period = new DateOnly(year, month, 1);
        var periodEnd = period.AddMonths(1).AddDays(-1);
        var today = clock.Today;
        if (period > new DateOnly(today.Year, today.Month, 1).AddMonths(1))
            throw new BusinessRuleException("PeriodTooFar", "Invoices can be generated at most one month ahead.");

        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var children = (await db.Children.AsNoTracking().Include(c => c.FeePlan)
                .Where(c => !c.IsArchived && c.Status != ChildStatus.WaitingList).ToListAsync())
            .Where(c => BillingRules.IsBillableFor(c.Status, c.EnrolmentDate, c.LeaveDate, period))
            .OrderBy(c => c.FamilyId).ThenBy(c => c.DateOfBirth).ToList();
        var alreadyBilled = (await db.Invoices.Where(i => i.Period == period && i.Status != InvoiceStatus.Cancelled).Select(i => i.ChildId).ToListAsync()).ToHashSet();

        var dueDate = BillingRules.DueDate(period, config.PaymentDueDay);
        if (dueDate < today) dueDate = today.AddDays(7);

        int created = 0, skipped = 0;
        decimal total = 0m;
        var families = new HashSet<int>();
        foreach (var child in children)
        {
            if (alreadyBilled.Contains(child.Id)) { skipped++; continue; }

            var lines = new List<InvoiceLine>();
            var fee = await BillingHelper.ComputeFeeAsync(db, config, child, period);
            if (fee is not null)
            {
                string label = child.FeePlan?.Name ?? "";
                lines.Add(new InvoiceLine { Kind = ChargeKind.MonthlyFee, Description = label, UnitPrice = fee.BaseFee, Amount = fee.BaseFee });
                if (fee.Meals > 0m)
                    lines.Add(new InvoiceLine { Kind = ChargeKind.Meals, Description = label, UnitPrice = fee.Meals, Amount = fee.Meals });
                if (fee.SiblingDiscount > 0m)
                    lines.Add(new InvoiceLine
                    {
                        Kind = ChargeKind.SiblingDiscount,
                        Description = string.Create(CultureInfo.InvariantCulture, $"-{fee.DiscountPercent:0.##} %"),
                        UnitPrice = -fee.SiblingDiscount,
                        Amount = -fee.SiblingDiscount,
                    });
            }

            var extras = await db.ExtraCharges
                .Where(c => c.ChildId == child.Id && !c.IsCancelled && c.InvoiceId == null && c.Date <= periodEnd)
                .OrderBy(c => c.Date).ToListAsync();
            lines.AddRange(extras.Select(c => new InvoiceLine { Kind = c.Kind, Description = c.Description, UnitPrice = c.Amount, Amount = c.Amount }));

            if (lines.Count == 0 || lines.Sum(l => l.Amount) <= 0m) { skipped++; continue; }

            var invoice = new Invoice
            {
                FamilyId = child.FamilyId,
                ChildId = child.Id,
                Period = period,
                IssueDate = today,
                DueDate = dueDate,
                Lines = lines,
                Total = lines.Sum(l => l.Amount),
                Status = InvoiceStatus.Unpaid,
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            invoice.Number = BillingHelper.InvoiceNumber(period.Year, invoice.Id);
            foreach (var extra in extras) extra.InvoiceId = invoice.Id;
            await db.SaveChangesAsync();

            created++;
            total += invoice.Total;
            families.Add(child.FamilyId);
        }

        // Money paid in advance settles the new invoices straight away.
        foreach (int familyId in families)
            await BillingHelper.ApplyCreditAsync(db, familyId);

        await tx.CommitAsync();
        return new GenerationResult(created, skipped, total);
    }

    public async Task<List<InvoiceListItem>> ListInvoicesAsync(InvoiceFilter filter)
    {
        User.DemandAny(Permission.Billing, Permission.Payments);
        await using var db = await OpenAsync();
        var query = db.Invoices.AsNoTracking().AsQueryable();
        if (!filter.IncludeCancelled && filter.Status != InvoiceStatus.Cancelled) query = query.Where(i => i.Status != InvoiceStatus.Cancelled);
        if (filter.Period is { } period) query = query.Where(i => i.Period == period);
        if (filter.FamilyId is { } familyId) query = query.Where(i => i.FamilyId == familyId);
        if (filter.Status is { } status) query = query.Where(i => i.Status == status);
        if (filter.OpenOnly) query = query.Where(i => i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string like = Like(filter.Search);
            query = query.Where(i => EF.Functions.Like(i.Number, like) || EF.Functions.Like(i.Family!.Name, like)
                                     || EF.Functions.Like(i.Child!.FirstName + " " + i.Child.LastName, like));
        }
        var today = clock.Today;
        var rows = await query.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id).Take(2000)
            .Select(i => new { i.Id, i.Number, i.FamilyId, FamilyName = i.Family!.Name, i.ChildId, i.Child!.FirstName, i.Child.LastName, i.Period, i.IssueDate, i.DueDate, i.Total, i.PaidAmount, i.Status })
            .ToListAsync();
        return rows.Select(i => new InvoiceListItem(i.Id, i.Number, i.FamilyId, i.FamilyName, i.ChildId, $"{i.FirstName} {i.LastName}".Trim(),
            i.Period, i.IssueDate, i.DueDate, i.Total, i.PaidAmount, i.Status,
            i.Status is InvoiceStatus.Unpaid or InvoiceStatus.PartiallyPaid && i.DueDate < today)).ToList();
    }

    public async Task<Invoice?> GetInvoiceAsync(int id)
    {
        User.DemandAny(Permission.Billing, Permission.Payments);
        await using var db = await OpenAsync();
        return await db.Invoices.AsNoTracking().AsSplitQuery()
            .Include(i => i.Lines).Include(i => i.Family).Include(i => i.Child)
            .Include(i => i.Allocations).ThenInclude(a => a.Payment)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task CancelInvoiceAsync(int id)
    {
        User.Demand(Permission.Billing);
        await using var db = await OpenAsync();
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The invoice no longer exists.");
        if (invoice.Status == InvoiceStatus.Cancelled) return;
        if (invoice.PaidAmount > 0m)
            throw new BusinessRuleException("InvoiceHasPayments", "An invoice with payments cannot be cancelled. Void the payments first.");
        invoice.Status = InvoiceStatus.Cancelled;
        // Extra charges go back to "pending" so they are billed on the next invoice.
        foreach (var charge in await db.ExtraCharges.Where(c => c.InvoiceId == id).ToListAsync())
            charge.InvoiceId = null;
        await db.SaveChangesAsync();
    }
}
