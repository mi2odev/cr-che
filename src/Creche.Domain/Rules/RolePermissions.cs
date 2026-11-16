using Creche.Domain.Enums;

namespace Creche.Domain.Rules;

/// <summary>The single source of truth for what each role may do.</summary>
public static class RolePermissions
{
    private static readonly HashSet<Permission> Secretary =
    [
        Permission.Dashboard, Permission.Children, Permission.Families, Permission.GroupsView,
        Permission.Enrolment, Permission.Billing, Permission.Payments, Permission.Search,
    ];

    private static readonly HashSet<Permission> Educator =
    [
        Permission.Attendance, Permission.ActivityLog, Permission.Search,
    ];

    public static bool Has(UserRole role, Permission permission) => role switch
    {
        UserRole.Admin => true,
        UserRole.Secretary => Secretary.Contains(permission),
        UserRole.Educator => Educator.Contains(permission),
        _ => false,
    };

    /// <summary>Educators only ever work with their own group; other roles are not restricted by group.</summary>
    public static bool CanAccessGroup(UserRole role, int? userGroupId, int? targetGroupId)
        => role != UserRole.Educator || (userGroupId is not null && userGroupId == targetGroupId);

    /// <summary>There must always be at least one active admin.</summary>
    public static void EnsureAdminRemains(int activeAdminsAfterChange)
    {
        if (activeAdminsAfterChange < 1)
            throw new BusinessRuleException("LastAdmin", "There must always be at least one active administrator.");
    }
}
