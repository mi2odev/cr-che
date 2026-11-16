using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Services;

public sealed class FamilyService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user) : ServiceBase(factory, user), IFamilyService
{
    public async Task<List<FamilyListItem>> ListAsync(string? search = null, bool includeArchived = false)
    {
        User.Demand(Permission.Families);
        await using var db = await OpenAsync();
        var query = db.Families.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(f => !f.IsArchived);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string like = Like(search);
            query = query.Where(f => EF.Functions.Like(f.Name, like)
                                     || f.Guardians.Any(g => EF.Functions.Like(g.FirstName + " " + g.LastName, like) || EF.Functions.Like(g.Phone!, like))
                                     || f.Children.Any(c => EF.Functions.Like(c.FirstName + " " + c.LastName, like)));
        }
        var rows = await query.OrderBy(f => f.Name).AsSplitQuery()
            .Select(f => new
            {
                f.Id, f.Name, f.Address, f.IsArchived,
                Guardian = f.Guardians.Where(g => !g.IsArchived).OrderByDescending(g => g.IsPrimary).ThenBy(g => g.Id)
                    .Select(g => new { g.FirstName, g.LastName, g.Phone }).FirstOrDefault(),
                Children = f.Children.Where(c => !c.IsArchived).OrderBy(c => c.DateOfBirth).Select(c => c.FirstName).ToList(),
            })
            .ToListAsync();

        // Money is summed in memory: SQLite has no exact decimal arithmetic.
        var open = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid)
            .Select(i => new { i.FamilyId, i.Total, i.PaidAmount }).ToListAsync();
        var due = open.GroupBy(i => i.FamilyId).ToDictionary(g => g.Key, g => g.Sum(i => i.Total - i.PaidAmount));

        return rows.Select(f => new FamilyListItem(
            f.Id, f.Name,
            f.Guardian is null ? null : $"{f.Guardian.FirstName} {f.Guardian.LastName}".Trim(),
            f.Guardian?.Phone, f.Address, f.Children.Count, string.Join(", ", f.Children),
            due.GetValueOrDefault(f.Id), f.IsArchived)).ToList();
    }

    public async Task<Family?> GetAsync(int id)
    {
        User.Demand(Permission.Families);
        await using var db = await OpenAsync();
        return await db.Families.AsNoTracking().AsSplitQuery()
            .Include(f => f.Guardians.Where(g => !g.IsArchived))
            .Include(f => f.Children.Where(c => !c.IsArchived)).ThenInclude(c => c.Group)
            .FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task<Family> SaveAsync(Family family)
    {
        User.Demand(Permission.Families);
        string name = Required(family.Name, "Name");
        await using var db = await OpenAsync();
        Family target;
        if (family.Id == 0)
        {
            target = new Family();
            db.Families.Add(target);
        }
        else
        {
            target = await db.Families.FirstOrDefaultAsync(f => f.Id == family.Id)
                ?? throw new BusinessRuleException("NotFound", "The family no longer exists.");
        }
        target.Name = name;
        target.Address = Clean(family.Address);
        target.Notes = Clean(family.Notes);
        await db.SaveChangesAsync();
        return target;
    }

    public async Task ArchiveAsync(int id)
    {
        User.Demand(Permission.Families);
        await using var db = await OpenAsync();
        var family = await db.Families.FirstOrDefaultAsync(f => f.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The family no longer exists.");
        if (await db.Children.AnyAsync(c => c.FamilyId == id && !c.IsArchived && c.Status != ChildStatus.Left))
            throw new BusinessRuleException("FamilyHasChildren", "A family with enrolled or waiting children cannot be archived.");
        if (await db.Invoices.AnyAsync(i => i.FamilyId == id && (i.Status == InvoiceStatus.Unpaid || i.Status == InvoiceStatus.PartiallyPaid)))
            throw new BusinessRuleException("FamilyOwesMoney", "A family that still owes money cannot be archived.");
        family.IsArchived = true;
        await db.SaveChangesAsync();
    }

    public async Task<Guardian> SaveGuardianAsync(Guardian guardian)
    {
        User.Demand(Permission.Families);
        string firstName = Required(guardian.FirstName, "FirstName");
        string lastName = Required(guardian.LastName, "LastName");
        await using var db = await OpenAsync();
        if (!await db.Families.AnyAsync(f => f.Id == guardian.FamilyId))
            throw new BusinessRuleException("NotFound", "The family no longer exists.");

        Guardian target;
        if (guardian.Id == 0)
        {
            target = new Guardian { FamilyId = guardian.FamilyId };
            db.Guardians.Add(target);
        }
        else
        {
            target = await db.Guardians.FirstOrDefaultAsync(g => g.Id == guardian.Id)
                ?? throw new BusinessRuleException("NotFound", "The guardian no longer exists.");
        }
        target.FirstName = firstName;
        target.LastName = lastName;
        target.Relationship = guardian.Relationship;
        target.Phone = Clean(guardian.Phone);
        target.Phone2 = Clean(guardian.Phone2);
        target.Email = Clean(guardian.Email);
        target.Address = Clean(guardian.Address);
        target.Job = Clean(guardian.Job);
        target.CanPickUp = guardian.CanPickUp;
        target.IsEmergencyContact = guardian.IsEmergencyContact;

        var others = await db.Guardians.Where(g => g.FamilyId == guardian.FamilyId && g.Id != guardian.Id && !g.IsArchived).ToListAsync();
        // Exactly one primary guardian per family.
        target.IsPrimary = guardian.IsPrimary || !others.Any(g => g.IsPrimary);
        if (target.IsPrimary)
            foreach (var other in others) other.IsPrimary = false;
        await db.SaveChangesAsync();
        return target;
    }

    public async Task ArchiveGuardianAsync(int id)
    {
        User.Demand(Permission.Families);
        await using var db = await OpenAsync();
        var guardian = await db.Guardians.FirstOrDefaultAsync(g => g.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The guardian no longer exists.");
        var others = await db.Guardians.Where(g => g.FamilyId == guardian.FamilyId && g.Id != id && !g.IsArchived).OrderBy(g => g.Id).ToListAsync();
        bool hasChildren = await db.Children.AnyAsync(c => c.FamilyId == guardian.FamilyId && !c.IsArchived && c.Status != ChildStatus.Left);
        if (others.Count == 0 && hasChildren)
            throw new BusinessRuleException("LastGuardian", "A child must always have at least one guardian.");
        guardian.IsArchived = true;
        if (guardian.IsPrimary && others.Count > 0)
        {
            guardian.IsPrimary = false;
            others[0].IsPrimary = true;
        }
        await db.SaveChangesAsync();
    }
}

public sealed class GroupService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user) : ServiceBase(factory, user), IGroupService
{
    public async Task<List<GroupInfo>> ListAsync(bool includeArchived = false)
    {
        User.DemandAny(Permission.GroupsView, Permission.GroupsManage);
        await using var db = await OpenAsync();
        return await LoadAsync(db, includeArchived);
    }

    internal static async Task<List<GroupInfo>> LoadAsync(CrecheDbContext db, bool includeArchived)
    {
        var rows = await db.Groups.AsNoTracking()
            .Where(g => includeArchived || !g.IsArchived)
            .OrderBy(g => g.MinAgeMonths).ThenBy(g => g.Name)
            .Select(g => new
            {
                g.Id, g.Name, g.MinAgeMonths, g.MaxAgeMonths, g.Capacity, g.IsArchived,
                Occupied = g.Children.Count(c => c.Status == ChildStatus.Active && !c.IsArchived),
                Educators = g.Staff.Where(s => s.IsActive).OrderBy(s => s.LastName).Select(s => s.FirstName + " " + s.LastName).ToList(),
            })
            .AsSplitQuery()
            .ToListAsync();
        return rows.Select(g => new GroupInfo(g.Id, g.Name, g.MinAgeMonths, g.MaxAgeMonths, g.Capacity, g.Occupied, string.Join(", ", g.Educators), g.IsArchived)).ToList();
    }

    public async Task<List<ChildListItem>> GetChildrenAsync(int groupId)
    {
        User.DemandAny(Permission.GroupsView, Permission.GroupsManage);
        await using var db = await OpenAsync();
        return await db.Children.AsNoTracking()
            .Where(c => c.GroupId == groupId && c.Status == ChildStatus.Active && !c.IsArchived)
            .OrderBy(c => c.LastName).ThenBy(c => c.FirstName)
            .Select(Projections.ChildListItem).ToListAsync();
    }

    public async Task<Group> SaveAsync(Group group)
    {
        User.Demand(Permission.GroupsManage);
        string name = Required(group.Name, "Name");
        if (group.Capacity < 1)
            throw new BusinessRuleException("CapacityInvalid", "A group needs at least one place.");
        if (group.MinAgeMonths < 0 || (group.MaxAgeMonths > 0 && group.MaxAgeMonths < group.MinAgeMonths))
            throw new BusinessRuleException("AgeRangeInvalid", "The age range is not valid.");

        await using var db = await OpenAsync();
        Group target;
        if (group.Id == 0)
        {
            target = new Group();
            db.Groups.Add(target);
        }
        else
        {
            target = await db.Groups.FirstOrDefaultAsync(g => g.Id == group.Id)
                ?? throw new BusinessRuleException("NotFound", "The group no longer exists.");
        }
        target.Name = name;
        target.MinAgeMonths = group.MinAgeMonths;
        target.MaxAgeMonths = group.MaxAgeMonths;
        target.Capacity = group.Capacity;
        await db.SaveChangesAsync();
        return target;
    }

    public async Task ArchiveAsync(int id)
    {
        User.Demand(Permission.GroupsManage);
        await using var db = await OpenAsync();
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id)
            ?? throw new BusinessRuleException("NotFound", "The group no longer exists.");
        if (await db.Children.AnyAsync(c => c.GroupId == id && c.Status == ChildStatus.Active && !c.IsArchived))
            throw new BusinessRuleException("GroupNotEmpty", "Move the children to another group before archiving this one.");
        group.IsArchived = true;
        await db.SaveChangesAsync();
    }
}
