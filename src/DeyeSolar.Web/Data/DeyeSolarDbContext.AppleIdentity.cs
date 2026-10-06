using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public sealed class AppleIdentityCredential
{
    public string UserId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Audience { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
// Independent of Identity FK: a committed account deletion must not remove revocation work.
public sealed class AppleIdentityRevocation
{
    public Guid Id { get; set; }
    public string Audience { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
}
public partial class DeyeSolarDbContext
{
    public DbSet<AppleIdentityCredential> AppleIdentityCredentials => Set<AppleIdentityCredential>();
    public DbSet<AppleIdentityRevocation> AppleIdentityRevocations => Set<AppleIdentityRevocation>();
    private static void ConfigureAppleIdentity(ModelBuilder model)
    {
        model.Entity<AppleIdentityCredential>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.Subject).HasMaxLength(255);
            e.Property(x => x.Audience).HasMaxLength(255);
            e.Property(x => x.ProtectedRefreshToken).HasMaxLength(30000);
            e.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AppleIdentityRevocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Audience).HasMaxLength(255);
            e.Property(x => x.ProtectedRefreshToken).HasMaxLength(30000);
            e.HasIndex(x => x.NextAttemptAt);
        });
    }
}
