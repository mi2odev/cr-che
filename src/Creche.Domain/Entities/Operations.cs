using Creche.Domain.Enums;

namespace Creche.Domain.Entities;

/// <summary>One row per child per day. Never hard-deleted: mistakes are flagged with <see cref="IsDeleted"/>.</summary>
public class AttendanceRecord
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public Child? Child { get; set; }
    public DateOnly Date { get; set; }
    public AttendanceStatus Status { get; set; }
    public TimeOnly? CheckIn { get; set; }
    public TimeOnly? CheckOut { get; set; }
    public string? DroppedOffBy { get; set; }
    public string? PickedUpBy { get; set; }
    public string? AbsenceReason { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
    public int? RecordedByUserId { get; set; }
}

public class ActivityLogEntry
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public Child? Child { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? Time { get; set; }
    public ActivityType Type { get; set; }
    public string Description { get; set; } = "";
    public bool IsDeleted { get; set; }
    public int? RecordedByUserId { get; set; }
}

public class FeePlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public FeePlanType Type { get; set; }
    /// <summary>Monthly fee for full-day and half-day plans.</summary>
    public decimal MonthlyAmount { get; set; }
    /// <summary>Price per attended hour for hourly plans.</summary>
    public decimal HourlyRate { get; set; }
    public bool IncludesMeals { get; set; }
    /// <summary>Monthly meal supplement, charged when <see cref="IncludesMeals"/> is true.</summary>
    public decimal MealsAmount { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A one-off charge (late pick-up, activity, transport…) waiting to be put on the next invoice.</summary>
public class ExtraCharge
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public Child? Child { get; set; }
    public DateOnly Date { get; set; }
    public ChargeKind Kind { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public bool IsCancelled { get; set; }
}

public class Invoice
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int FamilyId { get; set; }
    public Family? Family { get; set; }
    public int ChildId { get; set; }
    public Child? Child { get; set; }
    /// <summary>First day of the billed month; null for one-off invoices such as registration fees.</summary>
    public DateOnly? Period { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Total { get; set; }
    public decimal PaidAmount { get; set; }
    public InvoiceStatus Status { get; set; }
    public string? Notes { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];
    public List<PaymentAllocation> Allocations { get; set; } = [];

    public decimal Remaining => Status == InvoiceStatus.Cancelled ? 0m : Total - PaidAmount;
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public ChargeKind Kind { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Money received from a family. Never deleted: a wrong payment is voided and stays in the history.</summary>
public class Payment
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public int FamilyId { get; set; }
    public Family? Family { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? ReceivedByUserId { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<PaymentAllocation> Allocations { get; set; } = [];
}

/// <summary>Which part of a payment settles which invoice.</summary>
public class PaymentAllocation
{
    public int Id { get; set; }
    public int PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
}

public class Expense
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public int? StaffId { get; set; }
    public Staff? Staff { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Key/value storage for application settings (stored as JSON).</summary>
public class SettingEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
