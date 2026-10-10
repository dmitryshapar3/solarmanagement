using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed class PanelLayoutSettingsTests
{
    [Fact]
    public void CountsAreUnknownByDefaultAndDoNotChangeTheEnergyModel()
    {
        var options = new SolarEstimateOptions();
        Assert.Null(options.Roof1PanelCount); Assert.Null(options.Roof2PanelCount);
        Assert.Null(options.Roof1PanelsPerRow); Assert.Null(options.Roof2PanelsPerRow);
        var at = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var observation = new SolarRadiationObservation(at, 800, 400, 20, 2, at, 0);
        var before = DeyeSolar.Domain.Services.SolarPowerCalculator.Calculate(observation, options, at);
        options.Roof1PanelCount = 0; options.Roof2PanelCount = 1000; options.Roof1PanelsPerRow = 0; options.Roof2PanelsPerRow = 4;
        var after = DeyeSolar.Domain.Services.SolarPowerCalculator.Calculate(observation, options, at);
        Assert.Equal(before.CentralKw, after.CentralKw);
        Assert.Equal(before.Roofs.Select(r => r.CentralKw), after.Roofs.Select(r => r.CentralKw));
    }

    [Theory]
    [InlineData(-1, null, null, null)]
    [InlineData(1001, null, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, 1001, null, null)]
    [InlineData(null, null, -1, null)]
    [InlineData(null, null, 1001, null)]
    [InlineData(null, null, null, -1)]
    [InlineData(null, null, null, 1001)]
    [InlineData(4, null, 5, null)]
    [InlineData(null, 4, null, 5)]
    public void InvalidCountsAndRowSizesAreRejected(int? count1, int? count2, int? row1, int? row2)
    {
        var site = Site() with { SolarEstimate = Site().SolarEstimate with
        { Roof1PanelCount = count1, Roof2PanelCount = count2, Roof1PanelsPerRow = row1, Roof2PanelsPerRow = row2 } };
        Assert.False(SiteSettingsService.TryValidate(site, out var error));
        Assert.Contains(row1 > count1 || row2 > count2 ? "positive row size" : "whole panel count", error);
    }

    [Theory]
    [InlineData(null, 8)]
    [InlineData(0, 8)]
    [InlineData(7, 7)]
    [InlineData(1000, 0)]
    public void UnknownOrEmptyRoofsCanKeepTheirLayoutAndZeroRowsMeansAutomatic(int? count, int? row)
        => Assert.True(SiteSettingsService.TryValidate(Site() with { SolarEstimate = Site().SolarEstimate with
        { Roof1PanelCount = count, Roof2PanelCount = count, Roof1PanelsPerRow = row, Roof2PanelsPerRow = row } }, out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothSettingsApisRoundTripExactCountsAndPreserveOlderClientOmissions(bool installationApi)
    {
        await using var fixture = await Fixture.Create();
        Assert.Null((await fixture.Legacy.LoadAsync()).SolarEstimate.Roof1PanelCount);
        var exact = Site() with { SolarEstimate = Site().SolarEstimate with
        { Roof1PanelCount = 9, Roof2PanelCount = 7, Roof1PanelsPerRow = 3, Roof2PanelsPerRow = 2 } };
        await fixture.Save(installationApi, exact);
        var loaded = await fixture.Legacy.LoadAsync();
        Assert.Equal(exact.SolarEstimate, loaded.SolarEstimate);
        Assert.Equal(exact.SolarEstimate, (await fixture.Installation.LoadAsync()).Site.SolarEstimate);

        // Deserialize the actual older payload shape with no new fields, rather than a new DTO full of defaults.
        var json = JsonSerializer.SerializeToNode(loaded, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var solar = json["solarEstimate"]!.AsObject();
        foreach (var key in new[] { "roof1PanelCount", "roof2PanelCount", "roof1PanelsPerRow", "roof2PanelsPerRow" }) solar.Remove(key);
        solar["locationLabel"] = "Legacy client renamed this site";
        var older = json.Deserialize<SiteSettingsDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(older.SolarEstimate.Roof1PanelCount);
        await fixture.Save(installationApi, older);
        loaded = await fixture.Legacy.LoadAsync();
        Assert.Equal(9, loaded.SolarEstimate.Roof1PanelCount); Assert.Equal(7, loaded.SolarEstimate.Roof2PanelCount);
        Assert.Equal(3, loaded.SolarEstimate.Roof1PanelsPerRow); Assert.Equal(2, loaded.SolarEstimate.Roof2PanelsPerRow);
        Assert.Equal("Legacy client renamed this site", loaded.SolarEstimate.LocationLabel);

        await fixture.Save(installationApi, older with { SolarEstimate = older.SolarEstimate with { Roof1PanelCount = 0, Roof1PanelsPerRow = 0 } });
        loaded = await fixture.Legacy.LoadAsync();
        Assert.Equal(0, loaded.SolarEstimate.Roof1PanelCount); Assert.Equal(0, loaded.SolarEstimate.Roof1PanelsPerRow);
        Assert.Equal(7, loaded.SolarEstimate.Roof2PanelCount); Assert.Equal(2, loaded.SolarEstimate.Roof2PanelsPerRow);
        var rows = await fixture.Rows();
        Assert.Equal("server-secret-kept", rows["SolarEstimate:ApiKey"]);
        Assert.Equal("-0.004", rows["SolarEstimate:TemperatureCoefficient"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingCountCannotLeaveAnOmittedStoredRowSizeInvalidAndRejectionIsAtomic(bool installationApi)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Save(installationApi, Site() with { SolarEstimate = Site().SolarEstimate with { Roof1PanelCount = 9, Roof1PanelsPerRow = 3 } });
        var before = await fixture.Rows();
        var invalid = Site() with { SolarEstimate = Site().SolarEstimate with { Roof1PanelCount = 2, LocationLabel = "Must not persist" } };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Save(installationApi, invalid));
        Assert.Contains("positive row size", error.Message);
        Assert.Equal(before.OrderBy(p => p.Key), (await fixture.Rows()).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPartialWriteRevalidatesTheOmittedPartnerInsideItsActualWriterTransaction(bool changeRow)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Save(false, Site() with { SolarEstimate = Site().SolarEstimate with { Roof1PanelCount = 9, Roof1PanelsPerRow = 1 } });
        Dictionary<string, string>? concurrentRows = null;
        var interleaved = fixture.LegacyWithInterleaving(async () =>
        {
            // Commit after the request's initial read/validation but before its writer transaction begins.
            var latest = await fixture.Legacy.LoadAsync();
            var concurrent = changeRow ? latest.SolarEstimate with { Roof1PanelCount = 5 }
                : latest.SolarEstimate with { Roof1PanelsPerRow = 3 };
            await fixture.Legacy.SaveAsync(latest with { SolarEstimate = concurrent });
            concurrentRows = await fixture.Rows();
        });
        var requested = Site().SolarEstimate with { LocationLabel = "Rejected draft must not persist" };
        requested = changeRow ? requested with { Roof1PanelsPerRow = 8 } : requested with { Roof1PanelCount = 2 };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => interleaved.SaveAsync(Site() with { SolarEstimate = requested }));
        Assert.Contains("positive row size", error.Message);
        Assert.NotNull(concurrentRows);
        Assert.Equal(concurrentRows.OrderBy(p => p.Key), (await fixture.Rows()).OrderBy(p => p.Key));
        var saved = (await fixture.Legacy.LoadAsync()).SolarEstimate;
        Assert.Equal(changeRow ? 5 : 9, saved.Roof1PanelCount);
        Assert.Equal(changeRow ? 1 : 3, saved.Roof1PanelsPerRow);
    }

    private static SiteSettingsDto Site() => new(new(50, 20, "Fictional panel site", "UTC", 5, 3, 25, 30, 230, 50), new("2026-10-01", "UTC", false));

    private sealed class Fixture(SqliteConnection connection, Factory factory, AppSettingsService settings, SiteSettingsService legacy,
        InstallationSettingsService installation) : IAsyncDisposable
    {
        public SiteSettingsService Legacy => legacy;
        public InstallationSettingsService Installation => installation;
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(); await TestInstallation.EnsureAsync(db);
                db.AppSettings.AddRange(new AppSetting { Section = "SolarEstimate", Key = "ApiKey", Value = "server-secret-kept" },
                    new AppSetting { Section = "SolarEstimate", Key = "TemperatureCoefficient", Value = "-0.004" });
                await db.SaveChangesAsync();
            }
            var settings = new AppSettingsService(factory, new ConfigurationBuilder().Build());
            var legacy = new SiteSettingsService(settings, settings, new FixedOptionsMonitor<InverterConnectionOptions>(new()), new NoInverter());
            var current = new CurrentInstallation(); current.BindOnce(TestInstallation.Id);
            var security = new InteractiveSecurityContext(new Owner(), current, new HttpContextAccessor());
            security.BindOnce(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "panel-owner")], "PanelTest")));
            return new(connection, factory, settings, legacy, new(factory, settings, security,
                new IntegrationChangeNotifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrationChangeNotifier>.Instance)));
        }
        public SiteSettingsService LegacyWithInterleaving(Func<Task> beforeWrite) => new(settings,
            new InterleavingWriter(settings, beforeWrite), new FixedOptionsMonitor<InverterConnectionOptions>(new()), new NoInverter());
        public async Task Save(bool installationApi, SiteSettingsDto site)
        {
            if (!installationApi) { await legacy.SaveAsync(site); return; }
            var current = await installation.LoadAsync();
            await installation.SaveAsync(new(site, current.Polling, current.Display, current.PrimaryInverterId, current.Version, new(current.IntegrationVersions)));
        }
        public async Task<Dictionary<string, string>> Rows()
        { await using var db = factory.CreateDbContext(); return await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Section + ":" + x.Key, x => x.Value); }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
    private sealed class InterleavingWriter(IAppSettingsWriter actual, Func<Task> beforeWrite) : IAppSettingsWriter
    {
        public async Task SaveSectionAsync<T>(string section, T options) where T : class
        { await beforeWrite(); await actual.SaveSectionAsync(section, options); }
        public async Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections, CancellationToken ct = default)
        { await beforeWrite(); await actual.SaveSectionsAsync(sections, ct); }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => new(options, TestInstallation.Id); }
    private sealed class Owner : IInstallationAccessAuthorizer
    {
        public Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default)
            => Task.FromResult(new InstallationMembership { InstallationId = installationId, UserId = "panel-owner", Role = "Owner" });
    }
    private sealed class NoInverter : IInverterDataSource
    { public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct) => throw new InvalidOperationException("Panel settings do not read a provider."); }
}
