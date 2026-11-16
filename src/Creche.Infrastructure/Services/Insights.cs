using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class DashboardService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock, IBackupService backups)
    : ServiceBase(factory, user), IDashboardService
{
    public async Task<DashboardData> GetAsync()
    {
        User.Demand(Permission.Dashboard);
        var today = clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        await using var db = await OpenAsync();

        var eligible = await AttendanceService.EligibleChildren(db, today).Select(c => c.Id).ToListAsync();
        var marks = await db.Attendance.AsNoTracking().Where(a => a.Date == today && !a.IsDeleted && eligible.Contains(a.ChildId)).Select(a => a.Status).ToListAsync();
        int present = marks.Count(s => s == AttendanceStatus.Present);
        int absent = marks.Count(s => s == AttendanceStatus.Absent);
        int notMarked = eligible.Count - marks.Count;
        int active = await db.Children.CountAsync(c => c.Status == ChildStatus.Active && !c.IsArchived);
        int waiting = await db.Children.CountAsync(c => c.Status == ChildStatus.WaitingList && !c.IsArchived);

        var groups = await GroupService.LoadAsync(db, includeArchived: false);

        var open = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid)
            .Select(i => new { i.FamilyId, i.Total, i.PaidAmount, i.DueDate }).ToListAsync();
        decimal due = open.Sum(i => i.Total - i.PaidAmount);
        var overdueInvoices = open.Where(i => i.DueDate < today).ToList();
        decimal overdue = overdueInvoices.Sum(i => i.Total - i.PaidAmount);
        decimal received = (await db.Payments.AsNoTracking().Where(p => !p.IsVoided && p.Date >= monthStart && p.Date <= today).Select(p => p.Amount).ToListAsync()).Sum();

        var kids = await db.Children.AsNoTracking().Where(c => c.Status == ChildStatus.Active && !c.IsArchived)
            .Select(c => new { c.Id, c.FirstName, c.LastName, c.DateOfBirth, GroupName = c.Group != null ? c.Group.Name : null }).ToListAsync();
        var birthdays = kids
            .Select(c =>
            {
                var next = NextBirthday(c.DateOfBirth, today);
                return new BirthdayItem(c.Id, $"{c.FirstName} {c.LastName}".Trim(), next, next.Year - c.DateOfBirth.Year, next.DayNumber - today.DayNumber, c.GroupName);
            })
            .Where(b => b.DaysUntil <= 30).OrderBy(b => b.DaysUntil).ThenBy(b => b.FullName).Take(8).ToList();

        var alerts = new List<AlertItem>();
        if (overdueInvoices.Count > 0)
            alerts.Add(new AlertItem(AlertKind.OverdueFees, "", overdueInvoices.Select(i => i.FamilyId).Distinct().Count()));
        foreach (var group in groups.Where(g => g.IsFull))
            alerts.Add(new AlertItem(AlertKind.GroupFull, group.Name, group.Occupied));
        if (waiting > 0) alerts.Add(new AlertItem(AlertKind.WaitingList, "", waiting));
        if (active > 0 && !await db.Invoices.AnyAsync(i => i.Period == monthStart && i.Status != InvoiceStatus.Cancelled))
            alerts.Add(new AlertItem(AlertKind.InvoicesNotGenerated, monthStart.ToString("MM/yyyy")));
        if (notMarked > 0 && eligible.Count > 0)
            alerts.Add(new AlertItem(AlertKind.AttendanceNotTaken, "", notMarked));
        var lastBackup = backups.LastBackupAt();
        if (lastBackup is null || (clock.Now - lastBackup.Value).TotalDays > 3)
            alerts.Add(new AlertItem(AlertKind.NoRecentBackup, lastBackup?.ToString("dd/MM/yyyy") ?? ""));

        return new DashboardData(today, active, present, absent, notMarked, groups, due, overdue, received, birthdays, alerts);
    }

    internal static DateOnly NextBirthday(DateOnly birth, DateOnly today)
    {
        DateOnly InYear(int year) => new(year, birth.Month, Math.Min(birth.Day, DateTime.DaysInMonth(year, birth.Month)));
        var candidate = InYear(today.Year);
        return candidate < today ? InYear(today.Year + 1) : candidate;
    }
}

public sealed class ReportService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IClock clock) : ServiceBase(factory, user), IReportService
{
    public async Task<ReportData> GetAsync(DateOnly from, DateOnly to)
    {
        User.Demand(Permission.Reports);
        if (to < from) (from, to) = (to, from);
        await using var db = await OpenAsync();

        var invoiced = await db.Invoices.AsNoTracking().Where(i => i.Status != InvoiceStatus.Cancelled && i.IssueDate >= from && i.IssueDate <= to).Select(i => i.Total).ToListAsync();
        var payments = await db.Payments.AsNoTracking().Where(p => !p.IsVoided && p.Date >= from && p.Date <= to).Select(p => new { p.Method, p.Amount }).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => !e.IsDeleted && e.Date >= from && e.Date <= to).Select(e => new { e.Category, e.Amount }).ToListAsync();
        var debtors = await BillingHelper.GetDebtorsAsync(db, clock.Today);

        var attendance = await db.Attendance.AsNoTracking().Where(a => !a.IsDeleted && a.Date >= from && a.Date <= to).Select(a => a.Status).ToListAsync();
        int present = attendance.Count(s => s == AttendanceStatus.Present);
        int absent = attendance.Count - present;

        int enrolments = await db.Children.CountAsync(c => !c.IsArchived && c.EnrolmentDate != null && c.EnrolmentDate >= from && c.EnrolmentDate <= to);
        int departures = await db.Children.CountAsync(c => c.Status == ChildStatus.Left && c.LeaveDate != null && c.LeaveDate >= from && c.LeaveDate <= to);
        int active = await db.Children.CountAsync(c => c.Status == ChildStatus.Active && !c.IsArchived);
        var groups = await GroupService.LoadAsync(db, includeArchived: false);
        int ungrouped = await db.Children.CountAsync(c => c.Status == ChildStatus.Active && !c.IsArchived && c.GroupId == null);
        var perGroup = groups.Select(g => new GroupCount(g.Name, g.Occupied, g.Capacity)).ToList();
        if (ungrouped > 0) perGroup.Add(new GroupCount("—", ungrouped, 0));

        return new ReportData(from, to,
            invoiced.Sum(), payments.Sum(p => p.Amount), debtors.Sum(d => d.AmountDue), expenses.Sum(e => e.Amount),
            present, absent, AttendanceRules.AttendanceRate(present, absent),
            enrolments, departures, active, perGroup,
            expenses.GroupBy(e => e.Category).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(e => e.Amount))).ToList(),
            payments.GroupBy(p => p.Method).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(p => p.Amount))).ToList(),
            debtors);
    }
}

public sealed class SearchService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user) : ServiceBase(factory, user), ISearchService
{
    public async Task<List<SearchHit>> SearchAsync(string text, int limitPerKind = 8)
    {
        User.Demand(Permission.Search);
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2) return hits;
        string like = Like(text);
        await using var db = await OpenAsync();

        bool allChildren = User.Can(Permission.Children);
        if (allChildren || User.Can(Permission.Attendance))
        {
            int? ownGroup = User.User!.GroupId;
            var children = await db.Children.AsNoTracking()
                .Where(c => !c.IsArchived && (allChildren || (c.GroupId != null && c.GroupId == ownGroup && c.Status == ChildStatus.Active)))
                .Where(c => EF.Functions.Like(c.FirstName + " " + c.LastName, like) || EF.Functions.Like(c.LastName + " " + c.FirstName, like))
                .OrderBy(c => c.LastName).Take(limitPerKind)
                .Select(c => new { c.Id, c.FirstName, c.LastName, Group = c.Group != null ? c.Group.Name : null })
                .ToListAsync();
            hits.AddRange(children.Select(c => new SearchHit(SearchKind.Child, c.Id, $"{c.FirstName} {c.LastName}".Trim(), c.Group)));
        }

        if (User.Can(Permission.Families))
        {
            var guardians = await db.Guardians.AsNoTracking()
                .Where(g => !g.IsArchived && (EF.Functions.Like(g.FirstName + " " + g.LastName, like) || EF.Functions.Like(g.LastName + " " + g.FirstName, like) || EF.Functions.Like(g.Phone!, like)))
                .OrderBy(g => g.LastName).Take(limitPerKind)
                .Select(g => new { g.Id, g.FirstName, g.LastName, g.Phone, g.FamilyId })
                .ToListAsync();
            hits.AddRange(guardians.Select(g => new SearchHit(SearchKind.Guardian, g.Id, $"{g.FirstName} {g.LastName}".Trim(), g.Phone, g.FamilyId)));
        }

        if (User.Can(Permission.Staff))
        {
            var staff = await db.Staff.AsNoTracking()
                .Where(s => EF.Functions.Like(s.FirstName + " " + s.LastName, like) || EF.Functions.Like(s.LastName + " " + s.FirstName, like))
                .OrderBy(s => s.LastName).Take(limitPerKind)
                .Select(s => new { s.Id, s.FirstName, s.LastName, s.Phone })
                .ToListAsync();
            hits.AddRange(staff.Select(s => new SearchHit(SearchKind.Staff, s.Id, $"{s.FirstName} {s.LastName}".Trim(), s.Phone)));
        }

        if (User.Can(Permission.Payments))
        {
            var receipts = await db.Payments.AsNoTracking()
                .Where(p => EF.Functions.Like(p.ReceiptNumber, like) || EF.Functions.Like(p.Reference!, like))
                .OrderByDescending(p => p.Id).Take(limitPerKind)
                .Select(p => new { p.Id, p.ReceiptNumber, Family = p.Family!.Name, p.Date, p.FamilyId })
                .ToListAsync();
            hits.AddRange(receipts.Select(p => new SearchHit(SearchKind.Receipt, p.Id, p.ReceiptNumber, $"{p.Family} · {p.Date:dd/MM/yyyy}", p.FamilyId)));
        }
        return hits;
    }
}
