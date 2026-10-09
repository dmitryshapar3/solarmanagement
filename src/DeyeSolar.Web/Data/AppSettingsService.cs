using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public sealed class AppSettingsService : IAppSettingsReader, IAppSettingsWriter
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
        foreach (var property in SettingsSchema.Properties(section, typeof(T)))
        {
            if (settings.TryGetValue(property.Name, out var value)) property.Apply(result!, value);
        }

        return result;
    }

    public Task SaveSectionAsync<T>(string section, T options) where T : class
        => SaveSectionsAsync(new Dictionary<string, object> { [section] = options });

    public async Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections, CancellationToken ct = default)
    {
        if (sections.Count == 0) return;
        foreach (var (section, options) in sections)
        {
            if (string.IsNullOrWhiteSpace(section) || section.Length > 128 || options is null) throw new ArgumentException("A settings section and its values are required.");
            if (section is "DeyeCloud" or "Shelly" || section.StartsWith("IntegrationRuntime", StringComparison.OrdinalIgnoreCase)
                || section.StartsWith("Integrations", StringComparison.OrdinalIgnoreCase)
                || section.StartsWith("Auth", StringComparison.OrdinalIgnoreCase) || section.StartsWith("AppleBilling", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Provider credentials and deployment security must be changed through their dedicated settings.");
        }
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var atomic = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await ApplySectionsAsync(db, sections, ct);
        await db.SaveChangesAsync(ct);
        await atomic.CommitAsync(ct);
        Reload();
    }

    internal T Defaults<T>(string section) where T : new()
    {
        var result = new T();
        _configuration.GetSection(section).Bind(result);
        return result;
    }
    internal void Reload() => _configurationRoot?.Reload();
    internal static async Task ApplySectionsAsync(DeyeSolarDbContext db, IReadOnlyDictionary<string, object> sections, CancellationToken ct)
    {
        var names = sections.Keys.ToArray();
        var existing = await db.AppSettings.Where(s => names.Contains(s.Section)).ToListAsync(ct);
        foreach (var (section, options) in sections)
        {
            var values = options is AppSettingsPatch patch
                ? patch.Values.Where(pair => SettingsSchema.IsRuntimeSetting(section, pair.Key)).Select(pair => new KeyValuePair<string, string>(pair.Key, InvariantSettingCodec.Format(pair.Value)))
                : SettingsSchema.Properties(section, options.GetType()).Where(property => property.CanRead).Select(property => new KeyValuePair<string, string>(property.Name, property.Read(options)));
            foreach (var (key, value) in values)
            {
                var setting = existing.SingleOrDefault(s => s.Section == section && s.Key == key);
                if (setting is null) db.AppSettings.Add(new AppSetting { Section = section, Key = key, Value = value });
                else setting.Value = value;
            }
        }
    }

}
