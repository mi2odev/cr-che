using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Infrastructure.Persistence;
using Creche.Infrastructure.Services;
using Creche.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Creche.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the database, the file locations and every application service.</summary>
    public static IServiceCollection AddCrecheInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));

        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        var paths = new AppPaths(storage);
        services.AddSingleton<IAppPaths>(paths);

        string connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, ForeignKeys = true }.ToString();
        services.AddDbContextFactory<CrecheDbContext>(options => options.UseSqlite(connectionString));

        return services.AddCrecheServices();
    }

    /// <summary>Everything except the database and file locations (tests supply their own).</summary>
    public static IServiceCollection AddCrecheServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ICurrentUser, CurrentUser>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IChildService, ChildService>();
        services.AddSingleton<IFamilyService, FamilyService>();
        services.AddSingleton<IGroupService, GroupService>();
        services.AddSingleton<IEnrolmentService, EnrolmentService>();
        services.AddSingleton<IAttendanceService, AttendanceService>();
        services.AddSingleton<IActivityLogService, ActivityLogService>();
        services.AddSingleton<IBillingService, BillingService>();
        services.AddSingleton<IPaymentService, PaymentService>();
        services.AddSingleton<IStaffService, StaffService>();
        services.AddSingleton<IExpenseService, ExpenseService>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<ISearchService, SearchService>();
        return services;
    }
}
