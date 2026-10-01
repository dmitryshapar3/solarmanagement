using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeyeSolar.Web.Data;

// Model generation must never run startup migration/seeding or contact a deployed database.
public sealed class DeyeSolarDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DeyeSolarDbContext>
{
    public DeyeSolarDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(
            "Server=localhost;Database=SolarManagementDesign;Integrated Security=True;TrustServerCertificate=True").Options);
}
