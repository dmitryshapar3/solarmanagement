using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tenancy;

internal sealed class TenantSettingsConfigurationSource(TenantDbContextFactory factory,
    IReadOnlyDictionary<string, string?> defaults) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new TenantSettingsConfigurationProvider(factory, defaults);
}

/// <summary>No global SQL provider, environment variables or other installation's defaults are in this configuration.</summary>
internal sealed class TenantSettingsConfigurationProvider(TenantDbContextFactory factory,
    IReadOnlyDictionary<string, string?> defaults) : ConfigurationProvider
{
    public override void Load()
    {
        var next = new Dictionary<string, string?>(defaults, StringComparer.OrdinalIgnoreCase);
        using var db = factory.CreateDbContext();
        var settings = db.AppSettings.AsNoTracking().ToList();
        foreach (var setting in settings)
            if (TenantRuntimeOptions.KnownSetting(setting.Section, setting.Key)) next[$"{setting.Section}:{setting.Key}"] = setting.Value;
        if (settings.Any(setting => setting.Section == LegacyIntegrationBootstrap.MarkerSection
            && setting.Key == LegacyIntegrationBootstrap.MarkerKey && setting.Value == "1"))
        {
            // A completed cutover cannot reactivate deployment fallback credentials on reload.
            next["DeyeCloud:AppSecret"] = "";
            next["DeyeCloud:Password"] = "";
            next["Shelly:AuthKey"] = "";
        }
        Data = next;
    }
}
