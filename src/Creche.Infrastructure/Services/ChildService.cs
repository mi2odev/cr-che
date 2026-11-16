using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class ChildService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, ISettingsService settings, IAppPaths paths, IClock clock)
    : ServiceBase(factory, user), IChildService
{
    public async Task<List<ChildListItem>> ListAsync(ChildFilter filter)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        var query = db.Children.AsNoTracking().AsQueryable();
        if (!filter.IncludeArchived) query = query.Where(c => !c.IsArchived);
        if (filter.Status is { } status) query = query.Where(c => c.Status == status);
        if (filter.GroupId is { } groupId) query = query.Where(c => c.GroupId == groupId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string like = Like(filter.Search);
            query = query.Where(c => EF.Functions.Like(c.FirstName + " " + c.LastName, like)
                                     || EF.Functions.Like(c.LastName + " " + c.FirstName, like)
                                     || EF.Functions.Like(c.Family!.Name, like));
        }
        return await query.OrderBy(c => c.LastName).ThenBy(c => c.FirstName).Select(Projections.ChildListItem).ToListAsync();
    }

    public async Task<Child?> GetAsync(int id)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        return await db.Children.AsNoTrackingWithIdentityResolution().AsSplitQuery()
            .Include(c => c.Group)
            .Include(c => c.FeePlan)
            .Include(c => c.Family!).ThenInclude(f => f.Guardians.Where(g => !g.IsArchived))
            .Include(c => c.Family!).ThenInclude(f => f.Children.Where(s => !s.IsArchived))
            .Include(c => c.Vaccinations.OrderBy(v => v.Date))
            .Include(c => c.Documents.Where(d => !d.IsArchived))
            .Include(c => c.AuthorizedPickups.Where(p => !p.IsArchived))
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task SaveAsync(Child child, bool allowOverCapacity = false)
    {
        User.Demand(Permission.Children);
        string firstName = Required(child.FirstName, "FirstName");
        string lastName = Required(child.LastName, "LastName");
        if (child.DateOfBirth > clock.Today)
            throw new BusinessRuleException("BirthDateFuture", "The date of birth cannot be in the future.");
        if (child.CustomMonthlyFee is < 0m)
            throw new BusinessRuleException("AmountNegative", "Amounts cannot be negative.");

        await using var db = await OpenAsync();
        var existing = await db.Children.FirstOrDefaultAsync(c => c.Id == child.Id)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");

        if (child.GroupId is { } groupId && groupId != existing.GroupId && existing.Status == ChildStatus.Active)
            await CapacityGuard.EnsureAsync(db, await settings.GetAsync(), groupId, allowOverCapacity, existing.Id);

        existing.FirstName = firstName;
        existing.LastName = lastName;
        existing.DateOfBirth = child.DateOfBirth;
        existing.Sex = child.Sex;
        existing.GroupId = child.GroupId;
        existing.FeePlanId = child.FeePlanId;
        existing.CustomMonthlyFee = child.CustomMonthlyFee is { } fee ? FeeCalculator.Round(fee) : null;
        existing.Allergies = Clean(child.Allergies);
        existing.MedicalNotes = Clean(child.MedicalNotes);
        existing.SpecialInstructions = Clean(child.SpecialInstructions);
        existing.PhotoFile = Clean(child.PhotoFile);
        existing.ActivityLogEnabled = child.ActivityLogEnabled;
        existing.SchoolYear = Clean(child.SchoolYear);
        if (existing.Status != ChildStatus.WaitingList && child.EnrolmentDate is not null)
            existing.EnrolmentDate = child.EnrolmentDate;
        await db.SaveChangesAsync();
    }

    public async Task ArchiveAsync(int id)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The child no longer exists.");
        if (child.Status == ChildStatus.Active)
            throw new BusinessRuleException("ArchiveActiveChild", "Record the child's departure before archiving the file.");
        child.IsArchived = true;
        await db.SaveChangesAsync();
    }

    public async Task<Vaccination> SaveVaccinationAsync(Vaccination vaccination)
    {
        User.Demand(Permission.Children);
        vaccination.Name = Required(vaccination.Name, "Name");
        vaccination.Notes = Clean(vaccination.Notes);
        await using var db = await OpenAsync();
        db.Vaccinations.Update(vaccination);
        await db.SaveChangesAsync();
        return vaccination;
    }

    public async Task DeleteVaccinationAsync(int id)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        await db.Vaccinations.Where(v => v.Id == id).ExecuteDeleteAsync();
    }

    public async Task<AuthorizedPickup> SavePickupAsync(AuthorizedPickup pickup)
    {
        User.Demand(Permission.Children);
        pickup.FullName = Required(pickup.FullName, "FullName");
        pickup.Phone = Clean(pickup.Phone);
        await using var db = await OpenAsync();
        db.AuthorizedPickups.Update(pickup);
        await db.SaveChangesAsync();
        return pickup;
    }

    public async Task ArchivePickupAsync(int id)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        await db.AuthorizedPickups.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsArchived, true));
    }

    public async Task<ChildDocument> AddDocumentAsync(int childId, string title, string sourceFilePath)
    {
        User.Demand(Permission.Children);
        title = Required(title, "Title");
        if (!File.Exists(sourceFilePath))
            throw new BusinessRuleException("FileNotFound", "The file does not exist.");
        await using var db = await OpenAsync();
        if (!await db.Children.AnyAsync(c => c.Id == childId))
            throw new BusinessRuleException("NotFound", "The child no longer exists.");

        string fileName = $"{childId}_{Guid.NewGuid():N}{Path.GetExtension(sourceFilePath).ToLowerInvariant()}";
        File.Copy(sourceFilePath, Path.Combine(paths.DocumentsFolder, fileName));
        var document = new ChildDocument { ChildId = childId, Title = title, FileName = fileName };
        db.ChildDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    public async Task ArchiveDocumentAsync(int id)
    {
        User.Demand(Permission.Children);
        await using var db = await OpenAsync();
        await db.ChildDocuments.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsArchived, true));
    }

    public async Task<FeeBreakdown?> GetMonthlyFeeAsync(int childId)
    {
        User.DemandAny(Permission.Children, Permission.Billing);
        await using var db = await OpenAsync();
        var child = await db.Children.AsNoTracking().Include(c => c.FeePlan).FirstOrDefaultAsync(c => c.Id == childId);
        if (child is null) return null;
        var today = clock.Today;
        return await BillingHelper.ComputeFeeAsync(db, await settings.GetAsync(), child, new DateOnly(today.Year, today.Month, 1));
    }
}
