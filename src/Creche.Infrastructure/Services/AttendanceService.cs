using System.Globalization;
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

public sealed class AttendanceService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, ISettingsService settings, IClock clock)
    : ServiceBase(factory, user), IAttendanceService
{
    public async Task<List<NamedItem>> GetAccessibleGroupsAsync()
    {
        User.DemandAny(Permission.Attendance, Permission.ActivityLog);
        await using var db = await OpenAsync();
        var groups = await db.Groups.AsNoTracking().Where(g => !g.IsArchived)
            .OrderBy(g => g.MinAgeMonths).ThenBy(g => g.Name)
            .Select(g => new NamedItem(g.Id, g.Name)).ToListAsync();
        return groups.Where(g => User.CanAccessGroup(g.Id)).ToList();
    }

    public async Task<List<RollCallItem>> GetRollCallAsync(int? groupId, DateOnly date)
    {
        User.Demand(Permission.Attendance);
        User.DemandGroup(groupId);
        await using var db = await OpenAsync();

        var children = await EligibleChildren(db, date)
            .Where(c => groupId == null || c.GroupId == groupId)
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Select(c => new
            {
                c.Id, c.FirstName, c.LastName, c.PhotoFile, c.Allergies, c.GroupId,
                GroupName = c.Group != null ? c.Group.Name : null,
                Guardians = c.Family!.Guardians.Where(g => !g.IsArchived && g.CanPickUp).Select(g => g.FirstName + " " + g.LastName).ToList(),
                Others = c.AuthorizedPickups.Where(p => !p.IsArchived && p.CanPickUp).Select(p => p.FullName).ToList(),
            })
            .AsSplitQuery()
            .ToListAsync();
        var ids = children.Select(c => c.Id).ToList();
        var records = await db.Attendance.AsNoTracking()
            .Where(a => a.Date == date && !a.IsDeleted && ids.Contains(a.ChildId))
            .ToDictionaryAsync(a => a.ChildId);

        return children.Select(c =>
        {
            records.TryGetValue(c.Id, out var r);
            return new RollCallItem(c.Id, $"{c.FirstName} {c.LastName}".Trim(), c.PhotoFile, c.Allergies, c.GroupId, c.GroupName,
                r?.Status, r?.CheckIn, r?.CheckOut, r?.DroppedOffBy, r?.PickedUpBy, r?.AbsenceReason,
                c.Guardians.Concat(c.Others).ToList());
        }).ToList();
    }

    public async Task CheckInAsync(int childId, DateOnly date, TimeOnly time, string? droppedOffBy)
    {
        await using var db = await OpenAsync();
        var (child, record) = await LoadAsync(db, childId, date);
        if (record?.CheckOut is { } checkedOut && time > checkedOut)
            throw new BusinessRuleException("CheckOutBeforeCheckIn", "Check-out time cannot be earlier than check-in time.");
        record ??= Add(db, child.Id, date);
        record.Status = AttendanceStatus.Present;
        record.CheckIn = time;
        record.DroppedOffBy = Clean(droppedOffBy);
        record.AbsenceReason = null;
        record.RecordedByUserId = User.User!.Id;
        await db.SaveChangesAsync();
    }

    public async Task CheckOutAsync(int childId, DateOnly date, TimeOnly time, string? pickedUpBy)
    {
        var config = await settings.GetAsync();
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var (child, record) = await LoadAsync(db, childId, date);
        AttendanceRules.EnsureCanCheckOut(record?.Status, record?.CheckIn, time);
        record!.CheckOut = time;
        record.PickedUpBy = Clean(pickedUpBy);
        record.RecordedByUserId = User.User!.Id;
        await db.SaveChangesAsync();
        await SyncLateChargeAsync(db, config, child.Id, date, time);
        await tx.CommitAsync();
    }

    public async Task MarkAbsentAsync(int childId, DateOnly date, string? reason)
    {
        await using var db = await OpenAsync();
        var (child, record) = await LoadAsync(db, childId, date);
        AttendanceRules.EnsureAbsenceAllowed(record?.CheckOut);
        record ??= Add(db, child.Id, date);
        record.Status = AttendanceStatus.Absent;
        record.CheckIn = null;
        record.DroppedOffBy = null;
        record.AbsenceReason = Clean(reason);
        record.RecordedByUserId = User.User!.Id;
        await db.SaveChangesAsync();
    }

    public async Task ClearAsync(int childId, DateOnly date)
    {
        var config = await settings.GetAsync();
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var (child, record) = await LoadAsync(db, childId, date);
        if (record is null) return;
        record.IsDeleted = true;
        await db.SaveChangesAsync();
        await SyncLateChargeAsync(db, config, child.Id, date, null);
        await tx.CommitAsync();
    }

    public async Task<int> CheckInAllAsync(int groupId, DateOnly date, TimeOnly time)
    {
        User.Demand(Permission.Attendance);
        User.DemandGroup(groupId);
        if (date > clock.Today)
            throw new BusinessRuleException("AttendanceFuture", "Attendance cannot be recorded for a future date.");
        await using var db = await OpenAsync();
        var ids = await EligibleChildren(db, date).Where(c => c.GroupId == groupId).Select(c => c.Id).ToListAsync();
        var marked = await db.Attendance.Where(a => a.Date == date && !a.IsDeleted && ids.Contains(a.ChildId)).Select(a => a.ChildId).ToListAsync();
        var missing = ids.Except(marked).ToList();
        foreach (int id in missing)
        {
            var record = Add(db, id, date);
            record.Status = AttendanceStatus.Present;
            record.CheckIn = time;
            record.RecordedByUserId = User.User!.Id;
        }
        await db.SaveChangesAsync();
        return missing.Count;
    }

    public async Task<MonthlySheet> GetMonthlySheetAsync(int childId, int year, int month)
    {
        User.DemandAny(Permission.Attendance, Permission.Children);
        await using var db = await OpenAsync();
        var child = await db.Children.AsNoTracking().Include(c => c.Group).FirstOrDefaultAsync(c => c.Id == childId)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        if (!User.Can(Permission.Children)) User.DemandGroup(child.GroupId);

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var records = await db.Attendance.AsNoTracking()
            .Where(a => a.ChildId == childId && !a.IsDeleted && a.Date >= first && a.Date <= last)
            .ToDictionaryAsync(a => a.Date);

        var days = new List<SheetDay>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            records.TryGetValue(day, out var r);
            days.Add(new SheetDay(day, r?.Status, r?.CheckIn, r?.CheckOut, r?.DroppedOffBy, r?.PickedUpBy, r?.AbsenceReason,
                r?.Status == AttendanceStatus.Present ? AttendanceRules.HoursAttended(r.CheckIn, r.CheckOut) : 0m));
        }
        int present = days.Count(d => d.Status == AttendanceStatus.Present);
        int absent = days.Count(d => d.Status == AttendanceStatus.Absent);
        return new MonthlySheet(child.Id, child.FullName, child.Group?.Name, year, month, days, present, absent,
            days.Sum(d => d.Hours), AttendanceRules.AttendanceRate(present, absent));
    }

    /// <summary>Children who belong to the crèche on <paramref name="date"/>.</summary>
    internal static IQueryable<Child> EligibleChildren(CrecheDbContext db, DateOnly date)
        => db.Children.AsNoTracking().Where(c => !c.IsArchived && c.EnrolmentDate != null && c.EnrolmentDate <= date
            && (c.Status == ChildStatus.Active || (c.Status == ChildStatus.Left && c.LeaveDate != null && c.LeaveDate >= date)));

    private async Task<(Child Child, AttendanceRecord? Record)> LoadAsync(CrecheDbContext db, int childId, DateOnly date)
    {
        User.Demand(Permission.Attendance);
        var child = await db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId && !c.IsArchived)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        User.DemandGroup(child.GroupId);
        AttendanceRules.EnsureCanRecord(child.Status, child.EnrolmentDate, child.LeaveDate, date, clock.Today);
        var record = await db.Attendance.FirstOrDefaultAsync(a => a.ChildId == childId && a.Date == date && !a.IsDeleted);
        return (child, record);
    }

    private static AttendanceRecord Add(CrecheDbContext db, int childId, DateOnly date)
    {
        var record = new AttendanceRecord { ChildId = childId, Date = date };
        db.Attendance.Add(record);
        return record;
    }

    /// <summary>Keeps the automatic late pick-up charge for that day in line with the check-out time.</summary>
    private static async Task SyncLateChargeAsync(CrecheDbContext db, CrecheSettings config, int childId, DateOnly date, TimeOnly? checkOut)
    {
        int lateMinutes = checkOut is { } time ? FeeCalculator.LateMinutes(time, config.ClosingTime) : 0;
        decimal fee = FeeCalculator.LatePickupFee(lateMinutes, config.LatePickupRule);
        var charge = await db.ExtraCharges.FirstOrDefaultAsync(c =>
            c.ChildId == childId && c.Date == date && c.Kind == ChargeKind.LatePickup && !c.IsCancelled && c.InvoiceId == null);

        if (fee <= 0m)
        {
            if (charge is not null) charge.IsCancelled = true;
        }
        else
        {
            if (charge is null)
            {
                charge = new ExtraCharge { ChildId = childId, Date = date, Kind = ChargeKind.LatePickup };
                db.ExtraCharges.Add(charge);
            }
            charge.Amount = fee;
            charge.Description = string.Create(CultureInfo.InvariantCulture, $"{date:dd/MM/yyyy} (+{lateMinutes} min)");
        }
        await db.SaveChangesAsync();
    }
}

public sealed class ActivityLogService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock)
    : ServiceBase(factory, user), IActivityLogService
{
    public async Task<List<NamedItem>> GetChildrenAsync(int? groupId)
    {
        User.Demand(Permission.ActivityLog);
        User.DemandGroup(groupId);
        await using var db = await OpenAsync();
        var rows = await db.Children.AsNoTracking()
            .Where(c => !c.IsArchived && c.Status == ChildStatus.Active && c.ActivityLogEnabled && (groupId == null || c.GroupId == groupId))
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Select(c => new { c.Id, c.FirstName, c.LastName }).ToListAsync();
        return rows.Select(c => new NamedItem(c.Id, $"{c.FirstName} {c.LastName}".Trim())).ToList();
    }

    public async Task<List<ActivityItem>> ListAsync(DateOnly date, int? groupId, int? childId = null)
    {
        User.Demand(Permission.ActivityLog);
        User.DemandGroup(groupId);
        await using var db = await OpenAsync();
        return await Query(db.ActivityLog.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Date == date && (groupId == null || a.Child!.GroupId == groupId) && (childId == null || a.ChildId == childId)));
    }

    public async Task<List<ActivityItem>> ListForChildAsync(int childId, DateOnly from, DateOnly to)
    {
        User.DemandAny(Permission.ActivityLog, Permission.Children);
        await using var db = await OpenAsync();
        if (!User.Can(Permission.Children))
        {
            int? groupId = await db.Children.Where(c => c.Id == childId).Select(c => c.GroupId).FirstOrDefaultAsync();
            User.DemandGroup(groupId);
        }
        return await Query(db.ActivityLog.AsNoTracking().Where(a => !a.IsDeleted && a.ChildId == childId && a.Date >= from && a.Date <= to));
    }

    public async Task<int> SaveAsync(ActivityLogEntry entry)
    {
        User.Demand(Permission.ActivityLog);
        string description = Required(entry.Description, "Description");
        if (entry.Date > clock.Today)
            throw new BusinessRuleException("AttendanceFuture", "Entries cannot be recorded for a future date.");
        await using var db = await OpenAsync();
        var child = await db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == entry.ChildId && !c.IsArchived)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        User.DemandGroup(child.GroupId);
        if (!child.ActivityLogEnabled)
            throw new BusinessRuleException("ActivityLogDisabled", "The activity log is switched off for this child.");

        ActivityLogEntry target;
        if (entry.Id == 0)
        {
            target = new ActivityLogEntry { ChildId = entry.ChildId };
            db.ActivityLog.Add(target);
        }
        else
        {
            target = await db.ActivityLog.FirstOrDefaultAsync(a => a.Id == entry.Id && a.ChildId == entry.ChildId)
                ?? throw new BusinessRuleException("NotFound", "The entry no longer exists.");
        }
        target.Date = entry.Date;
        target.Time = entry.Time;
        target.Type = entry.Type;
        target.Description = description;
        target.RecordedByUserId = User.User!.Id;
        await db.SaveChangesAsync();
        return target.Id;
    }

    public async Task DeleteAsync(int id)
    {
        User.Demand(Permission.ActivityLog);
        await using var db = await OpenAsync();
        var entry = await db.ActivityLog.Include(a => a.Child).FirstOrDefaultAsync(a => a.Id == id);
        if (entry is null) return;
        User.DemandGroup(entry.Child!.GroupId);
        entry.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task<List<ActivityItem>> Query(IQueryable<ActivityLogEntry> source)
    {
        var rows = await source.OrderBy(a => a.Date).ThenBy(a => a.Time).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.ChildId, a.Child!.FirstName, a.Child.LastName, a.Date, a.Time, a.Type, a.Description })
            .ToListAsync();
        return rows.Select(a => new ActivityItem(a.Id, a.ChildId, $"{a.FirstName} {a.LastName}".Trim(), a.Date, a.Time, a.Type, a.Description)).ToList();
    }
}
