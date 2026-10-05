namespace DeyeSolar.Web.Auth;

/// <summary>The same role matrix serves in-memory authorization and translatable SQL predicates.</summary>
public static class InstallationPermissionPolicy
{
    public static string[] AllowedRoles(InstallationPermission permission) => permission switch
    {
        InstallationPermission.Read => ["Owner", "IntegrationManager", "Operator", "Viewer"],
        InstallationPermission.ManageIntegrations => ["Owner", "IntegrationManager"],
        InstallationPermission.ManageRules or InstallationPermission.ControlDevices => ["Owner", "Operator"],
        InstallationPermission.ManageSettings => ["Owner"],
        _ => []
    };

    public static bool Allows(string role, InstallationPermission permission)
        => AllowedRoles(permission).Contains(role, StringComparer.Ordinal);
}
