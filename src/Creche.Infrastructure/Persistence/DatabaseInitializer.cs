using Creche.Application.Settings;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Creche.Infrastructure.Persistence;

/// <summary>Brings the database up to date at startup: migrations, WAL mode, the admin account and optional demo data.</summary>
public sealed class DatabaseInitializer(IDbContextFactory<CrecheDbContext> factory, IOptions<DatabaseOptions> options, ILogger<DatabaseInitializer> log)
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin";

    public async Task InitializeAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        if (db.Database.IsSqlite() && !db.Database.GetConnectionString()!.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        await EnsureAdminAsync(db);
        if (options.Value.SeedDemoData) await SeedDemoDataAsync(db);
    }

    /// <summary>The admin account always exists. Its first password must be changed at first sign-in.</summary>
    public async Task EnsureAdminAsync(CrecheDbContext db)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin)) return;
        var (hash, salt, iterations) = PasswordHasher.Hash(DefaultAdminPassword);
        db.Users.Add(new User
        {
            Username = DefaultAdminUsername,
            DisplayName = "Administrator",
            Role = UserRole.Admin,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            MustChangePassword = true,
        });
        await db.SaveChangesAsync();
        log.LogInformation("Created the default administrator account");
    }

    public async Task SeedDemoDataAsync(CrecheDbContext db)
    {
        if (await db.Groups.AnyAsync() || await db.Children.AnyAsync() || await db.FeePlans.AnyAsync()) return;

        var today = DateOnly.FromDateTime(DateTime.Today);
        string schoolYear = CrecheSettings.DefaultSchoolYear(DateTime.Today);
        var enrolled = new DateOnly(today.Month >= 9 ? today.Year : today.Year - 1, 9, 1);

        var babies = new Group { Name = "Bébés", MinAgeMonths = 3, MaxAgeMonths = 18, Capacity = 8 };
        var toddlers = new Group { Name = "Moyens", MinAgeMonths = 18, MaxAgeMonths = 36, Capacity = 12 };
        var older = new Group { Name = "Grands", MinAgeMonths = 36, MaxAgeMonths = 60, Capacity = 15 };
        db.Groups.AddRange(babies, toddlers, older);

        var fullMeals = new FeePlan { Name = "Journée complète + repas", Type = FeePlanType.FullDay, MonthlyAmount = 18000m, IncludesMeals = true, MealsAmount = 4000m };
        var full = new FeePlan { Name = "Journée complète", Type = FeePlanType.FullDay, MonthlyAmount = 18000m };
        var half = new FeePlan { Name = "Demi-journée", Type = FeePlanType.HalfDay, MonthlyAmount = 11000m };
        var hourly = new FeePlan { Name = "Horaire", Type = FeePlanType.Hourly, HourlyRate = 250m };
        db.FeePlans.AddRange(fullMeals, full, half, hourly);

        db.Staff.AddRange(
            new Staff { FirstName = "Nadia", LastName = "Hamdi", Role = StaffRole.Director, MonthlySalary = 70000m, Phone = "0550 10 20 30", HireDate = enrolled.AddYears(-3) },
            new Staff { FirstName = "Samira", LastName = "Bouzid", Role = StaffRole.Educator, Group = babies, MonthlySalary = 42000m, HireDate = enrolled.AddYears(-2) },
            new Staff { FirstName = "Lina", LastName = "Meziane", Role = StaffRole.Educator, Group = toddlers, MonthlySalary = 42000m, HireDate = enrolled.AddYears(-1) },
            new Staff { FirstName = "Yasmine", LastName = "Kaci", Role = StaffRole.Educator, Group = older, MonthlySalary = 42000m, HireDate = enrolled },
            new Staff { FirstName = "Fatiha", LastName = "Saadi", Role = StaffRole.Cook, MonthlySalary = 35000m, HireDate = enrolled.AddYears(-2) });

        Child Kid(string first, string last, int ageMonths, Sex sex, Group group, FeePlan plan, string? allergies = null) => new()
        {
            FirstName = first, LastName = last, DateOfBirth = today.AddMonths(-ageMonths).AddDays(-(first.Length * 3)), Sex = sex, Group = group, FeePlan = plan,
            Status = ChildStatus.Active, EnrolmentDate = enrolled, SchoolYear = schoolYear, Allergies = allergies,
        };
        Guardian Parent(string first, string last, Relationship relationship, string phone, string job, bool primary = false) => new()
        {
            FirstName = first, LastName = last, Relationship = relationship, Phone = phone, Job = job, IsPrimary = primary,
        };

        db.Families.AddRange(
            new Family
            {
                Name = "Benali", Address = "12 rue des Oliviers, Alger",
                Guardians = [Parent("Amel", "Benali", Relationship.Mother, "0661 11 22 33", "Médecin", true), Parent("Karim", "Benali", Relationship.Father, "0770 44 55 66", "Ingénieur")],
                Children = [Kid("Ines", "Benali", 50, Sex.Female, older, fullMeals), Kid("Adam", "Benali", 26, Sex.Male, toddlers, fullMeals, "Arachides"), Kid("Lyna", "Benali", 10, Sex.Female, babies, full)],
            },
            new Family
            {
                Name = "Cherif", Address = "5 cité des Roses, Blida",
                Guardians = [Parent("Sofiane", "Cherif", Relationship.Father, "0555 12 34 56", "Commerçant", true)],
                Children = [Kid("Rayan", "Cherif", 40, Sex.Male, older, full), Kid("Sara", "Cherif", 20, Sex.Female, toddlers, half)],
            },
            new Family
            {
                Name = "Mansouri", Address = "28 boulevard Front de Mer, Oran",
                Guardians = [Parent("Leila", "Mansouri", Relationship.Mother, "0699 87 65 43", "Enseignante", true)],
                Children = [Kid("Yanis", "Mansouri", 14, Sex.Male, babies, fullMeals, "Lactose")],
            },
            new Family
            {
                Name = "Haddad", Address = "3 rue Didouche Mourad, Alger",
                Guardians = [Parent("Meriem", "Haddad", Relationship.Mother, "0540 22 33 44", "Avocate", true), Parent("Walid", "Haddad", Relationship.Father, "0541 55 66 77", "Architecte")],
                Children = [Kid("Nour", "Haddad", 30, Sex.Female, toddlers, hourly)],
            },
            new Family
            {
                Name = "Toumi", Address = "17 cité El Wiam, Sétif",
                Guardians = [Parent("Hakim", "Toumi", Relationship.Father, "0772 90 80 70", "Pharmacien", true)],
                Children =
                [
                    Kid("Anis", "Toumi", 45, Sex.Male, older, half),
                    new Child { FirstName = "Maya", LastName = "Toumi", DateOfBirth = today.AddMonths(-8), Sex = Sex.Female, Group = babies, FeePlan = full, Status = ChildStatus.WaitingList },
                ],
            });

        await db.SaveChangesAsync();
        log.LogInformation("Demo data inserted");
    }
}
