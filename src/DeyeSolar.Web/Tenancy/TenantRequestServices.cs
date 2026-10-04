using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Billing;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tenancy;

public static class TenantRequestServices
{
    public static IServiceCollection AddTenantRequestServices(this IServiceCollection services, IConfiguration deployment, string? solarApiKey)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new TenantRuntimeFactory(
            provider.GetRequiredService<DbContextOptions<DeyeSolarDbContext>>(), deployment,
            provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<IIntegrationRuntimeExecutor>(),
            provider.GetRequiredService<IntegrationSecretStore>(), provider.GetRequiredService<IntegrationChangeNotifier>(), solarApiKey,
            provider.GetRequiredService<LegacyIntegrationBootstrap>(), provider.GetRequiredService<BillingAccessService>()));
        services.AddSingleton<TenantRuntimeRegistry>();
        services.AddScoped<IDbContextFactory<DeyeSolarDbContext>, RequestDbContextFactory>();
        Add<AppSettingsService>(services);
        Add<IInverterDataSource>(services);
        Add<IExportGridHistorySource>(services);
        Add<IExportReadingStore>(services);
        Add<IExportPriceStore>(services);
        Add<IExportPriceSource>(services);
        Add<IExportSalesService>(services);
        Add<ISocketController>(services);
        Add<ISocketInventoryService>(services);
        Add<ISolarRadiationSource>(services);
        Add<ISolarEstimateStore>(services);
        Add<SolarEstimateService>(services);
        Add<ISolarHistoryRadiationSource>(services);
        Add<ISolarHistoryStore>(services);
        Add<InverterDataSnapshot>(services);
        Add<DeviceStatusSnapshot>(services);
        Add<RuleEvaluator>(services);
        Add<IRuleRepository>(services);
        Add<IntegrationProbeGate>(services);
        Add<IDeviceLabelStore>(services);
        Add<DeviceNameService>(services);
        Add<SiteSettingsService>(services);
        Add<IIntegrationTestService>(services);
        // The request container must not dispose resources owned by the installation container.
        services.AddScoped<IInverterRefreshService>(provider => new RefreshBorrow(Resolve<IInverterRefreshService>(provider)));
        services.AddScoped<ISolarHistoryService>(provider => new HistoryBorrow(Resolve<ISolarHistoryService>(provider)));
        AddOptions<DeyeCloudOptions>(services);
        AddOptions<ShellyOptions>(services);
        AddOptions<InverterConnectionOptions>(services);
        Add<IInverterCatalog>(services);
        Add<ISmartSocketCatalog>(services);
        Add<ISocketCommandTracker>(services);
        Add<DynamicSocketGateway>(services);
        AddOptions<PollingOptions>(services);
        AddOptions<DisplayOptions>(services);
        AddOptions<SolarEstimateOptions>(services);
        AddOptions<SolarSalesOptions>(services);
        services.AddHostedService<TenantRuntimeWorker>();
        return services;
    }

    private static T Resolve<T>(IServiceProvider provider) where T : notnull
    {
        var id = provider.GetRequiredService<CurrentInstallation>().Id
            ?? throw new InvalidOperationException("An authenticated installation must be bound before reading private services.");
        return provider.GetRequiredService<TenantRuntimeRegistry>().Resolve<T>(id);
    }
    private static void Add<T>(IServiceCollection services) where T : class => services.AddScoped(Resolve<T>);
    private static void AddOptions<T>(IServiceCollection services) where T : class
        => services.AddScoped<IOptionsMonitor<T>>(provider => new OptionsBorrow<T>(Resolve<IOptionsMonitor<T>>(provider)));

    private sealed class RefreshBorrow(IInverterRefreshService inner) : IInverterRefreshService
    {
        public Task<InverterData> RefreshAsync(CancellationToken ct) => inner.RefreshAsync(ct);
    }
    private sealed class HistoryBorrow(ISolarHistoryService inner) : ISolarHistoryService
    {
        public Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null)
            => inner.ReadAsync(period, ct, endDate);
    }
    private sealed class OptionsBorrow<T>(IOptionsMonitor<T> inner) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => inner.CurrentValue;
        public T Get(string? name) => inner.Get(name);
        public IDisposable? OnChange(Action<T, string?> listener) => inner.OnChange(listener);
    }
}
