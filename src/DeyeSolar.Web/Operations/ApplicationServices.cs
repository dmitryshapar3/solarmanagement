using DeyeSolar.Web.Integrations;
using SolarManagement.Integrations.Runtime;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Tenancy;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Billing;

namespace DeyeSolar.Web.Operations;

/// <summary>Composition only. Deployment decisions and initialization live in separate services.</summary>
public static class ApplicationServices
{
    public static void AddSolarApplication(this WebApplicationBuilder builder, DeploymentConfiguration deployment)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<UiText>();
        builder.Services.AddScoped<UserLanguageService>();
        // Database
        builder.Services.AddDbContextFactory<DeyeSolarDbContext>(options =>
            options.UseSqlServer(deployment.ConnectionString));
        builder.Services.AddDbContext<DeyeSolarDbContext>(options =>
            options.UseSqlServer(deployment.ConnectionString));

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
        builder.Services.AddAccountIdentities(deployment.AuthProviders);
        builder.Services.AddAppleBilling(deployment.AppleBilling);
        builder.Services.AddBillingAccess();

        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(
                MobileBearerAuthenticationHandler.SchemeName,
                _ => { });

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/signin";
            options.LogoutPath = "/logout";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            if (!builder.Environment.IsDevelopment()) options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        // Each validated installation owns credentials, snapshots, caches and background work.
        builder.Services.Configure<IntegrationRuntimeOptions>(deployment.IntegrationConfiguration.GetSection(IntegrationRuntimeOptions.Section));
        builder.Services.PostConfigure<IntegrationRuntimeOptions>(options =>
        {
            if (!Path.IsPathFullyQualified(options.PackageDirectory))
                options.PackageDirectory = Path.Combine(builder.Environment.ContentRootPath, options.PackageDirectory);
        });
        builder.Services.AddIntegrationRuntime();
        builder.Services.AddDynamicIntegrations(deployment.IntegrationConfiguration, builder.Environment.ContentRootPath);
        builder.Services.AddIntegrationManagement();
        builder.Services.AddTenantRequestServices(deployment.OpenMeteoApiKey);
        builder.Services.AddBillingSocketAccess();
        builder.Services.AddSingleton(provider => MobileSessionStore.Persistent(
            provider.GetRequiredService<DbContextOptions<DeyeSolarDbContext>>(), provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddAccountSecurity();
        builder.Services.AddExpiredSessionCleanup();
        builder.Services.AddScoped<MobileAuthService>();
        builder.Services.AddScoped<Redesign.ManualOverrideService>();
        builder.Services.AddScoped<Redesign.RedesignQueries>();
        builder.Services.AddScoped<Redesign.InstallationSettingsService>();

        // Blazor application and native design components.
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AllowAnonymousToPage("/Privacy");
            options.Conventions.AllowAnonymousToPage("/Support");
            if (builder.Environment.IsDevelopment()) options.Conventions.AllowAnonymousToPage("/DesignGalleryHost");
            else options.Conventions.AddPageRouteModelConvention("/DesignGalleryHost", model => model.Selectors.Clear());
        });
        builder.Services.AddServerSideBlazor();

        builder.Services.AddSingleton(deployment);
        builder.Services.AddSingleton<IApplicationDatabaseInitializer, ApplicationDatabaseInitializer>();
        builder.Services.AddScoped<IApplicationSeedData, ApplicationSeedData>();
        builder.Services.AddApplicationHealth(deployment);
    }
}
