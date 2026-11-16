using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class EnrolmentService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, ISettingsService settings, IClock clock)
    : ServiceBase(factory, user), IEnrolmentService
{
    public async Task<Child> EnrolAsync(EnrolmentRequest request)
    {
        User.Demand(Permission.Enrolment);
        string firstName = Required(request.FirstName, "FirstName");
        string lastName = Required(request.LastName, "LastName");
        if (request.DateOfBirth > clock.Today)
            throw new BusinessRuleException("BirthDateFuture", "The date of birth cannot be in the future.");
        if (request.RegistrationFee < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");

        var config = await settings.GetAsync();
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        int familyId;
        if (request.FamilyId is { } existingFamily)
        {
            if (!await db.Families.AnyAsync(f => f.Id == existingFamily && !f.IsArchived))
                throw new BusinessRuleException("NotFound", "The family no longer exists.");
            familyId = existingFamily;
        }
        else
        {
            var family = new Family
            {
                Name = lastName,
                Address = Clean(request.Address),
                Guardians =
                [
                    new Guardian
                    {
                        FirstName = Required(request.GuardianFirstName, "GuardianFirstName"),
                        LastName = Required(request.GuardianLastName, "GuardianLastName"),
                        Relationship = request.GuardianRelationship,
                        Phone = Clean(request.GuardianPhone),
                        Job = Clean(request.GuardianJob),
                        Address = Clean(request.Address),
                        IsPrimary = true,
                    },
                ],
            };
            db.Families.Add(family);
            await db.SaveChangesAsync();
            familyId = family.Id;
        }

        if (!request.WaitingList && request.GroupId is { } groupId)
            await CapacityGuard.EnsureAsync(db, config, groupId, request.AllowOverCapacity);

        var child = new Child
        {
            FamilyId = familyId,
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = request.DateOfBirth,
            Sex = request.Sex,
            GroupId = request.GroupId,
            FeePlanId = request.FeePlanId,
            Status = request.WaitingList ? ChildStatus.WaitingList : ChildStatus.Active,
            EnrolmentDate = request.WaitingList ? null : request.EnrolmentDate,
            SchoolYear = request.WaitingList ? null : config.SchoolYear,
            Allergies = Clean(request.Allergies),
            MedicalNotes = Clean(request.MedicalNotes),
        };
        db.Children.Add(child);
        await db.SaveChangesAsync();

        if (!request.WaitingList && request.RegistrationFee > 0m)
            await BillingHelper.AddOneOffInvoiceAsync(db, child, ChargeKind.RegistrationFee, config.SchoolYear, request.RegistrationFee, clock.Today);

        await tx.CommitAsync();
        return child;
    }

    public async Task AdmitFromWaitingListAsync(int childId, int? groupId, int? feePlanId, DateOnly enrolmentDate, decimal registrationFee, bool allowOverCapacity = false)
    {
        User.Demand(Permission.Enrolment);
        if (registrationFee < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");
        var config = await settings.GetAsync();
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == childId && !c.IsArchived)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        if (child.Status != ChildStatus.WaitingList)
            throw new BusinessRuleException("NotOnWaitingList", "This child is not on the waiting list.");
        if (groupId is { } target)
            await CapacityGuard.EnsureAsync(db, config, target, allowOverCapacity, child.Id);

        child.Status = ChildStatus.Active;
        child.GroupId = groupId;
        child.FeePlanId = feePlanId ?? child.FeePlanId;
        child.EnrolmentDate = enrolmentDate;
        child.SchoolYear = config.SchoolYear;
        await db.SaveChangesAsync();
        if (registrationFee > 0m)
            await BillingHelper.AddOneOffInvoiceAsync(db, child, ChargeKind.RegistrationFee, config.SchoolYear, registrationFee, clock.Today);
        await tx.CommitAsync();
    }

    public async Task ReEnrolAsync(int childId, string schoolYear, int? groupId, int? feePlanId, decimal fee, bool allowOverCapacity = false)
    {
        User.Demand(Permission.Enrolment);
        schoolYear = Required(schoolYear, "SchoolYear");
        if (fee < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");
        var config = await settings.GetAsync();
        await using var db = await OpenAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == childId && !c.IsArchived)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        if (child.Status == ChildStatus.WaitingList)
            throw new BusinessRuleException("NotEnrolled", "A child on the waiting list must be admitted first.");
        if (child.Status == ChildStatus.Active && child.SchoolYear == schoolYear)
            throw new BusinessRuleException("AlreadyReEnrolled", $"This child is already enrolled for {schoolYear}.", schoolYear);

        int? targetGroup = groupId ?? child.GroupId;
        bool takesNewPlace = child.Status == ChildStatus.Left || targetGroup != child.GroupId;
        if (targetGroup is { } target && takesNewPlace)
            await CapacityGuard.EnsureAsync(db, config, target, allowOverCapacity, child.Id);

        if (child.Status == ChildStatus.Left)
        {
            child.Status = ChildStatus.Active;
            child.LeaveDate = null;
            child.LeaveReason = null;
            child.EnrolmentDate = clock.Today;
        }
        child.GroupId = targetGroup;
        child.FeePlanId = feePlanId ?? child.FeePlanId;
        child.SchoolYear = schoolYear;
        await db.SaveChangesAsync();
        if (fee > 0m)
            await BillingHelper.AddOneOffInvoiceAsync(db, child, ChargeKind.RegistrationFee, schoolYear, fee, clock.Today);
        await tx.CommitAsync();
    }

    public async Task LeaveAsync(int childId, DateOnly leaveDate, string? reason)
    {
        User.Demand(Permission.Enrolment);
        await using var db = await OpenAsync();
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == childId)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        if (child.Status != ChildStatus.Active)
            throw new BusinessRuleException("NotEnrolled", "Only an enrolled child can leave the crèche.");
        if (child.EnrolmentDate is { } enrolled && leaveDate < enrolled)
            throw new BusinessRuleException("LeaveBeforeEnrolment", "The leave date cannot be before the enrolment date.");
        child.Status = ChildStatus.Left;
        child.LeaveDate = leaveDate;
        child.LeaveReason = Clean(reason);
        await db.SaveChangesAsync();
    }
}
