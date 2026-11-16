using Creche.Domain.Enums;

namespace Creche.Domain.Rules;

public sealed record AllocationResult(IReadOnlyList<(int InvoiceId, decimal Amount)> Allocations, decimal Unallocated);

public static class BillingRules
{
    public static InvoiceStatus StatusFor(decimal total, decimal paid, bool cancelled = false)
    {
        if (cancelled) return InvoiceStatus.Cancelled;
        if (paid <= 0m) return total <= 0m ? InvoiceStatus.Paid : InvoiceStatus.Unpaid;
        return paid >= total ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }

    /// <summary>
    /// Spreads <paramref name="amount"/> over open invoices in the order given (callers pass oldest first).
    /// Whatever is left over stays as credit on the family account.
    /// </summary>
    public static AllocationResult Allocate(decimal amount, IEnumerable<(int InvoiceId, decimal Remaining)> openInvoices)
    {
        var result = new List<(int, decimal)>();
        decimal left = amount;
        foreach (var (id, remaining) in openInvoices)
        {
            if (left <= 0m) break;
            if (remaining <= 0m) continue;
            decimal part = Math.Min(left, remaining);
            result.Add((id, part));
            left -= part;
        }
        return new AllocationResult(result, left);
    }

    public static void ValidatePaymentAmount(decimal amount)
    {
        if (amount <= 0m)
            throw new BusinessRuleException("PaymentAmountInvalid", "The payment amount must be greater than zero.");
        if (FeeCalculator.Round(amount) != amount)
            throw new BusinessRuleException("PaymentAmountInvalid", "The payment amount cannot have more than two decimals.");
    }

    public static DateOnly DueDate(DateOnly periodStart, int dueDay)
    {
        int day = Math.Clamp(dueDay, 1, DateTime.DaysInMonth(periodStart.Year, periodStart.Month));
        return new DateOnly(periodStart.Year, periodStart.Month, day);
    }

    /// <summary>A child is billed for a month when enrolled on or before its last day and not gone before its first.</summary>
    public static bool IsBillableFor(ChildStatus status, DateOnly? enrolmentDate, DateOnly? leaveDate, DateOnly periodStart)
    {
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        if (status == ChildStatus.WaitingList || enrolmentDate is null) return false;
        if (enrolmentDate > periodEnd) return false;
        if (status == ChildStatus.Left && (leaveDate is null || leaveDate < periodStart)) return false;
        return true;
    }
}
