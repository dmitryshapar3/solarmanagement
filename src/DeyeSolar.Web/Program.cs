using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Infrastructure.Tuya;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Workers;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=data/deye.db";

var dbOptionsBuilder = new DbContextOptionsBuilder<DeyeSolarDbContext>();
dbOptionsBuilder.UseSqlite(connectionString);
using (var db = new DeyeSolarDbContext(dbOptionsBuilder.Options))
{
    db.Database.EnsureCreated();
}

((IConfigurationBuilder)builder.Configuration).Add(new DbConfigurationSource(connectionString));

// Configuration
builder.Services.Configure<DeyeCloudOptions>(builder.Configuration.GetSection(DeyeCloudOptions.Section));
builder.Services.Configure<TuyaOptions>(builder.Configuration.GetSection(TuyaOptions.Section));
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.Section));

// Database
builder.Services.AddDbContextFactory<DeyeSolarDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddSingleton<AppSettingsService>();

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

// Seed settings (creates missing DB rows for all options properties)
using (var scope = app.Services.CreateScope())
{
    var settingsService = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
    await settingsService.SeedSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
    await settingsService.SeedSectionAsync<TuyaOptions>(TuyaOptions.Section);
    await settingsService.SeedSectionAsync<PollingOptions>(PollingOptions.Section);
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
