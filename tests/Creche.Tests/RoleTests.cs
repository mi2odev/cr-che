using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Creche.Infrastructure.Security;

namespace Creche.Tests;

public class RoleTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    [Fact]
    public void Admin_can_do_everything()
        => Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(UserRole.Admin, p)));

    [Theory]
    [InlineData(Permission.Children, true)]
    [InlineData(Permission.Families, true)]
    [InlineData(Permission.Enrolment, true)]
    [InlineData(Permission.Billing, true)]
    [InlineData(Permission.Payments, true)]
    [InlineData(Permission.Users, false)]
    [InlineData(Permission.Settings, false)]
    [InlineData(Permission.Expenses, false)]
    [InlineData(Permission.Staff, false)]
    [InlineData(Permission.Reports, false)]
    [InlineData(Permission.Backup, false)]
    [InlineData(Permission.FeePlansManage, false)]
    public void Secretary_permissions(Permission permission, bool allowed)
        => Assert.Equal(allowed, RolePermissions.Has(UserRole.Secretary, permission));

    [Theory]
    [InlineData(Permission.Attendance, true)]
    [InlineData(Permission.ActivityLog, true)]
    [InlineData(Permission.Children, false)]
    [InlineData(Permission.Families, false)]
    [InlineData(Permission.Payments, false)]
    [InlineData(Permission.Billing, false)]
    [InlineData(Permission.Users, false)]
    public void Educator_permissions(Permission permission, bool allowed)
        => Assert.Equal(allowed, RolePermissions.Has(UserRole.Educator, permission));

    [Fact]
    public async Task Nothing_is_readable_without_signing_in()
    {
        using var host = await TestHost.CreateAsync();
        var child = await host.EnrolAsync("Ines");
        host.SignOut();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IChildService>().ListAsync(new ChildFilter()));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IChildService>().GetAsync(child.Id));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IFamilyService>().ListAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IAttendanceService>().GetRollCallAsync(null, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IPaymentService>().ListPaymentsAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<ISearchService>().SearchAsync("Ines"));
        Assert.Throws<PermissionDeniedException>(() => host.Get<IBackupService>().ListBackups());
    }

    [Fact]
    public async Task Secretary_handles_children_and_money_but_not_administration()
    {
        using var host = await TestHost.CreateAsync();
        var plan = await host.AddPlanAsync();
        host.SignIn(UserRole.Secretary, userId: 2);

        var child = await host.EnrolAsync("Ines", planId: plan.Id);
        await host.Get<IBillingService>().GenerateMonthlyInvoicesAsync(2026, 11);
        await host.Get<IPaymentService>().RecordPaymentAsync(new PaymentRequest(child.FamilyId, 1000m, PaymentMethod.Cash, Today));

        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IUserService>().ListAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IExpenseService>().ListAsync(Today, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IStaffService>().ListAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IReportService>().GetAsync(Today, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IBillingService>().SaveFeePlanAsync(new FeePlan { Name = "X" }));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IGroupService>().SaveAsync(new Group { Name = "X", Capacity = 3 }));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IBackupService>().CreateBackupAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.UpdateSettingsAsync(s => s.Name = "Hacked"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IAttendanceService>().CheckInAsync(child.Id, Today, new TimeOnly(8, 0), null));
    }

    [Fact]
    public async Task Educator_works_only_with_their_own_group()
    {
        using var host = await TestHost.CreateAsync();
        var mine = await host.AddGroupAsync("Mine");
        var other = await host.AddGroupAsync("Other");
        var myChild = await host.EnrolAsync("Mine", groupId: mine.Id);
        var otherChild = await host.EnrolAsync("Other", groupId: other.Id);
        host.SignIn(UserRole.Educator, groupId: mine.Id, userId: 3);
        var attendance = host.Get<IAttendanceService>();
        var log = host.Get<IActivityLogService>();

        // Own group: allowed.
        Assert.Equal([mine.Id], (await attendance.GetAccessibleGroupsAsync()).Select(g => g.Id).ToArray());
        Assert.Single(await attendance.GetRollCallAsync(mine.Id, Today));
        await attendance.CheckInAsync(myChild.Id, Today, new TimeOnly(8, 0), null);
        await log.SaveAsync(new ActivityLogEntry { ChildId = myChild.Id, Date = Today, Type = ActivityType.Meal, Description = "Ate well" });
        Assert.Single(await log.ListAsync(Today, mine.Id));
        Assert.Equal(myChild.Id, Assert.Single(await host.Get<ISearchService>().SearchAsync("Test")).Id);

        // Other group, everything at once, and the rest of the application: refused.
        await Assert.ThrowsAsync<PermissionDeniedException>(() => attendance.GetRollCallAsync(other.Id, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => attendance.GetRollCallAsync(null, Today));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => attendance.CheckInAsync(otherChild.Id, Today, new TimeOnly(8, 0), null));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => attendance.CheckInAllAsync(other.Id, Today, new TimeOnly(8, 0)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => attendance.GetMonthlySheetAsync(otherChild.Id, 2026, 11));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => log.SaveAsync(new ActivityLogEntry { ChildId = otherChild.Id, Date = Today, Description = "x" }));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => log.ListAsync(Today, other.Id));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IChildService>().GetAsync(myChild.Id));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IFamilyService>().ListAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IPaymentService>().GetDebtorsAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IDashboardService>().GetAsync());
    }

    [Fact]
    public async Task An_educator_without_a_group_can_access_no_group()
    {
        using var host = await TestHost.CreateAsync();
        var group = await host.AddGroupAsync();
        host.SignIn(UserRole.Educator, groupId: null, userId: 3);
        Assert.Empty(await host.Get<IAttendanceService>().GetAccessibleGroupsAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => host.Get<IAttendanceService>().GetRollCallAsync(group.Id, Today));
    }

    [Fact]
    public async Task The_admin_account_is_always_created_and_must_change_its_password()
    {
        using var host = await TestHost.CreateAsync();
        host.SignOut();
        var auth = host.Get<IAuthService>();

        Assert.Equal(LoginFailure.InvalidCredentials, (await auth.LoginAsync("admin", "wrong")).Failure);
        Assert.Equal(LoginFailure.InvalidCredentials, (await auth.LoginAsync("nobody", "admin")).Failure);
        var login = await auth.LoginAsync(" Admin ", DatabaseInitializer.DefaultAdminPassword);

        Assert.True(login.Success);
        Assert.Equal(UserRole.Admin, login.Session!.Role);
        Assert.True(login.Session.MustChangePassword);

        await Assert.ThrowsAsync<BusinessRuleException>(() => auth.ChangePasswordAsync("admin", "123"));        // too short
        await Assert.ThrowsAsync<BusinessRuleException>(() => auth.ChangePasswordAsync("wrong", "s3cret-pass")); // wrong current
        await auth.ChangePasswordAsync("admin", "s3cret-pass");
        auth.Logout();
        Assert.False((await auth.LoginAsync("admin", "admin")).Success);
        Assert.True((await auth.LoginAsync("admin", "s3cret-pass")).Success);
    }

    [Fact]
    public async Task There_is_always_at_least_one_active_admin()
    {
        using var host = await TestHost.CreateAsync();
        var users = host.Get<IUserService>();
        var admin = (await users.ListAsync()).Single();

        Assert.Equal("LastAdmin", (await Assert.ThrowsAsync<BusinessRuleException>(() => users.SetActiveAsync(admin.Id, false))).Code);
        Assert.Equal("LastAdmin", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            users.SaveAsync(new UserEdit { Id = admin.Id, Username = admin.Username, DisplayName = admin.DisplayName, Role = UserRole.Secretary }))).Code);

        // With a second admin the first one may be disabled; then the second becomes the last.
        int second = await users.SaveAsync(new UserEdit { Username = "Boss", DisplayName = "Boss", Role = UserRole.Admin, NewPassword = "another-pass" });
        await users.SetActiveAsync(admin.Id, false);
        await Assert.ThrowsAsync<BusinessRuleException>(() => users.SetActiveAsync(second, false));
    }

    [Fact]
    public async Task Users_are_disabled_not_deleted_and_disabled_users_cannot_sign_in()
    {
        using var host = await TestHost.CreateAsync();
        var users = host.Get<IUserService>();
        int id = await users.SaveAsync(new UserEdit { Username = "sec", DisplayName = "Secretary", Role = UserRole.Secretary, NewPassword = "secret-1" });
        await Assert.ThrowsAsync<BusinessRuleException>(() => users.SaveAsync(new UserEdit { Username = "SEC", DisplayName = "Dup", NewPassword = "secret-2" }));

        await users.SetActiveAsync(id, false);

        Assert.Equal(2, (await users.ListAsync()).Count);
        host.SignOut();
        Assert.Equal(LoginFailure.Disabled, (await host.Get<IAuthService>().LoginAsync("sec", "secret-1")).Failure);
    }

    [Fact]
    public void Passwords_are_hashed_with_pbkdf2_and_a_random_salt()
    {
        var first = PasswordHasher.Hash("correct horse");
        var second = PasswordHasher.Hash("correct horse");

        Assert.Equal(100_000, first.Iterations);
        Assert.Equal(16, Convert.FromBase64String(first.Salt).Length);
        Assert.Equal(32, Convert.FromBase64String(first.Hash).Length);
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(PasswordHasher.Verify("correct horse", first.Hash, first.Salt, first.Iterations));
        Assert.False(PasswordHasher.Verify("Correct horse", first.Hash, first.Salt, first.Iterations));
        Assert.False(PasswordHasher.Verify("correct horse", "not-base64!", first.Salt, first.Iterations));
    }
}
