using System.Security.Cryptography;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Infrastructure.Tuya;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");

((IConfigurationBuilder)builder.Configuration).Add(new DbConfigurationSource(connectionString));

builder.Services.AddDbContextFactory<DeyeSolarDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDbContext<DeyeSolarDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddSingleton<AppSettingsService>();

// Identity
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
})
.AddEntityFrameworkStores<DeyeSolarDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

// Configuration
builder.Services.Configure<DeyeCloudOptions>(builder.Configuration.GetSection(DeyeCloudOptions.Section));
builder.Services.Configure<TuyaOptions>(builder.Configuration.GetSection(TuyaOptions.Section));
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.Section));

// Infrastructure
builder.Services.AddHttpClient<DeyeCloudClient>();
builder.Services.AddSingleton<IInverterDataSource>(sp => sp.GetRequiredService<DeyeCloudClient>());
builder.Services.AddHttpClient<TuyaCloudClient>();
builder.Services.AddSingleton<ISocketController>(sp => sp.GetRequiredService<TuyaCloudClient>());

// Snapshot & Rule engine
builder.Services.AddSingleton<InverterDataSnapshot>();
builder.Services.AddSingleton<RuleEvaluator>();
builder.Services.AddSingleton<IRuleRepository, RuleRepository>();

// Background worker
builder.Services.AddHostedService<PollingWorker>();

// Blazor + MudBlazor
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

var app = builder.Build();

// Migrate database and seed
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DeyeSolarDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    // Seed settings
    var settingsService = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
    await settingsService.SeedSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
    await settingsService.SeedSectionAsync<TuyaOptions>(TuyaOptions.Section);
    await settingsService.SeedSectionAsync<PollingOptions>(PollingOptions.Section);

    // Seed default rule
    if (!db.TriggerRules.Any())
    {
        db.TriggerRules.Add(new DeyeSolar.Domain.Models.TriggerRule
        {
            Name = "Solar Battery Management",
            EntityId = "",
            Enabled = false,
            SocTurnOnThreshold = 80,
            MaxConsumptionWh = 500,
            MonitoringWindowMinutes = 15,
            CooldownMinutes = 15,
            IntervalSeconds = 30
        });
        await db.SaveChangesAsync();
    }

    // Seed admin user
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var adminUser = await userManager.FindByNameAsync("admin");
    if (adminUser == null)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))[..16];
        adminUser = new IdentityUser { UserName = "admin", Email = "admin@deye.local" };
        var result = await userManager.CreateAsync(adminUser, password);
        if (result.Succeeded)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("=== Admin user created. Password: {Password} ===", password);
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapBlazorHub();
app.MapRazorPages();
app.MapFallbackToPage("/_Host");

app.Run();
