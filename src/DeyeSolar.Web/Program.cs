using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Infrastructure.HomeAssistant;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=data/deye.db";

// Ensure DB exists before adding it as a config source
var dbOptionsBuilder = new DbContextOptionsBuilder<DeyeSolarDbContext>();
dbOptionsBuilder.UseSqlite(connectionString);
using (var db = new DeyeSolarDbContext(dbOptionsBuilder.Options))
{
    db.Database.EnsureCreated();
}

// Add DB-backed configuration (overrides appsettings.json)
((IConfigurationBuilder)builder.Configuration).Add(new DbConfigurationSource(connectionString));

// Configuration bindings (reads from appsettings.json + DB overlay)
builder.Services.Configure<DeyeCloudOptions>(builder.Configuration.GetSection(DeyeCloudOptions.Section));
builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.Section));
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.Section));

// Database context factory
builder.Services.AddDbContextFactory<DeyeSolarDbContext>(options =>
    options.UseSqlite(connectionString));

// App settings service
builder.Services.AddSingleton<AppSettingsService>();

// Infrastructure
builder.Services.AddHttpClient<DeyeCloudClient>();
builder.Services.AddSingleton<IInverterDataSource>(sp => sp.GetRequiredService<DeyeCloudClient>());
builder.Services.AddHttpClient<HomeAssistantSocketController>();
builder.Services.AddSingleton<ISocketController>(sp => sp.GetRequiredService<HomeAssistantSocketController>());
builder.Services.AddSingleton<HomeAssistantSocketController>();

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

// Seed DB settings from appsettings.json (only if DB has no settings yet)
using (var scope = app.Services.CreateScope())
{
    var settingsService = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await settingsService.SeedFromConfigurationAsync(config, DeyeCloudOptions.Section);
    await settingsService.SeedFromConfigurationAsync(config, HomeAssistantOptions.Section);
    await settingsService.SeedFromConfigurationAsync(config, PollingOptions.Section);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
