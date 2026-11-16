using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure;
using Creche.Infrastructure.Persistence;
using Creche.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Creche.Tests;

public sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 11, 16, 10, 0, 0);
    public DateOnly Today => DateOnly.FromDateTime(Now);
}

/// <summary>
/// The real services on a real SQLite database: in memory by default (one open connection shared by every
/// context, schema created by the migrations), or a temporary file when a test needs the backup API.
/// </summary>
public sealed class TestHost : IDisposable
{
    private sealed class SharedConnectionFactory(SqliteConnection connection) : IDbContextFactory<CrecheDbContext>
    {
        public CrecheDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<CrecheDbContext>().UseSqlite(connection).Options);
    }

    private readonly SqliteConnection? _connection;
    private readonly ServiceProvider _provider;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "creche-tests-" + Guid.NewGuid().ToString("N"));

    public FakeClock Clock { get; } = new();
    public IAppPaths Paths { get; }

    private TestHost(bool onDisk)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DatabaseOptions>(_ => { });
        services.AddSingleton<IClock>(Clock);
        Paths = new AppPaths(new StorageOptions { DataFolder = _folder });
        services.AddSingleton(Paths);

        if (onDisk)
        {
            services.AddDbContextFactory<CrecheDbContext>(o => o.UseSqlite($"Data Source={Paths.DatabasePath}"));
        }
        else
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }
            services.AddSingleton<IDbContextFactory<CrecheDbContext>>(new SharedConnectionFactory(_connection));
        }
        services.AddCrecheServices();
        _provider = services.BuildServiceProvider();
    }

    public static async Task<TestHost> CreateAsync(bool onDisk = false)
    {
        var host = new TestHost(onDisk);
        await host.Get<DatabaseInitializer>().InitializeAsync();
        host.SignIn(UserRole.Admin);
        return host;
    }

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public CrecheDbContext Db() => Get<IDbContextFactory<CrecheDbContext>>().CreateDbContext();

    public void SignIn(UserRole role, int? groupId = null, int userId = 1)
        => Get<ICurrentUser>().SignIn(new UserSession(userId, role.ToString().ToLowerInvariant(), role.ToString(), role, groupId, false));

    public void SignOut() => Get<ICurrentUser>().SignOut();

    // ── Shortcuts used by many tests ──

    public async Task<Group> AddGroupAsync(string name = "Moyens", int capacity = 10)
        => await Get<IGroupService>().SaveAsync(new Group { Name = name, Capacity = capacity, MinAgeMonths = 0, MaxAgeMonths = 60 });

    public async Task<FeePlan> AddPlanAsync(decimal monthly = 10000m, FeePlanType type = FeePlanType.FullDay, decimal meals = 0m, decimal hourlyRate = 0m)
        => await Get<IBillingService>().SaveFeePlanAsync(new FeePlan
        {
            Name = $"{type} {monthly}", Type = type, MonthlyAmount = monthly, IncludesMeals = meals > 0m, MealsAmount = meals, HourlyRate = hourlyRate,
        });

    public async Task<Child> EnrolAsync(string firstName, int? familyId = null, int? groupId = null, int? planId = null,
        DateOnly? dateOfBirth = null, DateOnly? enrolmentDate = null, bool waitingList = false, decimal registrationFee = 0m, bool allowOverCapacity = false)
        => await Get<IEnrolmentService>().EnrolAsync(new EnrolmentRequest
        {
            FirstName = firstName,
            LastName = "Test",
            DateOfBirth = dateOfBirth ?? new DateOnly(2024, 1, 15),
            FamilyId = familyId,
            GuardianFirstName = "Parent",
            GuardianLastName = "Test",
            GuardianPhone = "0550 00 00 00",
            GroupId = groupId,
            FeePlanId = planId,
            EnrolmentDate = enrolmentDate ?? new DateOnly(2026, 9, 1),
            WaitingList = waitingList,
            RegistrationFee = registrationFee,
            AllowOverCapacity = allowOverCapacity,
        });

    public async Task UpdateSettingsAsync(Action<CrecheSettings> change)
    {
        var service = Get<ISettingsService>();
        var settings = await service.GetAsync();
        change(settings);
        await service.SaveAsync(settings);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }
}
