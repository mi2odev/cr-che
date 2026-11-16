using Creche.Domain.Enums;

namespace Creche.Domain.Rules;

public static class AttendanceRules
{
    /// <summary>Attendance can only be recorded for an enrolled child, on a day they belong to the crèche, not in the future.</summary>
    public static void EnsureCanRecord(ChildStatus status, DateOnly? enrolmentDate, DateOnly? leaveDate, DateOnly date, DateOnly today)
    {
        if (date > today)
            throw new BusinessRuleException("AttendanceFuture", "Attendance cannot be recorded for a future date.");
        if (status == ChildStatus.WaitingList || enrolmentDate is null)
            throw new BusinessRuleException("AttendanceNotEnrolled", "This child is not enrolled.");
        if (date < enrolmentDate)
            throw new BusinessRuleException("AttendanceBeforeEnrolment", "The date is before the child's enrolment date.");
        if (status == ChildStatus.Left && (leaveDate is null || date > leaveDate))
            throw new BusinessRuleException("AttendanceAfterLeave", "The child has left the crèche.");
    }

    public static void EnsureCanCheckOut(AttendanceStatus? status, TimeOnly? checkIn, TimeOnly checkOut)
    {
        if (status != AttendanceStatus.Present || checkIn is null)
            throw new BusinessRuleException("CheckOutWithoutCheckIn", "The child must be checked in before being checked out.");
        if (checkOut < checkIn)
            throw new BusinessRuleException("CheckOutBeforeCheckIn", "Check-out time cannot be earlier than check-in time.");
    }

    public static void EnsureAbsenceAllowed(TimeOnly? checkOut)
    {
        if (checkOut is not null)
            throw new BusinessRuleException("AbsentAfterCheckOut", "A child who was already checked out cannot be marked absent.");
    }

    public static decimal HoursAttended(TimeOnly? checkIn, TimeOnly? checkOut)
    {
        if (checkIn is null || checkOut is null || checkOut <= checkIn) return 0m;
        return Math.Round((decimal)(checkOut.Value - checkIn.Value).TotalMinutes / 60m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Share of recorded days on which the child was present, as a percentage (0 when nothing recorded).</summary>
    public static decimal AttendanceRate(int presentDays, int absentDays)
    {
        int total = presentDays + absentDays;
        return total == 0 ? 0m : Math.Round(presentDays * 100m / total, 1, MidpointRounding.AwayFromZero);
    }
}
