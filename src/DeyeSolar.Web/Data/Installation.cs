using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Data;

public static class InstallationIds
{
    public const string ClaimType = "solar:installation";
    public const string RoleClaimType = "solar:installation-role";
}

public class Installation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "My solar installation";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    // A temporary deletion fence is recoverable after a process crash. These markers
    // are cleared on success/rollback or by startup recovery before workers admit data.
    public string? OffboardingUserId { get; set; }
    public bool? OffboardingWasEnabled { get; set; }
}

public class InstallationMembership
{
    public string InstallationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = "Owner";
    public Installation Installation { get; set; } = null!;
    public IdentityUser User { get; set; } = null!;
}

// Request/circuit state is immutable after binding. No implicit installation fallback.
public sealed class CurrentInstallation
{
    public string? Id { get; private set; }
    public void BindOnce(string installationId)
    {
        if (string.IsNullOrWhiteSpace(installationId) || installationId.Length > 64)
            throw new ArgumentException("A valid installation is required.", nameof(installationId));
        if (Id is not null && Id != installationId)
            throw new InvalidOperationException("This request or circuit is already bound to another installation.");
        Id = installationId;
    }
}
