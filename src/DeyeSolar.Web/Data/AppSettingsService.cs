using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class AppSettingsService
{
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly DbConfigurationProvider? _dbProvider;

    public AppSettingsService(IDbContextFactory<DeyeSolarDbContext> dbFactory, IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;

        if (configuration is IConfigurationRoot root)
        {
            _dbProvider = root.Providers.OfType<DbConfigurationProvider>().FirstOrDefault();
        }
    }

    public async Task<T> LoadSectionAsync<T>(string section) where T : new()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var settings = await db.AppSettings
            .Where(s => s.Section == section)
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var result = new T();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanWrite)
                continue;

            if (settings.TryGetValue(prop.Name, out var value) && !string.IsNullOrEmpty(value))
            {
                try
                {
                    var converted = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(result, converted);
                }
                catch { }
            }
        }

        return result;
    }

    public async Task SaveSectionAsync<T>(string section, T options) where T : class
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanRead)
                continue;

            var value = prop.GetValue(options)?.ToString() ?? "";
            var existing = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Section == section && s.Key == prop.Name);

            if (existing != null)
            {
                existing.Value = value;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Section = section, Key = prop.Name, Value = value });
            }
        }

        await db.SaveChangesAsync();
        _dbProvider?.Reload();
    }

    public async Task SeedSectionAsync<T>(string section) where T : new()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingSettings = await db.AppSettings
            .Where(s => s.Section == section)
            .ToListAsync();
        var existingByKey = existingSettings.ToDictionary(s => s.Key, s => s);

        var defaults = new T();
        var configSection = _configuration.GetSection(section);

        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanRead)
                continue;

            var configValue = configSection[prop.Name];
            var defaultValue = prop.GetValue(defaults)?.ToString() ?? "";
            var hasConfigOverride = !string.IsNullOrEmpty(configValue);
            var value = hasConfigOverride ? configValue! : defaultValue;

            if (existingByKey.TryGetValue(prop.Name, out var existing))
            {
                if (hasConfigOverride && !string.Equals(existing.Value, value, StringComparison.Ordinal))
                    existing.Value = value;
                continue;
            }

            db.AppSettings.Add(new AppSetting { Section = section, Key = prop.Name, Value = value });
        }

        await db.SaveChangesAsync();
    }
}
