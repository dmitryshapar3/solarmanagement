using System.Reflection;
using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class AppSettingsService
{
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly IConfigurationRoot? _configurationRoot;

    public AppSettingsService(IDbContextFactory<DeyeSolarDbContext> dbFactory, IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;

        _configurationRoot = configuration as IConfigurationRoot;
    }

    public async Task<T> LoadSectionAsync<T>(string section) where T : new()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var settings = await db.AppSettings
            .Where(s => s.Section == section)
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var result = new T();
        // Runtime configuration contains this installation's defaults only, never another site's settings.
        _configuration.GetSection(section).Bind(result);
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanWrite)
                continue;
            if (section == "SolarEstimate" && prop.Name == "ApiKey")
                continue; // Only the runtime's captured server configuration supplies this key.

            if (settings.TryGetValue(prop.Name, out var value))
            {
                try
                {
                    var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    var converted = type == typeof(string) ? value : string.IsNullOrEmpty(value) && Nullable.GetUnderlyingType(prop.PropertyType) is not null
                        ? null : TypeDescriptor.GetConverter(type).ConvertFromInvariantString(value);
                    prop.SetValue(result, converted);
                }
                catch { }
            }
        }

        return result;
    }

    public async Task SaveSectionAsync<T>(string section, T options) where T : class
    {
        if (section is "DeyeCloud" or "Shelly" || section.StartsWith("IntegrationRuntime", StringComparison.OrdinalIgnoreCase)
            || section.StartsWith("Integrations", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider credentials must be changed through versioned integration settings. Publisher trust is configured by the operator.");
        await using var db = await _dbFactory.CreateDbContextAsync();

        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanRead)
                continue;

            var value = ToSettingValue(prop.GetValue(options));
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
        _configurationRoot?.Reload();
    }

    public async Task SeedSectionAsync<T>(string section) where T : new()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingKeys = await db.AppSettings
            .Where(s => s.Section == section)
            .Select(s => s.Key)
            .ToListAsync();
        var existingKeySet = new HashSet<string>(existingKeys);

        var defaults = new T();
        var configSection = _configuration.GetSection(section);

        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == "Section" || !prop.CanRead)
                continue;

            if (existingKeySet.Contains(prop.Name))
                continue;

            var configValue = configSection[prop.Name];
            var defaultValue = ToSettingValue(prop.GetValue(defaults));
            var value = configValue ?? defaultValue;

            db.AppSettings.Add(new AppSetting { Section = section, Key = prop.Name, Value = value });
        }

        await db.SaveChangesAsync();
    }

    internal static string ToSettingValue(object? value) => value switch
    {
        null => "",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
