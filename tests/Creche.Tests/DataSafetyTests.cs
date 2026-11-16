using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Creche.Tests;

public class DataSafetyTests
{
    [Fact]
    public async Task Children_and_guardians_are_archived_never_deleted()
    {
        using var host = await TestHost.CreateAsync();
        var child = await host.EnrolAsync("Ines");
        var children = host.Get<IChildService>();
        var families = host.Get<IFamilyService>();

        Assert.Equal("ArchiveActiveChild", (await Assert.ThrowsAsync<BusinessRuleException>(() => children.ArchiveAsync(child.Id))).Code);
        var guardian = (await families.GetAsync(child.FamilyId))!.Guardians.Single();
        Assert.Equal("LastGuardian", (await Assert.ThrowsAsync<BusinessRuleException>(() => families.ArchiveGuardianAsync(guardian.Id))).Code);

        await host.Get<IEnrolmentService>().LeaveAsync(child.Id, new DateOnly(2026, 11, 1), "Moved");
        await children.ArchiveAsync(child.Id);
        await families.ArchiveAsync(child.FamilyId);

        Assert.Empty(await children.ListAsync(new ChildFilter()));
        Assert.Single(await children.ListAsync(new ChildFilter(IncludeArchived: true)));
        await using var db = host.Db();
        Assert.Equal(1, await db.Children.CountAsync());
        Assert.Equal(1, await db.Families.CountAsync());
        Assert.Equal(1, await db.Guardians.CountAsync());
    }

    [Fact]
    public async Task Leaving_and_re_enrolling_keeps_the_history()
    {
        using var host = await TestHost.CreateAsync();
        var child = await host.EnrolAsync("Ines");
        var enrolment = host.Get<IEnrolmentService>();

        await Assert.ThrowsAsync<BusinessRuleException>(() => enrolment.LeaveAsync(child.Id, new DateOnly(2026, 8, 1), null)); // before enrolment
        await enrolment.LeaveAsync(child.Id, new DateOnly(2026, 11, 10), "Moved away");
        var left = (await host.Get<IChildService>().GetAsync(child.Id))!;
        Assert.Equal(ChildStatus.Left, left.Status);
        Assert.Equal(new DateOnly(2026, 11, 10), left.LeaveDate);

        await enrolment.ReEnrolAsync(child.Id, "2027/2028", null, null, fee: 3000m);
        var back = (await host.Get<IChildService>().GetAsync(child.Id))!;
        Assert.Equal(ChildStatus.Active, back.Status);
        Assert.Equal("2027/2028", back.SchoolYear);
        Assert.Null(back.LeaveDate);
        Assert.Equal(3000m, Assert.Single(await host.Get<IBillingService>().ListInvoicesAsync(new InvoiceFilter())).Total);
        await Assert.ThrowsAsync<BusinessRuleException>(() => enrolment.ReEnrolAsync(child.Id, "2027/2028", null, null, 0m)); // twice
    }

    [Fact]
    public async Task Database_constraints_are_enforced()
    {
        using var host = await TestHost.CreateAsync();
        var child = await host.EnrolAsync("Ines");
        await using var db = host.Db();

        db.Attendance.Add(new AttendanceRecord { ChildId = child.Id, Date = new DateOnly(2026, 11, 2) });
        db.Attendance.Add(new AttendanceRecord { ChildId = child.Id, Date = new DateOnly(2026, 11, 2) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());   // one live record per child and day

        await using var db2 = host.Db();
        db2.Children.Add(new Child { FirstName = "Ghost", LastName = "X", FamilyId = 9999 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());  // foreign keys are on
    }

    [Fact]
    public async Task Demo_data_is_seeded_only_on_request_and_only_once()
    {
        using var host = await TestHost.CreateAsync();
        await using (var empty = host.Db())
            Assert.Equal(0, await empty.Children.CountAsync());

        var seeder = new DatabaseInitializer(host.Get<IDbContextFactory<CrecheDbContext>>(),
            Options.Create(new Application.Settings.DatabaseOptions { SeedDemoData = true }), NullLogger<DatabaseInitializer>.Instance);
        await seeder.InitializeAsync();
        await seeder.InitializeAsync();

        await using var db = host.Db();
        Assert.Equal(3, await db.Groups.CountAsync());
        Assert.Equal(4, await db.FeePlans.CountAsync());
        Assert.Equal(9, await db.Children.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Backup_and_restore_round_trip_with_a_safety_copy()
    {
        using var host = await TestHost.CreateAsync(onDisk: true);
        var backups = host.Get<IBackupService>();
        await host.EnrolAsync("Before");

        var backup = await backups.CreateBackupAsync();
        await host.EnrolAsync("After");
        Assert.Equal(2, (await host.Get<IChildService>().ListAsync(new ChildFilter())).Count);

        var safety = await backups.RestoreAsync(backup.Path);

        Assert.Equal("Before", Assert.Single(await host.Get<IChildService>().ListAsync(new ChildFilter())).FirstName);
        Assert.True(File.Exists(safety.Path));
        Assert.StartsWith(host.Paths.BackupsFolder, backup.Path);
        Assert.Equal(2, backups.ListBackups().Count);

        string notABackup = Path.Combine(host.Paths.DataFolder, "notes.db");
        await File.WriteAllTextAsync(notABackup, "this is not a database");
        Assert.Equal("BackupInvalid", (await Assert.ThrowsAsync<BusinessRuleException>(() => backups.RestoreAsync(notABackup))).Code);
    }

    [Fact]
    public async Task Automatic_backup_runs_once_a_day_and_prunes_old_files()
    {
        using var host = await TestHost.CreateAsync(onDisk: true);
        await host.UpdateSettingsAsync(s => s.BackupRetentionCount = 2);
        var backups = host.Get<IBackupService>();

        Assert.NotNull(await backups.RunAutomaticBackupIfDueAsync());
        Assert.Null(await backups.RunAutomaticBackupIfDueAsync());      // already done today
        foreach (int day in new[] { 17, 18, 19 })
        {
            host.Clock.Now = new DateTime(2026, 11, day, 9, 0, 0);
            Assert.NotNull(await backups.RunAutomaticBackupIfDueAsync());
        }

        var files = backups.ListBackups();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.IsAutomatic));
        Assert.Contains(files, f => f.FileName.Contains("20261119"));
    }

    [Fact]
    public void Csv_writer_quotes_and_neutralises_formulas()
    {
        var csv = new CsvWriter(';').Row("Name", "Amount").Row("Ben;ali \"Jr\"", 1234.5m).Row("=SUM(A1)", -12m, new DateOnly(2026, 11, 16)).ToString();
        Assert.Equal("Name;Amount\r\n\"Ben;ali \"\"Jr\"\"\";1234.50\r\n'=SUM(A1);-12.00;2026-11-16\r\n", csv);
    }

    [Fact]
    public void Money_helper_parses_both_decimal_separators()
    {
        Assert.True(Money.TryParse("12 500,50", out var a));
        Assert.Equal(12500.50m, a);
        Assert.True(Money.TryParse("99.999", out var b));
        Assert.Equal(100.00m, b);
        Assert.False(Money.TryParse("abc", out _));
    }
}
