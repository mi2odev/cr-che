using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Creche.Tests;

public class AttendanceTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    private static async Task<(TestHost Host, int ChildId, int GroupId, IAttendanceService Attendance)> SetupAsync()
    {
        var host = await TestHost.CreateAsync();
        var group = await host.AddGroupAsync();
        var child = await host.EnrolAsync("Ines", groupId: group.Id);
        return (host, child.Id, group.Id, host.Get<IAttendanceService>());
    }

    [Fact]
    public async Task Check_in_then_check_out_records_times_and_people()
    {
        var (host, childId, groupId, attendance) = await SetupAsync();
        using var _ = host;

        await attendance.CheckInAsync(childId, Today, new TimeOnly(8, 5), "Parent Test");
        await attendance.CheckOutAsync(childId, Today, new TimeOnly(16, 35), "Grandma");

        var row = Assert.Single(await attendance.GetRollCallAsync(groupId, Today));
        Assert.Equal(AttendanceStatus.Present, row.Status);
        Assert.Equal(new TimeOnly(8, 5), row.CheckIn);
        Assert.Equal(new TimeOnly(16, 35), row.CheckOut);
        Assert.Equal("Parent Test", row.DroppedOffBy);
        Assert.Equal("Grandma", row.PickedUpBy);
        Assert.Contains("Parent Test", row.PickupNames);
    }

    [Fact]
    public async Task Check_out_requires_a_check_in()
    {
        var (host, childId, _, attendance) = await SetupAsync();
        using var __ = host;
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.CheckOutAsync(childId, Today, new TimeOnly(16, 0), null));
        Assert.Equal("CheckOutWithoutCheckIn", error.Code);
    }

    [Fact]
    public async Task Check_out_cannot_be_before_check_in()
    {
        var (host, childId, _, attendance) = await SetupAsync();
        using var __ = host;
        await attendance.CheckInAsync(childId, Today, new TimeOnly(9, 0), null);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.CheckOutAsync(childId, Today, new TimeOnly(8, 0), null));
        Assert.Equal("CheckOutBeforeCheckIn", error.Code);
    }

    [Fact]
    public async Task Attendance_cannot_be_recorded_in_the_future_or_before_enrolment()
    {
        var (host, childId, _, attendance) = await SetupAsync();
        using var __ = host;
        var future = await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.CheckInAsync(childId, Today.AddDays(1), new TimeOnly(8, 0), null));
        var early = await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.MarkAbsentAsync(childId, new DateOnly(2026, 8, 31), "n/a"));
        Assert.Equal("AttendanceFuture", future.Code);
        Assert.Equal("AttendanceBeforeEnrolment", early.Code);
    }

    [Fact]
    public async Task Waiting_list_children_and_children_who_left_cannot_be_marked()
    {
        var (host, childId, groupId, attendance) = await SetupAsync();
        using var _ = host;
        var waiting = await host.EnrolAsync("Waiting", groupId: groupId, waitingList: true);
        await host.Get<IEnrolmentService>().LeaveAsync(childId, new DateOnly(2026, 11, 10), "Moved");

        Assert.Equal("AttendanceNotEnrolled", (await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.CheckInAsync(waiting.Id, Today, new TimeOnly(8, 0), null))).Code);
        Assert.Equal("AttendanceAfterLeave", (await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.CheckInAsync(childId, Today, new TimeOnly(8, 0), null))).Code);
        await attendance.CheckInAsync(childId, new DateOnly(2026, 11, 10), new TimeOnly(8, 0), null); // last day is still allowed
        Assert.Empty(await attendance.GetRollCallAsync(groupId, Today));
    }

    [Fact]
    public async Task Absence_with_reason_can_be_corrected_to_present_but_not_after_check_out()
    {
        var (host, childId, groupId, attendance) = await SetupAsync();
        using var _ = host;

        await attendance.MarkAbsentAsync(childId, Today, "Fever");
        var row = Assert.Single(await attendance.GetRollCallAsync(groupId, Today));
        Assert.Equal(AttendanceStatus.Absent, row.Status);
        Assert.Equal("Fever", row.AbsenceReason);

        await attendance.CheckInAsync(childId, Today, new TimeOnly(10, 0), null);
        await attendance.CheckOutAsync(childId, Today, new TimeOnly(15, 0), null);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => attendance.MarkAbsentAsync(childId, Today, "oops"));
        Assert.Equal("AbsentAfterCheckOut", error.Code);
    }

    [Fact]
    public async Task Clearing_a_record_keeps_it_in_the_database_as_deleted()
    {
        var (host, childId, groupId, attendance) = await SetupAsync();
        using var _ = host;
        await attendance.CheckInAsync(childId, Today, new TimeOnly(8, 0), null);

        await attendance.ClearAsync(childId, Today);
        await attendance.MarkAbsentAsync(childId, Today, "Sick");

        await using var db = host.Db();
        var rows = await db.Attendance.Where(a => a.ChildId == childId).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsDeleted);
        Assert.False(rows[1].IsDeleted);
        Assert.Equal(AttendanceStatus.Absent, Assert.Single(await attendance.GetRollCallAsync(groupId, Today)).Status);
    }

    [Fact]
    public async Task Fast_roll_call_checks_in_everyone_not_yet_marked()
    {
        var (host, childId, groupId, attendance) = await SetupAsync();
        using var _ = host;
        var second = await host.EnrolAsync("Adam", groupId: groupId);
        await attendance.MarkAbsentAsync(childId, Today, "Sick");

        int checkedIn = await attendance.CheckInAllAsync(groupId, Today, new TimeOnly(8, 0));

        Assert.Equal(1, checkedIn);
        var rows = await attendance.GetRollCallAsync(groupId, Today);
        Assert.Equal(AttendanceStatus.Absent, rows.Single(r => r.ChildId == childId).Status);
        Assert.Equal(AttendanceStatus.Present, rows.Single(r => r.ChildId == second.Id).Status);
    }

    [Fact]
    public async Task Late_pick_up_creates_an_extra_charge_that_follows_corrections()
    {
        var (host, childId, _, attendance) = await SetupAsync();
        using var __ = host;
        await host.UpdateSettingsAsync(s =>
        {
            s.ClosingTime = new TimeOnly(17, 0);
            s.LatePickupEnabled = true;
            s.LatePickupGraceMinutes = 10;
            s.LatePickupBlockMinutes = 15;
            s.LatePickupFeePerBlock = 200m;
        });
        var billing = host.Get<IBillingService>();
        await attendance.CheckInAsync(childId, Today, new TimeOnly(8, 0), null);

        await attendance.CheckOutAsync(childId, Today, new TimeOnly(17, 40), null);   // 30 billable minutes → 2 blocks
        var charge = Assert.Single(await billing.ListExtraChargesAsync());
        Assert.Equal(ChargeKind.LatePickup, charge.Kind);
        Assert.Equal(400m, charge.Amount);

        await attendance.CheckOutAsync(childId, Today, new TimeOnly(17, 20), null);   // corrected: 1 block
        Assert.Equal(200m, Assert.Single(await billing.ListExtraChargesAsync()).Amount);

        await attendance.CheckOutAsync(childId, Today, new TimeOnly(16, 55), null);   // on time after all
        Assert.Empty(await billing.ListExtraChargesAsync());
    }

    [Fact]
    public async Task Monthly_sheet_totals_days_hours_and_rate()
    {
        var (host, childId, _, attendance) = await SetupAsync();
        using var __ = host;
        await attendance.CheckInAsync(childId, new DateOnly(2026, 11, 2), new TimeOnly(8, 0), null);
        await attendance.CheckOutAsync(childId, new DateOnly(2026, 11, 2), new TimeOnly(16, 0), null);
        await attendance.CheckInAsync(childId, new DateOnly(2026, 11, 3), new TimeOnly(8, 30), null);
        await attendance.CheckOutAsync(childId, new DateOnly(2026, 11, 3), new TimeOnly(12, 0), null);
        await attendance.MarkAbsentAsync(childId, new DateOnly(2026, 11, 4), "Sick");
        await attendance.MarkAbsentAsync(childId, new DateOnly(2026, 11, 5), "Sick");

        var sheet = await attendance.GetMonthlySheetAsync(childId, 2026, 11);

        Assert.Equal(30, sheet.Days.Count);
        Assert.Equal(2, sheet.PresentDays);
        Assert.Equal(2, sheet.AbsentDays);
        Assert.Equal(11.5m, sheet.TotalHours);
        Assert.Equal(50m, sheet.AttendanceRate);
    }

    [Fact]
    public void Pure_rules()
    {
        Assert.Equal(7.75m, AttendanceRules.HoursAttended(new TimeOnly(8, 0), new TimeOnly(15, 45)));
        Assert.Equal(0m, AttendanceRules.HoursAttended(new TimeOnly(8, 0), null));
        Assert.Equal(0m, AttendanceRules.AttendanceRate(0, 0));
        Assert.Equal(66.7m, AttendanceRules.AttendanceRate(2, 1));
    }
}
