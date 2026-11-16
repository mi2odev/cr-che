using Creche.Application.Common;
using Creche.Application.Settings;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;

namespace Creche.Application.Services;

// ───────────────────────── Authentication & users ─────────────────────────

public enum LoginFailure { None, InvalidCredentials, Disabled }

public sealed record LoginResult(bool Success, LoginFailure Failure, UserSession? Session);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string username, string password);
    void Logout();
    /// <summary>Changes the signed-in user's own password.</summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword);
}

public sealed record UserInfo(int Id, string Username, string DisplayName, UserRole Role, int? GroupId, string? GroupName, bool IsActive, DateTime? LastLoginAt);

public sealed class UserEdit
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Secretary;
    public int? GroupId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Required for a new user; leave empty to keep the existing password.</summary>
    public string? NewPassword { get; set; }
}

public interface IUserService
{
    Task<List<UserInfo>> ListAsync();
    Task<int> SaveAsync(UserEdit edit);
    Task SetActiveAsync(int userId, bool active);
}

// ───────────────────────── Settings & backup ─────────────────────────

public interface ISettingsService
{
    /// <summary>Readable without signing in (the login screen shows the crèche name and logo).</summary>
    Task<CrecheSettings> GetAsync();
    Task SaveAsync(CrecheSettings settings);
}

public sealed record BackupInfo(string Path, string FileName, DateTime CreatedAt, long SizeBytes, bool IsAutomatic);

public interface IBackupService
{
    Task<BackupInfo> CreateBackupAsync();
    /// <summary>Runs without a signed-in user: makes today's automatic backup if it is enabled and missing.</summary>
    Task<BackupInfo?> RunAutomaticBackupIfDueAsync();
    IReadOnlyList<BackupInfo> ListBackups();
    /// <summary>Takes a safety copy of the current database, then replaces its content with the backup.</summary>
    Task<BackupInfo> RestoreAsync(string backupPath);
    DateTime? LastBackupAt();
}

// ───────────────────────── Children, families, groups ─────────────────────────

public sealed record ChildFilter(string? Search = null, ChildStatus? Status = null, int? GroupId = null, bool IncludeArchived = false);

public sealed record ChildListItem(
    int Id, string FirstName, string LastName, DateOnly DateOfBirth, Sex Sex, ChildStatus Status,
    int? GroupId, string? GroupName, int FamilyId, string FamilyName, string? PrimaryPhone,
    DateOnly? EnrolmentDate, DateOnly? LeaveDate, string? SchoolYear, string? FeePlanName,
    string? PhotoFile, bool HasAllergies, bool IsArchived)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public interface IChildService
{
    Task<List<ChildListItem>> ListAsync(ChildFilter filter);
    /// <summary>Full file: family with guardians and siblings, vaccinations, documents, authorised pick-ups.</summary>
    Task<Child?> GetAsync(int id);
    /// <summary>Updates the child's own fields. Moving to a full group follows the group-capacity policy.</summary>
    Task SaveAsync(Child child, bool allowOverCapacity = false);
    Task ArchiveAsync(int id);
    Task<Vaccination> SaveVaccinationAsync(Vaccination vaccination);
    Task DeleteVaccinationAsync(int id);
    Task<AuthorizedPickup> SavePickupAsync(AuthorizedPickup pickup);
    Task ArchivePickupAsync(int id);
    /// <summary>Copies the file into the documents folder and attaches it to the child.</summary>
    Task<ChildDocument> AddDocumentAsync(int childId, string title, string sourceFilePath);
    Task ArchiveDocumentAsync(int id);
    Task<FeeBreakdown?> GetMonthlyFeeAsync(int childId);
}

public sealed record FamilyListItem(int Id, string Name, string? PrimaryGuardian, string? Phone, string? Address, int ChildrenCount, string ChildrenNames, decimal Balance, bool IsArchived);

public interface IFamilyService
{
    Task<List<FamilyListItem>> ListAsync(string? search = null, bool includeArchived = false);
    Task<Family?> GetAsync(int id);
    Task<Family> SaveAsync(Family family);
    Task ArchiveAsync(int id);
    Task<Guardian> SaveGuardianAsync(Guardian guardian);
    Task ArchiveGuardianAsync(int id);
}

public sealed record GroupInfo(int Id, string Name, int MinAgeMonths, int MaxAgeMonths, int Capacity, int Occupied, string Educators, bool IsArchived)
{
    public int Free => GroupRules.FreePlaces(Capacity, Occupied);
    public bool IsFull => Occupied >= Capacity;
}

public interface IGroupService
{
    Task<List<GroupInfo>> ListAsync(bool includeArchived = false);
    Task<List<ChildListItem>> GetChildrenAsync(int groupId);
    Task<Group> SaveAsync(Group group);
    Task ArchiveAsync(int id);
}

// ───────────────────────── Enrolment ─────────────────────────

public sealed class EnrolmentRequest
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public Sex Sex { get; set; }
    /// <summary>Existing family (sibling) — when null a new family is created from the guardian fields.</summary>
    public int? FamilyId { get; set; }
    public string? GuardianFirstName { get; set; }
    public string? GuardianLastName { get; set; }
    public Relationship GuardianRelationship { get; set; }
    public string? GuardianPhone { get; set; }
    public string? GuardianJob { get; set; }
    public string? Address { get; set; }
    public bool WaitingList { get; set; }
    public int? GroupId { get; set; }
    public int? FeePlanId { get; set; }
    public DateOnly EnrolmentDate { get; set; }
    public decimal RegistrationFee { get; set; }
    public string? Allergies { get; set; }
    public string? MedicalNotes { get; set; }
    public bool AllowOverCapacity { get; set; }
}

public interface IEnrolmentService
{
    Task<Child> EnrolAsync(EnrolmentRequest request);
    Task AdmitFromWaitingListAsync(int childId, int? groupId, int? feePlanId, DateOnly enrolmentDate, decimal registrationFee, bool allowOverCapacity = false);
    /// <summary>Renews the child for a new school year, optionally moving group/plan and charging a fee.</summary>
    Task ReEnrolAsync(int childId, string schoolYear, int? groupId, int? feePlanId, decimal fee, bool allowOverCapacity = false);
    Task LeaveAsync(int childId, DateOnly leaveDate, string? reason);
}

// ───────────────────────── Attendance & activity log ─────────────────────────

public sealed record RollCallItem(
    int ChildId, string FullName, string? PhotoFile, string? Allergies, int? GroupId, string? GroupName,
    AttendanceStatus? Status, TimeOnly? CheckIn, TimeOnly? CheckOut, string? DroppedOffBy, string? PickedUpBy,
    string? AbsenceReason, IReadOnlyList<string> PickupNames);

public sealed record SheetDay(DateOnly Date, AttendanceStatus? Status, TimeOnly? CheckIn, TimeOnly? CheckOut, string? DroppedOffBy, string? PickedUpBy, string? AbsenceReason, decimal Hours);

public sealed record MonthlySheet(int ChildId, string ChildName, string? GroupName, int Year, int Month, IReadOnlyList<SheetDay> Days, int PresentDays, int AbsentDays, decimal TotalHours, decimal AttendanceRate);

public sealed record NamedItem(int Id, string Name);

public interface IAttendanceService
{
    /// <summary>Groups the signed-in user may take attendance for (educators: their own group only).</summary>
    Task<List<NamedItem>> GetAccessibleGroupsAsync();
    Task<List<RollCallItem>> GetRollCallAsync(int? groupId, DateOnly date);
    Task CheckInAsync(int childId, DateOnly date, TimeOnly time, string? droppedOffBy);
    Task CheckOutAsync(int childId, DateOnly date, TimeOnly time, string? pickedUpBy);
    Task MarkAbsentAsync(int childId, DateOnly date, string? reason);
    /// <summary>Undoes a mistake; the record is kept, flagged as deleted.</summary>
    Task ClearAsync(int childId, DateOnly date);
    /// <summary>Fast roll call: checks in every child of the group who has no record yet. Returns how many.</summary>
    Task<int> CheckInAllAsync(int groupId, DateOnly date, TimeOnly time);
    Task<MonthlySheet> GetMonthlySheetAsync(int childId, int year, int month);
}

public sealed record ActivityItem(int Id, int ChildId, string ChildName, DateOnly Date, TimeOnly? Time, ActivityType Type, string Description);

public interface IActivityLogService
{
    /// <summary>Children of the group that have the activity log switched on.</summary>
    Task<List<NamedItem>> GetChildrenAsync(int? groupId);
    Task<List<ActivityItem>> ListAsync(DateOnly date, int? groupId, int? childId = null);
    Task<List<ActivityItem>> ListForChildAsync(int childId, DateOnly from, DateOnly to);
    Task<int> SaveAsync(ActivityLogEntry entry);
    Task DeleteAsync(int id);
}

// ───────────────────────── Billing & payments ─────────────────────────

public sealed record InvoiceFilter(DateOnly? Period = null, int? FamilyId = null, InvoiceStatus? Status = null, bool OpenOnly = false, string? Search = null, bool IncludeCancelled = false);

public sealed record InvoiceListItem(int Id, string Number, int FamilyId, string FamilyName, int ChildId, string ChildName, DateOnly? Period, DateOnly IssueDate, DateOnly DueDate, decimal Total, decimal PaidAmount, InvoiceStatus Status, bool IsOverdue)
{
    public decimal Remaining => Status == InvoiceStatus.Cancelled ? 0m : Total - PaidAmount;
}

public sealed record ExtraChargeItem(int Id, int ChildId, string ChildName, DateOnly Date, ChargeKind Kind, string Description, decimal Amount, bool IsBilled, string? InvoiceNumber);

public sealed record GenerationResult(int Created, int Skipped, decimal TotalAmount);

public interface IBillingService
{
    Task<List<FeePlan>> ListFeePlansAsync(bool includeInactive = false);
    Task<FeePlan> SaveFeePlanAsync(FeePlan plan);
    Task<List<ExtraChargeItem>> ListExtraChargesAsync(bool pendingOnly = true, int? childId = null);
    Task<ExtraCharge> AddExtraChargeAsync(int childId, DateOnly date, ChargeKind kind, string description, decimal amount);
    Task CancelExtraChargeAsync(int id);
    /// <summary>Creates the month's invoice for every billable child that does not have one yet.</summary>
    Task<GenerationResult> GenerateMonthlyInvoicesAsync(int year, int month);
    Task<List<InvoiceListItem>> ListInvoicesAsync(InvoiceFilter filter);
    Task<Invoice?> GetInvoiceAsync(int id);
    /// <summary>Only invoices with no payment on them can be cancelled.</summary>
    Task CancelInvoiceAsync(int id);
}

public sealed record PaymentRequest(int FamilyId, decimal Amount, PaymentMethod Method, DateOnly Date, string? Reference = null, string? Notes = null);

public sealed record PaymentListItem(int Id, string ReceiptNumber, int FamilyId, string FamilyName, DateOnly Date, decimal Amount, PaymentMethod Method, string? Reference, string? Notes, bool IsVoided, string? VoidReason);

public sealed record FamilyBalance(int FamilyId, decimal Invoiced, decimal Paid)
{
    /// <summary>Amount the family still owes (0 when in credit).</summary>
    public decimal Due => Math.Max(0m, Invoiced - Paid);
    /// <summary>Money paid in advance, not yet used by an invoice.</summary>
    public decimal Credit => Math.Max(0m, Paid - Invoiced);
}

public sealed record DebtorItem(int FamilyId, string FamilyName, string? Guardian, string? Phone, decimal AmountDue, DateOnly OldestDueDate, int DaysLate, int OpenInvoices);

public sealed record ReceiptLine(string InvoiceNumber, string ChildName, DateOnly? Period, decimal Amount);

public sealed record ReceiptData(
    int PaymentId, string ReceiptNumber, DateOnly Date, decimal Amount, PaymentMethod Method, string? Reference, string? Notes,
    string FamilyName, string? Guardian, string? ReceivedBy, IReadOnlyList<ReceiptLine> Lines, decimal Unallocated, decimal BalanceDueAfter, bool IsVoided);

public interface IPaymentService
{
    /// <summary>Records a (possibly partial) payment and settles the family's oldest invoices first.</summary>
    Task<Payment> RecordPaymentAsync(PaymentRequest request);
    Task VoidPaymentAsync(int paymentId, string reason);
    Task<List<PaymentListItem>> ListPaymentsAsync(DateOnly? from = null, DateOnly? to = null, int? familyId = null, string? search = null, bool includeVoided = true);
    Task<ReceiptData?> GetReceiptAsync(int paymentId);
    Task<FamilyBalance> GetFamilyBalanceAsync(int familyId);
    /// <summary>Families who owe money, with how much and since when.</summary>
    Task<List<DebtorItem>> GetDebtorsAsync();
}

// ───────────────────────── Staff & expenses ─────────────────────────

public sealed record StaffAttendanceItem(int StaffId, string FullName, StaffRole Role, StaffAttendanceStatus? Status, TimeOnly? CheckIn, TimeOnly? CheckOut, string? Notes);

public interface IStaffService
{
    Task<List<Staff>> ListAsync(bool includeInactive = false);
    Task<Staff> SaveAsync(Staff staff);
    Task SetActiveAsync(int id, bool active);
    Task<List<StaffAttendanceItem>> GetAttendanceAsync(DateOnly date);
    Task MarkAttendanceAsync(int staffId, DateOnly date, StaffAttendanceStatus status, TimeOnly? checkIn, TimeOnly? checkOut, string? notes = null);
}

public sealed record ProfitSummary(DateOnly From, DateOnly To, decimal Received, decimal Spent)
{
    public decimal Net => Received - Spent;
}

public interface IExpenseService
{
    Task<List<Expense>> ListAsync(DateOnly from, DateOnly to, ExpenseCategory? category = null);
    Task<Expense> SaveAsync(Expense expense);
    Task DeleteAsync(int id);
    /// <summary>Adds one salary expense per active employee for the month, skipping those already recorded.</summary>
    Task<int> RecordSalariesAsync(int year, int month);
    Task<ProfitSummary> GetProfitAsync(DateOnly from, DateOnly to);
}

// ───────────────────────── Dashboard, reports, search ─────────────────────────

public sealed record BirthdayItem(int ChildId, string FullName, DateOnly NextBirthday, int TurningAge, int DaysUntil, string? GroupName);

public enum AlertKind { OverdueFees, GroupFull, WaitingList, InvoicesNotGenerated, NoRecentBackup, AttendanceNotTaken }

public sealed record AlertItem(AlertKind Kind, string Text, int Count = 0);

public sealed record DashboardData(
    DateOnly Date, int ActiveChildren, int PresentToday, int AbsentToday, int NotMarkedToday,
    IReadOnlyList<GroupInfo> Groups, decimal FeesDue, decimal FeesOverdue, decimal ReceivedThisMonth,
    IReadOnlyList<BirthdayItem> Birthdays, IReadOnlyList<AlertItem> Alerts);

public interface IDashboardService
{
    Task<DashboardData> GetAsync();
}

public sealed record LabelAmount(string Label, decimal Amount);

public sealed record GroupCount(string GroupName, int Children, int Capacity);

public sealed record ReportData(
    DateOnly From, DateOnly To,
    decimal Invoiced, decimal Revenue, decimal Unpaid, decimal Expenses,
    int PresentDays, int AbsentDays, decimal AttendanceRate,
    int Enrolments, int Departures, int ActiveChildren,
    IReadOnlyList<GroupCount> ChildrenPerGroup,
    IReadOnlyList<(ExpenseCategory Category, decimal Amount)> ExpensesByCategory,
    IReadOnlyList<(PaymentMethod Method, decimal Amount)> RevenueByMethod,
    IReadOnlyList<DebtorItem> Debtors)
{
    public decimal NetProfit => Revenue - Expenses;
}

public interface IReportService
{
    Task<ReportData> GetAsync(DateOnly from, DateOnly to);
}

public enum SearchKind { Child, Guardian, Staff, Receipt }

public sealed record SearchHit(SearchKind Kind, int Id, string Title, string? Subtitle, int? RelatedId = null);

public interface ISearchService
{
    /// <summary>Searches only what the signed-in user's role may see.</summary>
    Task<List<SearchHit>> SearchAsync(string text, int limitPerKind = 8);
}
