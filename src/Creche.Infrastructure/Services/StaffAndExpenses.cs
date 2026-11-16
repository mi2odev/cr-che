using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class StaffService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock) : ServiceBase(factory, user), IStaffService
{
    public async Task<List<Staff>> ListAsync(bool includeInactive = false)
    {
        User.Demand(Permission.Staff);
        await using var db = await OpenAsync();
        return await db.Staff.AsNoTracking().Include(s => s.Group)
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ToListAsync();
    }

    public async Task<Staff> SaveAsync(Staff staff)
    {
        User.Demand(Permission.Staff);
        string firstName = Required(staff.FirstName, "FirstName");
        string lastName = Required(staff.LastName, "LastName");
        if (staff.MonthlySalary < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");
        await using var db = await OpenAsync();
        Staff target;
        if (staff.Id == 0)
        {
            target = new Staff();
            db.Staff.Add(target);
        }
        else
        {
            target = await db.Staff.FirstOrDefaultAsync(s => s.Id == staff.Id)
                ?? throw new BusinessRuleException("NotFound", "The employee no longer exists.");
        }
        target.FirstName = firstName;
        target.LastName = lastName;
        target.Role = staff.Role;
        target.Phone = Clean(staff.Phone);
        target.Address = Clean(staff.Address);
        target.GroupId = staff.GroupId;
        target.MonthlySalary = FeeCalculator.Round(staff.MonthlySalary);
        target.HireDate = staff.HireDate;
        target.Notes = Clean(staff.Notes);
        target.IsActive = staff.IsActive;
        await db.SaveChangesAsync();
        return target;
    }

    public async Task SetActiveAsync(int id, bool active)
    {
        User.Demand(Permission.Staff);
        await using var db = await OpenAsync();
        await db.Staff.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, active));
    }

    public async Task<List<StaffAttendanceItem>> GetAttendanceAsync(DateOnly date)
    {
        User.Demand(Permission.Staff);
        await using var db = await OpenAsync();
        var staff = await db.Staff.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ToListAsync();
        var records = await db.StaffAttendance.AsNoTracking().Where(a => a.Date == date).ToDictionaryAsync(a => a.StaffId);
        return staff.Select(s =>
        {
            records.TryGetValue(s.Id, out var r);
            return new StaffAttendanceItem(s.Id, s.FullName, s.Role, r?.Status, r?.CheckIn, r?.CheckOut, r?.Notes);
        }).ToList();
    }

    public async Task MarkAttendanceAsync(int staffId, DateOnly date, StaffAttendanceStatus status, TimeOnly? checkIn, TimeOnly? checkOut, string? notes = null)
    {
        User.Demand(Permission.Staff);
        if (date > clock.Today)
            throw new BusinessRuleException("AttendanceFuture", "Attendance cannot be recorded for a future date.");
        if (status == StaffAttendanceStatus.Present && checkIn is { } start && checkOut is { } end && end < start)
            throw new BusinessRuleException("CheckOutBeforeCheckIn", "Check-out time cannot be earlier than check-in time.");
        await using var db = await OpenAsync();
        if (!await db.Staff.AnyAsync(s => s.Id == staffId))
            throw new BusinessRuleException("NotFound", "The employee no longer exists.");
        var record = await db.StaffAttendance.FirstOrDefaultAsync(a => a.StaffId == staffId && a.Date == date);
        if (record is null)
        {
            record = new StaffAttendance { StaffId = staffId, Date = date };
            db.StaffAttendance.Add(record);
        }
        record.Status = status;
        record.CheckIn = status == StaffAttendanceStatus.Present ? checkIn : null;
        record.CheckOut = status == StaffAttendanceStatus.Present ? checkOut : null;
        record.Notes = Clean(notes);
        await db.SaveChangesAsync();
    }
}

public sealed class ExpenseService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock) : ServiceBase(factory, user), IExpenseService
{
    public async Task<List<Expense>> ListAsync(DateOnly from, DateOnly to, ExpenseCategory? category = null)
    {
        User.Demand(Permission.Expenses);
        await using var db = await OpenAsync();
        return await db.Expenses.AsNoTracking()
            .Where(e => !e.IsDeleted && e.Date >= from && e.Date <= to && (category == null || e.Category == category))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).ToListAsync();
    }

    public async Task<Expense> SaveAsync(Expense expense)
    {
        User.Demand(Permission.Expenses);
        if (expense.Amount <= 0m)
            throw new BusinessRuleException("AmountInvalid", "The amount must be greater than zero.");
        string description = Required(expense.Description, "Description");
        await using var db = await OpenAsync();
        Expense target;
        if (expense.Id == 0)
        {
            target = new Expense();
            db.Expenses.Add(target);
        }
        else
        {
            target = await db.Expenses.FirstOrDefaultAsync(e => e.Id == expense.Id && !e.IsDeleted)
                ?? throw new BusinessRuleException("NotFound", "The expense no longer exists.");
        }
        target.Date = expense.Date;
        target.Category = expense.Category;
        target.Description = description;
        target.Amount = FeeCalculator.Round(expense.Amount);
        target.StaffId = expense.StaffId;
        await db.SaveChangesAsync();
        return target;
    }

    public async Task DeleteAsync(int id)
    {
        User.Demand(Permission.Expenses);
        await using var db = await OpenAsync();
        await db.Expenses.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDeleted, true));
    }

    public async Task<int> RecordSalariesAsync(int year, int month)
    {
        User.Demand(Permission.Expenses);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var date = last < clock.Today ? last : clock.Today;
        await using var db = await OpenAsync();
        var staff = await db.Staff.AsNoTracking().Where(s => s.IsActive).ToListAsync();
        var paid = (await db.Expenses.Where(e => !e.IsDeleted && e.Category == ExpenseCategory.Salaries && e.StaffId != null && e.Date >= first && e.Date <= last)
            .Select(e => e.StaffId!.Value).ToListAsync()).ToHashSet();
        int added = 0;
        foreach (var employee in staff.Where(s => s.MonthlySalary > 0m && !paid.Contains(s.Id)))
        {
            db.Expenses.Add(new Expense
            {
                Date = date,
                Category = ExpenseCategory.Salaries,
                Description = $"{employee.FullName} — {year}-{month:D2}",
                Amount = employee.MonthlySalary,
                StaffId = employee.Id,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    public async Task<ProfitSummary> GetProfitAsync(DateOnly from, DateOnly to)
    {
        User.DemandAny(Permission.Expenses, Permission.Reports);
        await using var db = await OpenAsync();
        var received = await db.Payments.AsNoTracking().Where(p => !p.IsVoided && p.Date >= from && p.Date <= to).Select(p => p.Amount).ToListAsync();
        var spent = await db.Expenses.AsNoTracking().Where(e => !e.IsDeleted && e.Date >= from && e.Date <= to).Select(e => e.Amount).ToListAsync();
        return new ProfitSummary(from, to, received.Sum(), spent.Sum());
    }
}
