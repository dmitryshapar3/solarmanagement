using DeyeSolar.Web.Integrations;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.Settlement;
using DeyeSolar.Infrastructure.Solar;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tenancy;

/// <summary>Builds completely separate credentials, options, HTTP clients and private service state per installation.</summary>
public sealed class TenantRuntimeFactory(DbContextOptions<DeyeSolarDbContext> databaseOptions,
    ILoggerFactory loggers, TimeProvider clock, IHostApplicationLifetime hostLifetime,
    IIntegrationRuntimeExecutor integrationExecutor, IntegrationSecretStore integrationSecrets,
    IntegrationChangeNotifier integrationChanges, string? serverSolarApiKey = null,
    IBillingAccessReader? billing = null, ITrialSocketQuota? quota = null) : IDisposable
{
    private readonly SemaphoreSlim _requests = new(8, 8);

    public async Task<TenantRuntime> CreateAsync(string installationId, CancellationToken ct = default)
    {
        var factory = new TenantDbContextFactory(databaseOptions, installationId);
        var defaults = TenantRuntimeOptions.Defaults(clock.GetUtcNow());
        // Operator weather credentials are shared infrastructure, never editable installation settings.
        defaults["SolarEstimate:ApiKey"] = serverSolarApiKey ?? "";
        await using (var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            if (!await db.Installations.AnyAsync(installation => installation.Id == installationId && installation.IsEnabled, ct).ConfigureAwait(false))
                throw new InvalidOperationException("The installation is unavailable.");
            var existing = await db.AppSettings.AsNoTracking().Select(setting => new { setting.Section, setting.Key }).ToListAsync(ct).ConfigureAwait(false);
            var keys = existing.Select(setting => $"{setting.Section}:{setting.Key}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (section, key, value) in SettingsSchema.RuntimeEntries(defaults))
            {
                if (keys.Contains($"{section}:{key}")) continue;
                db.AppSettings.Add(new AppSetting { Section = section, Key = key, Value = value });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        var configuration = new ConfigurationBuilder().Add(new TenantSettingsConfigurationSource(factory, defaults)).Build();
        return BuildRuntime(factory, configuration);
    }

    internal TenantRuntime BuildRuntime(TenantDbContextFactory factory, IConfigurationRoot configuration,
        Action<IServiceCollection>? configureServices = null)
    {
        var lifetime = new TenantRuntimeLifetime(hostLifetime.ApplicationStopping);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(loggers);
        services.AddSingleton(clock);
        if (billing is not null) services.AddSingleton<IBillingAccessReader>(billing);
        if (quota is not null) services.AddSingleton<ITrialSocketQuota>(quota);
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(factory);
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<IAppSettingsReader>(provider => provider.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IAppSettingsWriter>(provider => provider.GetRequiredService<AppSettingsService>());
        services.Configure<PollingOptions>(configuration.GetSection(PollingOptions.Section));
        services.Configure<DisplayOptions>(configuration.GetSection("Display"));
        services.Configure<SolarEstimateOptions>(configuration.GetSection(SolarEstimateOptions.Section));
        services.Configure<SolarSalesOptions>(configuration.GetSection(SolarSalesOptions.Section));

        services.AddSingleton(integrationExecutor);
        services.AddSingleton(integrationSecrets);
        services.AddSingleton(integrationChanges);
        services.AddSingleton<IIntegrationRegistry, IntegrationRegistry>();
        services.AddSingleton<InverterSelectionMonitor>();
        services.AddSingleton<IOptionsMonitor<InverterConnectionOptions>>(provider => provider.GetRequiredService<InverterSelectionMonitor>());
        services.AddSingleton<DynamicInverterGateway>();
        services.AddSingleton<IInverterCatalog>(provider => provider.GetRequiredService<DynamicInverterGateway>());
        services.AddSingleton<DynamicSocketGateway>();
        services.AddSingleton<IAccountSocketCatalog>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddSingleton<IAccountSocketControl>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddSingleton<ISmartSocketCatalog>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddSingleton<ISocketCommandTracker>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddHttpClient<IPseJsonReader, PseJsonReader>(client => client.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(SolarManagement.Http.PublicHttpTransport.CreateHandler)
            .AddHttpMessageHandler(() => new TenantRequestGate(_requests, lifetime.ApplicationStopping));
        services.AddTransient<PseExportPriceClient>();
        services.AddHttpClient<ExportPriceFeedClient>(client => client.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(SolarManagement.Http.PublicHttpTransport.CreateHandler)
            .AddHttpMessageHandler(() => new TenantRequestGate(_requests, lifetime.ApplicationStopping));
        services.AddOpenMeteoSolarClients(transport => transport
            .AddHttpMessageHandler(() => new TenantRequestGate(_requests, lifetime.ApplicationStopping)));
        services.AddSingleton<IInverterDataSource>(provider => provider.GetRequiredService<DynamicInverterGateway>());
        services.AddSingleton<IExportGridHistorySource>(provider => provider.GetRequiredService<DynamicInverterGateway>());
        services.AddSingleton<ExportReadingStore>();
        services.AddSingleton<IExportReadingStore>(provider => provider.GetRequiredService<ExportReadingStore>());
        services.AddSingleton<IExportPriceStore, ExportPriceStore>();
        services.AddSingleton<IExportPriceSource, ConfiguredExportPriceSource>();
        services.AddSingleton<IExportSalesService, ExportSalesService>();
        services.AddSingleton<ISocketController>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddSingleton<ISocketInventoryService>(provider => provider.GetRequiredService<DynamicSocketGateway>());
        services.AddSingleton<ISolarRadiationSource>(provider => provider.GetRequiredService<OpenMeteoCurrentSolarClient>());
        services.AddSingleton<ISolarEstimateStore, SolarEstimateStore>();
        services.AddSingleton<SolarEstimateService>();
        services.AddSingleton<ISolarHistoryRadiationSource>(provider => provider.GetRequiredService<OpenMeteoSolarHistoryClient>());
        services.AddSingleton<ISolarDayForecastSource>(provider => provider.GetRequiredService<OpenMeteoSolarHistoryClient>());
        services.AddSingleton<ISolarHistoryStore, SolarHistoryStore>();
        services.AddSingleton<ISolarHistoryService, SolarHistoryService>();
        services.AddSingleton<Redesign.SolarProductionService>();
        services.AddSingleton<InverterDataSnapshot>();
        services.AddSingleton<IInverterRefreshService, InverterRefreshService>();
        services.AddSingleton<DeviceStatusSnapshot>();
        services.AddSingleton<RuleEvaluator>();
        services.AddSingleton<IRuleDecisionEvaluator>(provider => provider.GetRequiredService<RuleEvaluator>());
        services.AddSingleton<IRuleRepository, RuleRepository>();
        services.AddSingleton<IRuleRunHistory>(provider => new RuleRunHistory(provider.GetRequiredService<IDbContextFactory<DeyeSolarDbContext>>(),
            provider.GetRequiredService<ILogger<RuleRunHistory>>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRuleObservationReconciler>(provider => new RuleObservationReconciler(provider.GetRequiredService<IDbContextFactory<DeyeSolarDbContext>>(),
            provider.GetRequiredService<ISocketController>()));
        services.AddSingleton<ISocketReceiptReconciler>(provider => new SocketReceiptReconciler(provider.GetRequiredService<IDbContextFactory<DeyeSolarDbContext>>(),
            provider.GetRequiredService<ISocketCommandTracker>(), provider.GetRequiredService<ILogger<SocketReceiptReconciler>>()));
        services.AddSingleton<IRuleAutomationExecutor>(provider => new RuleAutomationExecutor(provider.GetRequiredService<ISocketController>(),
            provider.GetRequiredService<IRuleRepository>(), provider.GetRequiredService<IRuleDecisionEvaluator>(), provider.GetRequiredService<IAppSettingsReader>(),
            provider.GetRequiredService<IRuleRunHistory>(), provider.GetRequiredService<IRuleObservationReconciler>(), provider.GetRequiredService<ILogger<RuleAutomationExecutor>>()));
        services.AddSingleton<IRulePollingCycle>(provider => new RulePollingCycle(
            provider.GetRequiredService<IInverterRefreshService>(), provider.GetRequiredService<IOptionsMonitor<InverterConnectionOptions>>(),
            provider.GetRequiredService<IRuleRepository>(), provider.GetRequiredService<IDbContextFactory<DeyeSolarDbContext>>(),
            provider.GetRequiredService<IRuleRunHistory>(), provider.GetRequiredService<IRuleAutomationExecutor>(), provider.GetRequiredService<ISocketReceiptReconciler>(),
            provider.GetRequiredService<ILogger<RulePollingCycle>>(), provider.GetRequiredService<IInverterDataSource>(), provider.GetRequiredService<ExportReadingStore>()));
        services.AddSingleton(provider => new PollingWorker(provider.GetRequiredService<IRulePollingCycle>(),
            provider.GetRequiredService<IOptionsMonitor<PollingOptions>>(), provider.GetRequiredService<ILogger<PollingWorker>>()));
        // These are singletons inside one immutable tenant container, never application-wide instances.
        services.AddSingleton<IntegrationProbeGate>();
        services.AddSingleton<IIntegrationTestService, IntegrationTestService>();
        services.AddSingleton<IDeviceLabelStore, AppSettingsDeviceLabelStore>();
        services.AddSingleton<DeviceNameService>();
        services.AddSingleton<SiteSettingsService>();
        services.AddHttpClient(IntegrationTestService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(16)).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(ProviderHandler).AddHttpMessageHandler(() => new TenantRequestGate(_requests, lifetime.ApplicationStopping));
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        try { return new TenantRuntime(factory.InstallationId, provider, configuration, lifetime, clock); }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            (configuration as IDisposable)?.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    internal async Task<IReadOnlyList<string>> EnabledInstallationIdsAsync(CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(databaseOptions);
        return await db.Installations.AsNoTracking().Where(installation => installation.IsEnabled)
            .Select(installation => installation.Id).OrderBy(id => id).ToListAsync(ct).ConfigureAwait(false);
    }

    private static HttpMessageHandler ProviderHandler() => SolarManagement.Http.PublicHttpTransport.CreateHandler();
    public void Dispose() => _requests.Dispose();
}

internal sealed class TenantRequestGate(SemaphoreSlim gate, CancellationToken stopping) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await base.SendAsync(request, linked.Token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}

internal sealed class TenantRuntimeLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping;
    public TenantRuntimeLifetime(CancellationToken hostStopping) => _stopping = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopping.Token;
    public void StopApplication() => _stopping.Cancel();
    public void Dispose() => _stopping.Dispose();
}
