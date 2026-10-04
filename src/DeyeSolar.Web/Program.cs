using DeyeSolar.Web.Integrations;
using SolarManagement.Integrations.Runtime;
using System.Security.Cryptography;
using System.Net;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.Solar;
using DeyeSolar.Infrastructure.Settlement;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Tenancy;
using MudBlazor.Services;
using DeyeSolar.Web.Localization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UiText>();
builder.Services.AddScoped<UserLanguageService>();
builder.Configuration.AddJsonFile("integration-bootstrap.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
// Publisher and network trust is operator configuration, captured before editable SQL settings.
var integrationRuntimeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(builder.Configuration.AsEnumerable()
    .Where(value => value.Key.StartsWith("IntegrationRuntime:", StringComparison.OrdinalIgnoreCase)
        || value.Key.StartsWith("Integrations:", StringComparison.OrdinalIgnoreCase))).Build();
// Authentication provider secrets must never be sourced from user-editable SQL settings.
var authProviders = AuthProviderOptions.Capture(builder.Configuration);
var bootstrapAdminPassword = builder.Configuration["Auth:BootstrapAdminPassword"];
var dataProtectionKeysPath = builder.Configuration["Auth:DataProtectionKeysPath"];
var trustedProxyAddresses = builder.Configuration["Auth:TrustedProxyAddresses"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    if (!Path.IsPathFullyQualified(dataProtectionKeysPath))
        throw new InvalidOperationException("Auth:DataProtectionKeysPath must be an absolute directory path.");
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}
if (!string.IsNullOrWhiteSpace(trustedProxyAddresses))
{
    var proxies = trustedProxyAddresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed
            : throw new InvalidOperationException("Auth:TrustedProxyAddresses must contain proxy IP addresses.")).ToArray();
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
    });
}
// Capture this deployment secret before the editable SQL settings provider is added.
// It must only come from server configuration/environment, never AppSettings or a client DTO.
var openMeteoApiKey = builder.Configuration["SolarEstimate:ApiKey"];

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");

((IConfigurationBuilder)builder.Configuration).Add(new DbConfigurationSource(connectionString));

builder.Services.AddDbContextFactory<DeyeSolarDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDbContext<DeyeSolarDbContext>(options =>
    options.UseSqlServer(connectionString));

// Identity
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<DeyeSolarDbContext>()
.AddDefaultTokenProviders();
builder.Services.AddAccountIdentities(authProviders);

builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(
        MobileBearerAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    if (!builder.Environment.IsDevelopment()) options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Each validated installation owns credentials, snapshots, caches and background work.
builder.Services.Configure<IntegrationRuntimeOptions>(integrationRuntimeConfiguration.GetSection(IntegrationRuntimeOptions.Section));
builder.Services.PostConfigure<IntegrationRuntimeOptions>(options =>
{
    if (!Path.IsPathFullyQualified(options.PackageDirectory))
        options.PackageDirectory = Path.Combine(builder.Environment.ContentRootPath, options.PackageDirectory);
});
builder.Services.AddIntegrationRuntime();
builder.Services.AddDynamicIntegrations(integrationRuntimeConfiguration, builder.Environment.ContentRootPath);
builder.Services.AddIntegrationManagement();
builder.Services.AddTenantRequestServices(builder.Configuration, openMeteoApiKey);
builder.Services.AddSingleton<MobileSessionStore>();
builder.Services.AddScoped<MobileAuthService>();

// Blazor + MudBlazor
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Privacy");
    options.Conventions.AllowAnonymousToPage("/Support");
});
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();
builder.Services.AddTransient<MudBlazor.MudLocalizer, DeyeSolar.Web.Localization.UiMudLocalizer>();

var app = builder.Build();

// Migrate database and seed
using (var scope = app.Services.CreateScope())
{
    var dbFactory = new TenantDbContextFactory(scope.ServiceProvider.GetRequiredService<DbContextOptions<DeyeSolarDbContext>>(), InstallationIds.Legacy);
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    if (!await db.Installations.AnyAsync(i => i.Id == InstallationIds.Legacy))
    {
        db.Installations.Add(new Installation { Id = InstallationIds.Legacy, Name = "Existing installation", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    // Seed settings
    var settingsService = new AppSettingsService(dbFactory, builder.Configuration);
    await scope.ServiceProvider.GetRequiredService<IntegrationPackageBootstrap>().EnsureInstalledAsync(CancellationToken.None);
    var integrationMigration = scope.ServiceProvider.GetRequiredService<LegacyIntegrationBootstrap>();
    if (!await integrationMigration.RunAsync(dbFactory, builder.Configuration, CancellationToken.None))
        app.Logger.LogWarning("Legacy connections await trusted provider packages. Existing credentials have been preserved for import.");
    await settingsService.SeedSectionAsync<PollingOptions>(PollingOptions.Section);
    await settingsService.SeedSectionAsync<DisplayOptions>(DisplayOptions.Section);

    // Seed default rule
    if (!db.TriggerRules.Any())
    {
        db.TriggerRules.Add(new DeyeSolar.Domain.Models.TriggerRule
        {
            Name = "Solar Surplus Diverter",
            EntityId = "",
            Enabled = false,
            SocTurnOnThreshold = 80,
            UseSeparateSocTurnOffThreshold = false,
            SocTurnOffThreshold = 80,
            UseSolarProductionThreshold = false,
            MinAverageSolarProductionWatts = 3000,
            CooldownMinutes = 15,
            IntervalSeconds = 30
        });
        await db.SaveChangesAsync();
    }

    // Seed admin user
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var adminUser = await userManager.FindByNameAsync("admin");
    if (adminUser == null && !string.IsNullOrWhiteSpace(bootstrapAdminPassword))
    {
        adminUser = new IdentityUser { UserName = "admin", Email = "admin@deye.local" };
        var result = await userManager.CreateAsync(adminUser, bootstrapAdminPassword);
        if (result.Succeeded)
        {
            db.InstallationMemberships.Add(new InstallationMembership { InstallationId = InstallationIds.Legacy, UserId = adminUser.Id, Role = "Owner" });
            await db.SaveChangesAsync();
            app.Logger.LogInformation("Bootstrap administrator created. Credentials are not logged.");
        }
        else throw new InvalidOperationException("The bootstrap administrator could not be created. Check Auth:BootstrapAdminPassword requirements.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
if (!string.IsNullOrWhiteSpace(trustedProxyAddresses)) app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter();
app.UseAccountIdentityOrigin();
app.UseAuthentication();
app.UseMiddleware<LanguageMiddleware>();
app.UseAuthorization();
app.UseMiddleware<InstallationBindingMiddleware>();
app.MapMobileApi();
app.MapDynamicIntegrations();
app.MapAccountIdentityApi();
app.MapUserLanguage();
app.MapGoogleIdentity();
app.MapExportSalesApi();
app.MapIntegrationManagement();
app.MapBlazorHub();
app.MapRazorPages();
app.MapFallbackToPage("/_Host");

app.Run();

public partial class Program;
