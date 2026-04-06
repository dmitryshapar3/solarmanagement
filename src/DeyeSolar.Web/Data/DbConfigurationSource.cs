using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class DbConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;

    public DbConfigurationSource(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new DbConfigurationProvider(_connectionString);
}

public class DbConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public DbConfigurationProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public override void Load()
    {
        var optionsBuilder = new DbContextOptionsBuilder<DeyeSolarDbContext>();
        optionsBuilder.UseSqlite(_connectionString);

        using var db = new DeyeSolarDbContext(optionsBuilder.Options);

        try
        {
            // Table may not exist yet on first run
            if (!db.Database.GetAppliedMigrations().Any() && !db.Database.CanConnect())
                return;

            foreach (var setting in db.AppSettings.AsNoTracking().ToList())
            {
                Data[$"{setting.Section}:{setting.Key}"] = setting.Value;
            }
        }
        catch
        {
            // DB not ready yet -- will be populated after startup seed
        }
    }

    public void Reload()
    {
        Data.Clear();
        Load();
        OnReload();
    }
}
