using System.Linq.Expressions;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public abstract class ServiceBase(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user)
{
    protected ICurrentUser User { get; } = user;
    protected Task<CrecheDbContext> OpenAsync() => factory.CreateDbContextAsync();

    protected static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessRuleException("FieldRequired", $"'{field}' is required.", field);
        return value.Trim();
    }

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected static string Like(string search) => $"%{search.Trim()}%";
}

internal static class Projections
{
    public static readonly Expression<Func<Child, ChildListItem>> ChildListItem = c => new ChildListItem(
        c.Id, c.FirstName, c.LastName, c.DateOfBirth, c.Sex, c.Status,
        c.GroupId, c.Group != null ? c.Group.Name : null, c.FamilyId, c.Family!.Name,
        c.Family.Guardians.Where(g => !g.IsArchived).OrderByDescending(g => g.IsPrimary).ThenBy(g => g.Id).Select(g => g.Phone).FirstOrDefault(),
        c.EnrolmentDate, c.LeaveDate, c.SchoolYear, c.FeePlan != null ? c.FeePlan.Name : null,
        c.PhotoFile, c.Allergies != null && c.Allergies != "", c.IsArchived);
}

internal static class CapacityGuard
{
    /// <summary>Applies the group-capacity policy before an active child is placed in <paramref name="groupId"/>.</summary>
    public static async Task EnsureAsync(CrecheDbContext db, CrecheSettings settings, int groupId, bool allowOverCapacity, int? excludeChildId = null)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && !g.IsArchived)
            ?? throw new BusinessRuleException("GroupNotFound", "The group does not exist.");
        int occupied = await db.Children.CountAsync(c => c.GroupId == groupId && c.Status == ChildStatus.Active && !c.IsArchived && c.Id != excludeChildId);
        GroupRules.EnsureCanAdd(group.Name, group.Capacity, occupied, settings.BlockWhenGroupFull, allowOverCapacity);
    }
}

internal static class BillingHelper
{
    public static string InvoiceNumber(int year, int id) => $"F-{year}-{id:D5}";
    public static string ReceiptNumber(int year, int id) => $"R-{year}-{id:D5}";

    /// <summary>Fee for one child and month: plan, hours (hourly plans) and sibling rank among the family's billable children.</summary>
    public static async Task<FeeBreakdown?> ComputeFeeAsync(CrecheDbContext db, CrecheSettings settings, Child child, DateOnly periodStart)
    {
        var plan = child.FeePlan ?? (child.FeePlanId is { } planId ? await db.FeePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId) : null);
        if (plan is null && child.CustomMonthlyFee is null) return null;

        var siblings = await db.Children.AsNoTracking()
            .Where(c => c.FamilyId == child.FamilyId && !c.IsArchived && c.Status != ChildStatus.WaitingList)
            .Select(c => new { c.Id, c.DateOfBirth, c.Status, c.EnrolmentDate, c.LeaveDate })
            .ToListAsync();
        var ranks = FeeCalculator.RankSiblings(siblings
            .Where(s => s.Id == child.Id || BillingRules.IsBillableFor(s.Status, s.EnrolmentDate, s.LeaveDate, periodStart))
            .Select(s => (s.Id, s.DateOfBirth)));

        decimal hours = 0m;
        if (plan?.Type == FeePlanType.Hourly)
        {
            var periodEnd = periodStart.AddMonths(1).AddDays(-1);
            var times = await db.Attendance.AsNoTracking()
                .Where(a => a.ChildId == child.Id && !a.IsDeleted && a.Status == AttendanceStatus.Present && a.Date >= periodStart && a.Date <= periodEnd)
                .Select(a => new { a.CheckIn, a.CheckOut })
                .ToListAsync();
            hours = times.Sum(t => AttendanceRules.HoursAttended(t.CheckIn, t.CheckOut));
        }

        return FeeCalculator.Calculate(new FeeInput(
            plan?.Type ?? FeePlanType.FullDay, plan?.MonthlyAmount ?? 0m, plan?.HourlyRate ?? 0m,
            plan?.IncludesMeals ?? false, plan?.MealsAmount ?? 0m, child.CustomMonthlyFee, hours,
            ranks.GetValueOrDefault(child.Id, 1), settings.SecondChildDiscountPercent, settings.ThirdChildDiscountPercent));
    }

    /// <summary>Adds a single-line invoice (registration / re-enrolment fee). The caller saves and commits.</summary>
    public static async Task<Invoice> AddOneOffInvoiceAsync(CrecheDbContext db, Child child, ChargeKind kind, string description, decimal amount, DateOnly today)
    {
        amount = FeeCalculator.Round(amount);
        var invoice = new Invoice
        {
            FamilyId = child.FamilyId,
            ChildId = child.Id,
            Period = null,
            IssueDate = today,
            DueDate = today.AddDays(7),
            Total = amount,
            Status = BillingRules.StatusFor(amount, 0m),
            Lines = [new InvoiceLine { Kind = kind, Description = description, Quantity = 1m, UnitPrice = amount, Amount = amount }],
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        invoice.Number = InvoiceNumber(today.Year, invoice.Id);
        await db.SaveChangesAsync();
        await ApplyCreditAsync(db, child.FamilyId);
        return invoice;
    }

    /// <summary>
    /// Uses every not-yet-allocated part of the family's payments to settle its open invoices, oldest due date first.
    /// Called after a payment, after new invoices and after a void, so invoice balances are always consistent.
    /// </summary>
    public static async Task ApplyCreditAsync(CrecheDbContext db, int familyId)
    {
        var payments = await db.Payments.Include(p => p.Allocations)
            .Where(p => p.FamilyId == familyId && !p.IsVoided)
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .ToListAsync();
        var invoices = await db.Invoices
            .Where(i => i.FamilyId == familyId && (i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid))
            .OrderBy(i => i.DueDate).ThenBy(i => i.Id)
            .ToListAsync();

        foreach (var payment in payments)
        {
            decimal free = payment.Amount - payment.Allocations.Sum(a => a.Amount);
            if (free <= 0m) continue;
            var plan = BillingRules.Allocate(free, invoices.Select(i => (i.Id, i.Remaining)));
            foreach (var (invoiceId, amount) in plan.Allocations)
            {
                var invoice = invoices.First(i => i.Id == invoiceId);
                payment.Allocations.Add(new PaymentAllocation { InvoiceId = invoiceId, Amount = amount });
                invoice.PaidAmount += amount;
                invoice.Status = BillingRules.StatusFor(invoice.Total, invoice.PaidAmount);
            }
        }
        await db.SaveChangesAsync();
    }

    public static async Task<List<DebtorItem>> GetDebtorsAsync(CrecheDbContext db, DateOnly today)
    {
        var open = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid)
            .Select(i => new { i.FamilyId, i.Total, i.PaidAmount, i.DueDate })
            .ToListAsync();
        if (open.Count == 0) return [];

        var familyIds = open.Select(o => o.FamilyId).Distinct().ToList();
        var families = await db.Families.AsNoTracking()
            .Where(f => familyIds.Contains(f.Id))
            .Select(f => new
            {
                f.Id,
                f.Name,
                Guardian = f.Guardians.Where(g => !g.IsArchived).OrderByDescending(g => g.IsPrimary).ThenBy(g => g.Id)
                    .Select(g => new { g.FirstName, g.LastName, g.Phone }).FirstOrDefault(),
            })
            .ToDictionaryAsync(f => f.Id);

        return open.GroupBy(o => o.FamilyId)
            .Select(g =>
            {
                var family = families[g.Key];
                var oldest = g.Min(x => x.DueDate);
                return new DebtorItem(
                    g.Key, family.Name,
                    family.Guardian is null ? null : $"{family.Guardian.FirstName} {family.Guardian.LastName}".Trim(),
                    family.Guardian?.Phone,
                    g.Sum(x => x.Total - x.PaidAmount), oldest, Math.Max(0, today.DayNumber - oldest.DayNumber), g.Count());
            })
            .Where(d => d.AmountDue > 0m)
            .OrderByDescending(d => d.DaysLate).ThenByDescending(d => d.AmountDue)
            .ToList();
    }
}
