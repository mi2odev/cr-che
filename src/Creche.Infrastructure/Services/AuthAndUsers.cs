using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Entities;
using Creche.Domain.Enums;
using Creche.Domain.Rules;
using Creche.Infrastructure.Persistence;
using Creche.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Creche.Infrastructure.Services;

public sealed class AuthService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, ILogger<AuthService> log) : ServiceBase(factory, user), IAuthService
{
    public const int MinimumPasswordLength = 6;

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        string name = (username ?? "").Trim().ToLowerInvariant();
        await using var db = await OpenAsync();
        var account = await db.Users.FirstOrDefaultAsync(u => u.Username == name);
        if (account is null)
        {
            // Spend the same time as a real check so a missing user is not detectable by timing.
            PasswordHasher.Hash(password ?? "");
            log.LogWarning("Failed sign-in for unknown user '{User}'", name);
            return new LoginResult(false, LoginFailure.InvalidCredentials, null);
        }
        if (!PasswordHasher.Verify(password ?? "", account.PasswordHash, account.PasswordSalt, account.PasswordIterations))
        {
            log.LogWarning("Failed sign-in for '{User}'", name);
            return new LoginResult(false, LoginFailure.InvalidCredentials, null);
        }
        if (!account.IsActive) return new LoginResult(false, LoginFailure.Disabled, null);

        account.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var session = new UserSession(account.Id, account.Username, account.DisplayName, account.Role, account.GroupId, account.MustChangePassword);
        User.SignIn(session);
        log.LogInformation("'{User}' signed in as {Role}", name, account.Role);
        return new LoginResult(true, LoginFailure.None, session);
    }

    public void Logout() => User.SignOut();

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var session = User.User ?? throw new PermissionDeniedException("You must sign in first.");
        EnsureStrong(newPassword);
        await using var db = await OpenAsync();
        var account = await db.Users.FirstAsync(u => u.Id == session.Id);
        if (!PasswordHasher.Verify(currentPassword ?? "", account.PasswordHash, account.PasswordSalt, account.PasswordIterations))
            throw new BusinessRuleException("WrongPassword", "The current password is not correct.");
        (account.PasswordHash, account.PasswordSalt, account.PasswordIterations) = PasswordHasher.Hash(newPassword);
        account.MustChangePassword = false;
        await db.SaveChangesAsync();
        User.SignIn(session with { MustChangePassword = false });
    }

    public static void EnsureStrong(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            throw new BusinessRuleException("PasswordTooShort", $"The password must have at least {MinimumPasswordLength} characters.", MinimumPasswordLength);
    }
}

public sealed class UserService(IDbContextFactory<CrecheDbContext> factory, ICurrentUser user) : ServiceBase(factory, user), IUserService
{
    public async Task<List<UserInfo>> ListAsync()
    {
        User.Demand(Permission.Users);
        await using var db = await OpenAsync();
        return await db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new UserInfo(u.Id, u.Username, u.DisplayName, u.Role, u.GroupId, u.Group != null ? u.Group.Name : null, u.IsActive, u.LastLoginAt))
            .ToListAsync();
    }

    public async Task<int> SaveAsync(UserEdit edit)
    {
        User.Demand(Permission.Users);
        string username = Required(edit.Username, "Username").ToLowerInvariant();
        string displayName = Required(edit.DisplayName, "DisplayName");

        await using var db = await OpenAsync();
        if (await db.Users.AnyAsync(u => u.Username == username && u.Id != edit.Id))
            throw new BusinessRuleException("UsernameTaken", $"The user name '{username}' is already used.", username);

        User account;
        if (edit.Id == 0)
        {
            AuthService.EnsureStrong(edit.NewPassword);
            account = new User();
            db.Users.Add(account);
        }
        else
        {
            account = await db.Users.FirstOrDefaultAsync(u => u.Id == edit.Id)
                ?? throw new BusinessRuleException("NotFound", "The user no longer exists.");
            bool wasActiveAdmin = account is { Role: UserRole.Admin, IsActive: true };
            bool staysActiveAdmin = edit is { Role: UserRole.Admin, IsActive: true };
            if (wasActiveAdmin && !staysActiveAdmin)
                RolePermissions.EnsureAdminRemains(await CountOtherActiveAdminsAsync(db, account.Id));
        }

        account.Username = username;
        account.DisplayName = displayName;
        account.Role = edit.Role;
        account.GroupId = edit.Role == UserRole.Educator ? edit.GroupId : null;
        account.IsActive = edit.IsActive;
        if (!string.IsNullOrEmpty(edit.NewPassword))
        {
            AuthService.EnsureStrong(edit.NewPassword);
            (account.PasswordHash, account.PasswordSalt, account.PasswordIterations) = PasswordHasher.Hash(edit.NewPassword);
            // Someone else chose this password: its owner must replace it at next sign-in.
            account.MustChangePassword = account.Id != User.User!.Id;
        }
        await db.SaveChangesAsync();
        return account.Id;
    }

    public async Task SetActiveAsync(int userId, bool active)
    {
        User.Demand(Permission.Users);
        await using var db = await OpenAsync();
        var account = await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new BusinessRuleException("NotFound", "The user no longer exists.");
        if (!active && account is { Role: UserRole.Admin, IsActive: true })
            RolePermissions.EnsureAdminRemains(await CountOtherActiveAdminsAsync(db, account.Id));
        account.IsActive = active;
        await db.SaveChangesAsync();
    }

    private static Task<int> CountOtherActiveAdminsAsync(CrecheDbContext db, int exceptId)
        => db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != exceptId);
}
