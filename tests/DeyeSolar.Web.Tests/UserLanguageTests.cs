using System.Globalization;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public sealed class UserLanguageTests
{
    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("PT_br", "pt")]
    [InlineData("zh-Hant", "zh")]
    [InlineData("xx", null)]
    public void RegionalLanguageNormalizationHasSafeFallback(string input, string? expected)
        => Assert.Equal(expected, UiText.Normalize(input));

    [Theory]
    [InlineData("fr;q=0.2,pl-PL;q=0.9,en;q=0.5", "pl")]
    [InlineData("ru;q=0,ja;q=0.8", "ja")]
    [InlineData("xx,ko-KR;q=0.7", "ko")]
    [InlineData("ru;q=invalid", "en")]
    public void BrowserLanguageQualityAndExclusionsAreRespected(string header, string expected)
        => Assert.Equal(expected, LanguageMiddleware.ResolveHeader(header));

    [Fact]
    public async Task UsersSharingAnInstallationCanChooseDifferentLanguagesAndReloadThem()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(connection).Options;
        await using (var db = new DeyeSolarDbContext(options)) await db.Database.EnsureCreatedAsync();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddScoped(_ => new DeyeSolarDbContext(options));
        collection.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
        collection.AddScoped<UserLanguageService>();
        await using var provider = collection.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.CreateAsync(new() { Id = "first", UserName = "first" })).Succeeded);
            Assert.True((await users.CreateAsync(new() { Id = "second", UserName = "second" })).Succeeded);
            var preferences = scope.ServiceProvider.GetRequiredService<UserLanguageService>();
            Assert.Null(await preferences.GetAsync("first"));
            Assert.True(await preferences.SetAsync("first", "ru"));
            Assert.True(await preferences.SetAsync("second", "pl"));
            Assert.True(await preferences.SetAsync("first", "de"));
            Assert.False(await preferences.SetAsync("first", null!));
            Assert.False(await preferences.SetAsync("first", ""));
            Assert.False(await preferences.SetAsync("first", " "));
            Assert.False(await preferences.SetAsync("first", "xx"));
            Assert.False(await preferences.SetAsync("missing", "de"));
        }
        using var reload = provider.CreateScope();
        var saved = reload.ServiceProvider.GetRequiredService<UserLanguageService>();
        Assert.Equal("de", await saved.GetAsync("first"));
        Assert.Equal("pl", await saved.GetAsync("second"));
        var claims = await reload.ServiceProvider.GetRequiredService<DeyeSolarDbContext>().UserClaims.ToListAsync();
        Assert.Equal(2, claims.Count(claim => claim.ClaimType == UserLanguageService.ClaimType));
    }

    [Fact]
    public void SharedCatalogTranslatesTemplatesWithoutChangingUserValues()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var collection = new ServiceCollection();
            collection.AddLogging(); collection.AddAntiforgery(); collection.AddHttpContextAccessor(); collection.AddScoped<UiText>();
            using var services = collection.BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            Assert.Equal("Настройки", text["Settings"]);
            var translated = text.Translate("Request failed with HTTP 503.");
            Assert.Contains("503", translated);
            Assert.DoesNotContain("Request failed", translated);
            Assert.Equal("Owner's custom device", text.Translate("Owner's custom device"));
            Assert.Equal("", text.Translate(null));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            Assert.NotEqual("Settings", text["Settings"]);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; CultureInfo.CurrentUICulture = oldUiCulture; }
    }
}
