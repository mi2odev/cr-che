using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class PaymentService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock)
    : ServiceBase(factory, user), IPaymentService
{
    public async Task<Payment> RecordPaymentAsync(PaymentRequest request)
    {
        User.Demand(Permission.Payments);
        BillingRules.ValidatePaymentAmount(request.Amount);
        if (request.Date > clock.Today)
            throw new BusinessRuleException("PaymentDateFuture", "A payment cannot be dated in the future.");

        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await db.Families.AnyAsync(f => f.Id == request.FamilyId))
            throw new BusinessRuleException("NotFound", "The family no longer exists.");

        var payment = new Payment
        {
            FamilyId = request.FamilyId,
            Date = request.Date,
            Amount = request.Amount,
            Method = request.Method,
            Reference = Clean(request.Reference),
            Notes = Clean(request.Notes),
            ReceivedByUserId = User.User!.Id,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        payment.ReceiptNumber = BillingHelper.ReceiptNumber(request.Date.Year, payment.Id);
        await db.SaveChangesAsync();
        await BillingHelper.ApplyCreditAsync(db, request.FamilyId);
        await tx.CommitAsync();
        return payment;
    }

    public async Task VoidPaymentAsync(int paymentId, string reason)
    {
        User.Demand(Permission.Payments);
        reason = Required(reason, "Reason");
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var payment = await db.Payments.Include(p => p.Allocations).ThenInclude(a => a.Invoice).FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new BusinessRuleException("NotFound", "The payment no longer exists.");
        if (payment.IsVoided)
            throw new BusinessRuleException("PaymentAlreadyVoided", "This payment is already voided.");

        foreach (var allocation in payment.Allocations.ToList())
        {
            var invoice = allocation.Invoice!;
            invoice.PaidAmount -= allocation.Amount;
            if (invoice.Status != InvoiceStatus.Cancelled)
                invoice.Status = BillingRules.StatusFor(invoice.Total, invoice.PaidAmount);
            db.PaymentAllocations.Remove(allocation);
        }
        payment.IsVoided = true;
        payment.VoidReason = reason;
        await db.SaveChangesAsync();
        // Any other advance payment of the family now covers the invoices that were reopened.
        await BillingHelper.ApplyCreditAsync(db, payment.FamilyId);
        await tx.CommitAsync();
    }

    public async Task<List<PaymentListItem>> ListPaymentsAsync(DateOnly? from = null, DateOnly? to = null, int? familyId = null, string? search = null, bool includeVoided = true)
    {
        User.Demand(Permission.Payments);
        await using var db = await OpenAsync();
        var query = db.Payments.AsNoTracking().AsQueryable();
        if (from is { } start) query = query.Where(p => p.Date >= start);
        if (to is { } end) query = query.Where(p => p.Date <= end);
        if (familyId is { } id) query = query.Where(p => p.FamilyId == id);
        if (!includeVoided) query = query.Where(p => !p.IsVoided);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string like = Like(search);
            query = query.Where(p => EF.Functions.Like(p.ReceiptNumber, like) || EF.Functions.Like(p.Family!.Name, like) || EF.Functions.Like(p.Reference!, like));
        }
        return await query.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(2000)
            .Select(p => new PaymentListItem(p.Id, p.ReceiptNumber, p.FamilyId, p.Family!.Name, p.Date, p.Amount, p.Method, p.Reference, p.Notes, p.IsVoided, p.VoidReason))
            .ToListAsync();
    }

    public async Task<ReceiptData?> GetReceiptAsync(int paymentId)
    {
        User.Demand(Permission.Payments);
        await using var db = await OpenAsync();
        var payment = await db.Payments.AsNoTracking().AsSplitQuery()
            .Include(p => p.Family!).ThenInclude(f => f.Guardians.Where(g => !g.IsArchived))
            .Include(p => p.Allocations).ThenInclude(a => a.Invoice!).ThenInclude(i => i.Child)
            .FirstOrDefaultAsync(p => p.Id == paymentId);
        if (payment is null) return null;

        string? receivedBy = payment.ReceivedByUserId is { } userId
            ? await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync()
            : null;
        var guardian = payment.Family!.Guardians.OrderByDescending(g => g.IsPrimary).ThenBy(g => g.Id).FirstOrDefault();
        var lines = payment.Allocations.OrderBy(a => a.Invoice!.DueDate)
            .Select(a => new ReceiptLine(a.Invoice!.Number, a.Invoice.Child!.FullName, a.Invoice.Period, a.Amount)).ToList();
        var balance = await BalanceAsync(db, payment.FamilyId);
        return new ReceiptData(payment.Id, payment.ReceiptNumber, payment.Date, payment.Amount, payment.Method, payment.Reference, payment.Notes,
            payment.Family.Name, guardian?.FullName, receivedBy, lines,
            payment.IsVoided ? 0m : payment.Amount - lines.Sum(l => l.Amount), balance.Due, payment.IsVoided);
    }

    public async Task<FamilyBalance> GetFamilyBalanceAsync(int familyId)
    {
        User.DemandAny(Permission.Payments, Permission.Billing, Permission.Families);
        await using var db = await OpenAsync();
        return await BalanceAsync(db, familyId);
    }

    public async Task<List<DebtorItem>> GetDebtorsAsync()
    {
        User.DemandAny(Permission.Payments, Permission.Billing);
        await using var db = await OpenAsync();
        return await BillingHelper.GetDebtorsAsync(db, clock.Today);
    }

    private static async Task<FamilyBalance> BalanceAsync(CrecheDbContext db, int familyId)
    {
        var invoiced = await db.Invoices.AsNoTracking().Where(i => i.FamilyId == familyId && i.Status != InvoiceStatus.Cancelled).Select(i => i.Total).ToListAsync();
        var paid = await db.Payments.AsNoTracking().Where(p => p.FamilyId == familyId && !p.IsVoided).Select(p => p.Amount).ToListAsync();
        return new FamilyBalance(familyId, invoiced.Sum(), paid.Sum());
    }
}
