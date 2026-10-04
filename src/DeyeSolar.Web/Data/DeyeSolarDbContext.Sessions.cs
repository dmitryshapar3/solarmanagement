using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Data;
public sealed class AccountSessionEntity
{
    public string TokenHash { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? SecurityStamp { get; set; }
    public string? InstallationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
public partial class DeyeSolarDbContext
{
    public DbSet<AccountSessionEntity> AccountSessions => Set<AccountSessionEntity>();
    private static void ConfigureAccountSessions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountSessionEntity>(e =>
        {
            e.HasKey(s => s.TokenHash);
            e.Property(s => s.TokenHash).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
            e.Property(s => s.UserName).HasMaxLength(256);
            e.Property(s => s.SecurityStamp).HasMaxLength(256);
            e.Property(s => s.InstallationId).HasMaxLength(64);
            e.HasIndex(s => new { s.UserId, s.CreatedAt });
            e.HasIndex(s => s.ExpiresAt);
            e.HasOne<IdentityUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
