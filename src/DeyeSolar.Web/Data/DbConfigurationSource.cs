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
        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<DeyeSolarDbContext>();
            optionsBuilder.UseSqlServer(_connectionString);

            using var db = new DeyeSolarDbContext(optionsBuilder.Options);
            if (!db.Database.CanConnect())
                return;

            // Check if AppSettings table exists
            try
            {
                foreach (var setting in db.AppSettings.AsNoTracking().ToList())
                {
                    Data[$"{setting.Section}:{setting.Key}"] = setting.Value;
                }
            }
            catch
            {
                // Table doesn't exist yet -- will be created by migration
            }
        }
        catch
        {
            // DB not ready yet
        }
    }

    public void Reload()
    {
        Data.Clear();
        Load();
        OnReload();
    }
}
