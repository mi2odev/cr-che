using Creche.Domain;
using Creche.Domain.Enums;
using Creche.Domain.Rules;

namespace Creche.Application.Common;

public sealed record UserSession(int Id, string Username, string DisplayName, UserRole Role, int? GroupId, bool MustChangePassword);

/// <summary>Who is signed in. Every service checks permissions through this, so hiding UI is never the only guard.</summary>
public interface ICurrentUser
{
    UserSession? User { get; }
    bool IsSignedIn { get; }
    bool Can(Permission permission);
    bool CanAccessGroup(int? groupId);
    /// <summary>Throws <see cref="PermissionDeniedException"/> unless the signed-in user holds the permission.</summary>
    void Demand(Permission permission);
    /// <summary>Throws unless the signed-in user holds at least one of the permissions.</summary>
    void DemandAny(params Permission[] permissions);
    void DemandGroup(int? groupId);
    void SignIn(UserSession session);
    void SignOut();
    event EventHandler? Changed;
}

public sealed class CurrentUser : ICurrentUser
{
    public UserSession? User { get; private set; }
    public bool IsSignedIn => User is not null;
    public event EventHandler? Changed;

    public bool Can(Permission permission) => User is not null && RolePermissions.Has(User.Role, permission);

    public bool CanAccessGroup(int? groupId) => User is not null && RolePermissions.CanAccessGroup(User.Role, User.GroupId, groupId);

    public void Demand(Permission permission)
    {
        if (User is null) throw new PermissionDeniedException("You must sign in first.");
        if (!RolePermissions.Has(User.Role, permission))
            throw new PermissionDeniedException($"Your role ({User.Role}) is not allowed to do this ({permission}).");
    }

    public void DemandAny(params Permission[] permissions)
    {
        if (User is null) throw new PermissionDeniedException("You must sign in first.");
        if (!permissions.Any(p => RolePermissions.Has(User.Role, p)))
            throw new PermissionDeniedException($"Your role ({User.Role}) is not allowed to do this.");
    }

    public void DemandGroup(int? groupId)
    {
        if (User is null) throw new PermissionDeniedException("You must sign in first.");
        if (!RolePermissions.CanAccessGroup(User.Role, User.GroupId, groupId))
            throw new PermissionDeniedException("You can only work with your own group.");
    }

    public void SignIn(UserSession session)
    {
        User = session;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        User = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public interface IClock
{
    DateTime Now { get; }
    DateOnly Today => DateOnly.FromDateTime(Now);
}

/// <summary>Where the application keeps its files. Everything lives under one local data folder.</summary>
public interface IAppPaths
{
    string DataFolder { get; }
    string DatabasePath { get; }
    string ImagesFolder { get; }
    string DocumentsFolder { get; }
    string BackupsFolder { get; }
    string LogsFolder { get; }
    string UiStatePath { get; }
}
