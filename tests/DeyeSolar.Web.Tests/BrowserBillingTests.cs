using System.Diagnostics;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace DeyeSolar.Web.Tests;

public class BrowserBillingTests
{
    private const string Password = "Browser billing password 42!";
    private const string UserId = "browser-billing-owner";
    private const string InstallationId = "browser-billing-site";
    private const string NeighbourId = "browser-independent-site";

    [SqlServerFact]
    public async Task RealLoginAndOpenBlazorCircuitLoseSocketAccessAtExpiryButKeepBillingAccountAndLogout()
    {
        await using var app = await ProductionApp.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US" });
        // The application uses only the isolated local SQL instance; the browser also avoids remote fonts.
        await context.RouteAsync("**/*", route => route.Request.Url.StartsWith(app.Address, StringComparison.Ordinal)
            ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var login = await page.GotoAsync(app.Address + "/login");
        Assert.Equal(200, login!.Status);
        await page.Locator("#username").FillAsync("billing-browser@example.test");
        await page.Locator("#password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(app.Address + "/");
        await page.GotoAsync(app.Address + "/devices");
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh devices", Exact = true });
        await Assertions.Expect(refresh).ToBeVisibleAsync();
        await refresh.ClickAsync();
        await Assertions.Expect(page.GetByText("No smart sockets found. Add and enable a socket integration in Settings, then refresh devices.",
            new() { Exact = true })).ToBeVisibleAsync();

        var allowed = await context.APIRequest.GetAsync(app.Address + "/api/devices?refresh=true");
        Assert.Equal(200, allowed.Status);
        var before = await app.ReadPrivateStateAsync();
        var invalidMutation = await context.APIRequest.PostAsync(app.Address + "/api/rules", new()
        {
            DataObject = new { name = "Unverified browser mutation", entityId = "unknown", enabled = false, sourceInverterId = (Guid?)null }
        });
        Assert.Equal(400, invalidMutation.Status);
        using (var problem = JsonDocument.Parse(await invalidMutation.TextAsync()))
            Assert.Equal("antiforgery", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, await app.ReadPrivateStateAsync());
        await app.ExpireTrialAsync();
        // This remains the same SignalR circuit and page: a navigation could hide stale circuit access.
        await Assertions.Expect(page.GetByText("Your trial has ended. Subscribe to read or control your sockets.",
            new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Assertions.Expect(refresh).ToHaveCountAsync(0);
        var denied = await context.APIRequest.GetAsync(app.Address + "/api/devices?refresh=true");
        {
            Assert.Equal(402, denied.Status);
            using var payload = JsonDocument.Parse(await denied.TextAsync());
            Assert.Equal("subscription_required", payload.RootElement.GetProperty("code").GetString());
        }
        var access = await context.APIRequest.GetAsync(app.Address + "/api/billing/access");
        {
            Assert.Equal(200, access.Status);
            using var payload = JsonDocument.Parse(await access.TextAsync());
            Assert.False(payload.RootElement.GetProperty("hasAccess").GetBoolean());
            Assert.Equal("expired", payload.RootElement.GetProperty("status").GetString());
        }
        var billing = await page.GotoAsync(app.Address + "/billing");
        Assert.Equal(200, billing!.Status);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Subscription", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Subscriptions are not configured yet. Contact support.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Account", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign-in methods for your account", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Verified email: billing-browser@example.test", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(before, await app.ReadPrivateStateAsync());
        await page.GotoAsync(app.Address + "/logout");
        await page.WaitForURLAsync(app.Address + "/login");
        var signedOut = await context.APIRequest.GetAsync(app.Address + "/api/billing/access");
        Assert.Equal(401, signedOut.Status);
    }

    internal sealed class ProductionApp(Process process, DbContextOptions<DeyeSolarDbContext> options, string directory, string address,
        Task standardOutput, Task standardError, StringBuilder logs, string applicationAssemblyIdentity, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public string Address { get; } = address;
        internal DbContextOptions<DeyeSolarDbContext> DatabaseOptions => options;
        internal string ApplicationAssemblyIdentity { get; } = applicationAssemblyIdentity;

        internal string DiagnosticLogTail()
        {
            string diagnostic;
            lock (logs) diagnostic = logs.ToString();
            using var db = new DeyeSolarDbContext(options);
            var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
            diagnostic = diagnostic.Replace(connection.ConnectionString, "[redacted connection]", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(connection.Password)) diagnostic = diagnostic.Replace(connection.Password, "[redacted]", StringComparison.Ordinal);
            return diagnostic[Math.Max(0, diagnostic.Length - 10_000)..];
        }

        public async Task ExpireTrialAsync()
        {
            await using var db = new DeyeSolarDbContext(options);
            var account = await db.BillingAccounts.SingleAsync(a => a.UserId == UserId);
            account.TrialStartedAt = DateTimeOffset.UtcNow.AddMonths(-1).AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        public async Task<string> ReadPrivateStateAsync()
        {
            await using var db = new DeyeSolarDbContext(options);
            return JsonSerializer.Serialize(new
            {
                Settings = await db.AppSettings.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.InstallationId == NeighbourId).OrderBy(s => s.Id).ToArrayAsync(),
                Rules = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
                Readings = await db.Readings.IgnoreQueryFilters().AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
                Devices = await db.IntegrationDeviceBindings.IgnoreQueryFilters().AsNoTracking().OrderBy(d => d.Id).ToArrayAsync(),
                Commands = await db.IntegrationCommands.IgnoreQueryFilters().AsNoTracking().OrderBy(c => c.Id).ToArrayAsync(),
                NeighbourAccount = await db.BillingAccounts.AsNoTracking().SingleAsync(a => a.UserId == "browser-neighbour-owner")
            });
        }

        public static async Task<ProductionApp> StartAsync(bool enableApple = false)
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarBrowserBilling");
            var options = database.Options;
            using var connectionContext = new DeyeSolarDbContext(options);
            var connection = new SqlConnectionStringBuilder(connectionContext.Database.GetConnectionString());
            var directory = Path.Combine(Path.GetTempPath(), "solar-browser-billing-" + Guid.NewGuid().ToString("N"));
            Process? process = null;
            try
            {
                Directory.CreateDirectory(directory);
                await SeedAsync(options, connection.ConnectionString);
                if (enableApple)
                {
                    // EF-created SQL databases may use row-versioned reads. This fixture needs a real blocked reader.
                    var master = new SqlConnectionStringBuilder(connection.ConnectionString) { InitialCatalog = "master" };
                    await using var server = new SqlConnection(master.ConnectionString);
                    await server.OpenAsync();
                    await using var command = server.CreateCommand();
                    command.CommandText = """
                        DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database)
                            + N' SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE';
                        EXEC sys.sp_executesql @sql;
                        """;
                    command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = connection.InitialCatalog;
                    await command.ExecuteNonQueryAsync();
                }
                // An isolated content root prevents developer bootstrap files or credentials from entering the child app.
                await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), JsonSerializer.Serialize(new
                {
                    Logging = new
                    {
                        LogLevel = new Dictionary<string, string>
                        {
                            ["Default"] = "Warning",
                            ["Microsoft.EntityFrameworkCore.Database.Command"] = enableApple ? "Information" : "Warning"
                        }
                    },
                    AllowedHosts = "*"
                }));
                var repository = FindRepository();
                var address = "http://127.0.0.1:" + AvailablePort();
                var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                if (string.IsNullOrWhiteSpace(host))
                    host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
                var info = new ProcessStartInfo(host)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var applicationAssembly = typeof(DeyeSolarDbContext).Assembly.Location;
                var applicationAssemblyIdentity = applicationAssembly + "; SHA256="
                    + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(applicationAssembly)));
                info.ArgumentList.Add(applicationAssembly);
                info.ArgumentList.Add("--urls"); info.ArgumentList.Add(address);
                info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(directory);
                foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("Auth__", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("Billing__", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("IntegrationRuntime__", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("Integrations__", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("SolarEstimate__", StringComparison.OrdinalIgnoreCase)).ToArray())
                    info.Environment.Remove(key);
                info.Environment.Remove("SOLAR_TEST_SQL_CONNECTION");
                info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                info.Environment["DOTNET_ENVIRONMENT"] = "Development";
                info.Environment["ASPNETCORE_WEBROOT"] = Path.Combine(repository, "src", "DeyeSolar.Web", "wwwroot");
                info.Environment["ConnectionStrings__DefaultConnection"] = connection.ConnectionString;
                info.Environment["Auth__PublicBaseUrl"] = "https://billing-browser.example.test";
                info.Environment["Auth__RegistrationEnabled"] = "false";
                info.Environment["Auth__DataProtectionKeysPath"] = Path.Combine(directory, "data-protection");
                info.Environment["Billing__Apple__Enabled"] = "false";
                if (enableApple)
                {
                    // These credentials configure a synthetic persisted lease; no receipt is verified or payment made.
                    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var request = new CertificateRequest("CN=Solar browser billing fixture root", key, HashAlgorithmName.SHA256);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                    var privateKey = Path.Combine(directory, "apple-fixture-key.p8");
                    var rootCertificate = Path.Combine(directory, "apple-fixture-root.cer");
                    await File.WriteAllTextAsync(privateKey, key.ExportPkcs8PrivateKeyPem());
                    await File.WriteAllBytesAsync(rootCertificate, certificate.Export(X509ContentType.Cert));
                    info.Environment["Billing__Apple__Enabled"] = "true";
                    info.Environment["Billing__Apple__Environment"] = "Sandbox";
                    info.Environment["Billing__Apple__IssuerId"] = Guid.NewGuid().ToString();
                    info.Environment["Billing__Apple__KeyId"] = "TESTKEY123";
                    info.Environment["Billing__Apple__PrivateKeyPath"] = privateKey;
                    info.Environment["Billing__Apple__RootCertificatePaths__0"] = rootCertificate;
                    // Block real Apple traffic even if the first worker scan races lease insertion.
                    foreach (var name in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy" })
                        info.Environment.Remove(name);
                    var proxyPort = AvailablePort();
                    while (proxyPort == new Uri(address).Port) proxyPort = AvailablePort();
                    var proxy = "http://127.0.0.1:" + proxyPort;
                    foreach (var name in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
                        info.Environment[name] = proxy;
                    info.Environment["NO_PROXY"] = info.Environment["no_proxy"] = "localhost,127.0.0.1,::1";
                }
                info.Environment["IntegrationRuntime__PackageDirectory"] = Path.Combine(directory, "packages");
                info.Environment["Integrations__KeyRingPath"] = Path.Combine(directory, "integration-keyring");
                info.Environment["SolarEstimate__Roof1Kwp"] = "0";
                info.Environment["SolarEstimate__Roof2Kwp"] = "0";
                info.Environment["Polling__IntervalSeconds"] = "600";
                process = Process.Start(info) ?? throw new InvalidOperationException("The production application did not start.");
                var logs = new StringBuilder();
                if (enableApple)
                    logs.AppendLine("Child application assembly: " + applicationAssemblyIdentity);
                var output = DrainAsync(process.StandardOutput, logs);
                var error = DrainAsync(process.StandardError, logs);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
                {
                    try
                    {
                        using var ready = await client.GetAsync(address + "/login");
                        if (ready.StatusCode == HttpStatusCode.OK)
                            return new(process, options, directory, address, output, error, logs, applicationAssemblyIdentity, database);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
                    await Task.Delay(100);
                }
                string diagnostic;
                lock (logs) diagnostic = logs.ToString().Replace(connection.Password, "[redacted]", StringComparison.Ordinal);
                throw new InvalidOperationException("The production application did not become ready. "
                    + diagnostic[Math.Max(0, diagnostic.Length - 3000)..]);
            }
            catch
            {
                try { if (process is not null) { await StopAsync(process); process.Dispose(); } }
                finally { await DeleteResourcesAsync(database, directory); }
                throw;
            }
        }

        private static async Task SeedAsync(DbContextOptions<DeyeSolarDbContext> options, string connectionString)
        {
            await using (var db = new DeyeSolarDbContext(options))
            {
                db.Installations.AddRange(new Installation { Id = InstallationId, Name = "Browser billing fixture", CreatedAt = DateTimeOffset.UtcNow },
                    new Installation { Id = NeighbourId, Name = "Independent browser neighbour", CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<DeyeSolarDbContext>(builder => builder.UseSqlServer(connectionString));
            services.AddIdentityCore<IdentityUser>().AddEntityFrameworkStores<DeyeSolarDbContext>();
            await using (var provider = services.BuildServiceProvider())
            {
                using var scope = provider.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                foreach (var user in new[]
                {
                    new IdentityUser { Id = UserId, UserName = "billing-browser@example.test", Email = "billing-browser@example.test", EmailConfirmed = true },
                    new IdentityUser { Id = "browser-neighbour-owner", UserName = "independent-browser@example.test", Email = "independent-browser@example.test", EmailConfirmed = true }
                })
                    Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            }
            await using (var db = new DeyeSolarDbContext(options))
            {
                db.InstallationMemberships.AddRange(new InstallationMembership { InstallationId = InstallationId, UserId = UserId, Role = "Owner" },
                    new InstallationMembership { InstallationId = NeighbourId, UserId = "browser-neighbour-owner", Role = "Owner" });
                await db.SaveChangesAsync();
            }
            foreach (var id in new[] { InstallationId, NeighbourId })
            {
                await using var db = new DeyeSolarDbContext(options, id);
                db.AppSettings.AddRange(new AppSetting { Section = "SolarEstimate", Key = "Roof1Kwp", Value = "0" },
                    new AppSetting { Section = "SolarEstimate", Key = "Roof2Kwp", Value = "0" },
                    new AppSetting { Section = "Polling", Key = "IntervalSeconds", Value = "600" });
                if (id == NeighbourId)
                {
                    db.AppSettings.Add(new() { Section = "Fixture", Key = "Preserve", Value = "Independent setting" });
                    db.TriggerRules.Add(new TriggerRule { Name = "Independent disabled rule", EntityId = "private-neighbour-socket", Enabled = false });
                    db.Readings.Add(new Reading { Timestamp = DateTime.UtcNow, BatterySoc = 73, SolarProduction = 2100 });
                }
                await db.SaveChangesAsync();
            }
        }

        private static async Task DrainAsync(StreamReader reader, StringBuilder logs)
        {
            while (await reader.ReadLineAsync() is { } line)
                lock (logs) { logs.AppendLine(line); if (logs.Length > 20_000) logs.Remove(0, logs.Length - 20_000); }
        }

        private static string FindRepository()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "DeyeSolar.sln"))) return current.FullName;
            throw new InvalidOperationException("The DeyeSolar solution directory could not be located.");
        }

        private static int AvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static async Task StopAsync(Process process)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task DeleteResourcesAsync(SqlServerTestDatabase database, string directory)
        {
            await database.DisposeAsync();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("solar-browser-billing-", StringComparison.Ordinal))
                throw new InvalidOperationException("Browser fixture cleanup escaped its temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(process);
                await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                process.Dispose();
                await DeleteResourcesAsync(database, directory);
            }
        }
    }
}
