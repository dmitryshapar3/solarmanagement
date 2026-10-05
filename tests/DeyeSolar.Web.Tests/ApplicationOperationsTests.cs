using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Tests;

public class ApplicationOperationsTests
{
    [Fact]
    public void ProductionCannotTurnHttpStartupIntoPrivilegedMigrationOrDisableRuntimePrivilegeValidation()
    {
        static WebApplicationBuilder Builder(string mode)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Operations:DatabaseMode"] = mode, ["Operations:RequireLeastPrivilege"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Server=fixture.invalid;Database=fixture",
                ["Auth:RegistrationEnabled"] = "false", ["Auth:PublicBaseUrl"] = "https://fixture.example",
                ["Auth:DataProtectionKeysPath"] = Path.Combine(Path.GetTempPath(), "solar-key-fixture")
            });
            return builder;
        }
        Assert.Throws<InvalidOperationException>(() => DeploymentConfiguration.Capture(Builder("migrate"), false));
        var runtimeBuilder = Builder("validate");
        var runtime = DeploymentConfiguration.Capture(runtimeBuilder, false);
        Assert.True(runtime.RequireLeastPrivilege);
        runtimeBuilder.Configuration["Operations:RuntimeDatabaseUser"] = "untrusted-late-change";
        Assert.Null(runtime.Configuration["Operations:RuntimeDatabaseUser"]);
        Assert.Equal(DatabaseStartupMode.Migrate, DeploymentConfiguration.Capture(Builder("validate"), true).DatabaseMode);
        var unsafeForwarding = Builder("validate");
        unsafeForwarding.Configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
        Assert.Throws<InvalidOperationException>(() => DeploymentConfiguration.Capture(unsafeForwarding, false));
    }

    [Fact]
    public async Task ProductionUnknownApiPathsReturnJsonForAllMethodsWithoutResolvingPrivateData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "solar-unknown-api-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production", ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:DatabaseMode"] = "validate", ["ConnectionStrings:DefaultConnection"] = "Server=fixture.invalid;Database=UnknownApi;Encrypt=False",
            ["Auth:PublicBaseUrl"] = "https://fixture.example", ["Auth:DataProtectionKeysPath"] = Path.Combine(directory, "auth"),
            ["Integrations:KeyRingPath"] = Path.Combine(directory, "integrations"),
            ["IntegrationRuntime:PackageDirectory"] = Path.Combine(directory, "packages")
        });
        var deployment = DeploymentConfiguration.Capture(builder, false);
        builder.AddSolarApplication(deployment);
        // This exercises the actual HTTP pipeline without running its separately tested startup/workers.
        foreach (var service in builder.Services.Where(service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType?.Assembly == typeof(Program).Assembly).ToArray())
            builder.Services.Remove(service);
        var database = new ForbiddenSqlConnection();
        builder.Services.RemoveAll<DbContextOptions<DeyeSolarDbContext>>();
        builder.Services.AddSingleton(new DbContextOptionsBuilder<DeyeSolarDbContext>()
            .UseSqlServer(deployment.ConnectionString).AddInterceptors(database).Options);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Fixture-Authenticated"] == "yes")
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-user")], "fixture"));
            await next(context);
        });
        app.UseSolarApplication(deployment);
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var authenticated in new[] { false, true })
            {
                if (authenticated)
                {
                    client.DefaultRequestHeaders.Add("X-Fixture-Authenticated", "yes");
                    client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token-that-must-not-query-sql");
                }
                foreach (var path in new[] { "/api", "/api/unknown-endpoint", "/api/unknown.json", "/api/devices/state", "/api/settings/deye", "/api/settings/test/shelly" })
                    foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head })
                    {
                        using var response = await client.SendAsync(new HttpRequestMessage(method, path));
                        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
                        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
                        if (method != HttpMethod.Head)
                        {
                            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                            Assert.Equal("endpoint_not_found", error.RootElement.GetProperty("code").GetString());
                        }
                    }
            }
            client.DefaultRequestHeaders.Remove("X-Fixture-Authenticated");
            client.DefaultRequestHeaders.Authorization = null;
            Assert.True((await client.GetAsync("/api/auth/options")).IsSuccessStatusCode);
            Assert.Equal(0, database.Calls);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReadinessDeadlineDoesNotDependOnCheckCancellationAndLivenessStaysResponsive()
    {
        var check = new UncancellableCheck();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHealthChecks().AddCheck("uncancellable", check, tags: ["ready"]);
        await using var app = builder.Build();
        app.UseApplicationHealth();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            var started = System.Diagnostics.Stopwatch.StartNew();
            var readiness = client.GetAsync("/health/ready");
            await check.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await client.GetAsync("/health/live")).IsSuccessStatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await readiness.WaitAsync(TimeSpan.FromSeconds(7))).StatusCode);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(7));
        }
        finally { check.Completion.TrySetResult(HealthCheckResult.Healthy()); await app.StopAsync(); }
    }

    [Fact]
    public void WorkerHeartbeatDistinguishesDisabledFailedStaleAndRecoveredCycles()
    {
        var clock = new Clock();
        var reporter = new WorkerHealthReporter(clock);
        Assert.True(reporter.IsHealthy);
        reporter.Started("scheduler", TimeSpan.FromSeconds(20));
        Assert.True(reporter.IsHealthy);
        reporter.Failed("scheduler");
        Assert.False(reporter.IsHealthy);
        reporter.Succeeded("scheduler");
        Assert.True(reporter.IsHealthy);
        clock.Now = clock.Now.AddSeconds(21);
        Assert.False(reporter.IsHealthy);
        reporter.Succeeded("scheduler");
        Assert.True(reporter.IsHealthy);
        reporter.Stopped("scheduler");
        Assert.True(reporter.IsHealthy);
    }

    [SqlServerFact]
    public async Task FreshSchemaAndBootstrapRequireExplicitIndependentInstallationOwnership()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarFreshOwnership", SqlTestSchema.None);
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        try
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Installations.ToListAsync());
            Assert.Empty(await db.Users.ToListAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name='IntegrationDeviceAliases'
                """).SingleAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
                WHERE c.name='InstallationId'
                """).SingleAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id=OBJECT_ID('Readings')
                    AND name='BatterySocValid' AND is_nullable=1
                """).SingleAsync());

            await ProvisionAsync(null);
            Assert.Empty(await db.Installations.ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync("short"));
            Assert.Empty(await db.Installations.ToListAsync());
            Assert.Empty(await db.Users.ToListAsync());

            await ProvisionAsync("BootstrapFixture!42");
            var installation = Assert.Single(await db.Installations.AsNoTracking().ToListAsync());
            Assert.True(Guid.TryParseExact(installation.Id, "N", out _));
            var user = Assert.Single(await db.Users.AsNoTracking().ToListAsync());
            Assert.True(user.EmailConfirmed);
            var membership = Assert.Single(await db.InstallationMemberships.AsNoTracking().ToListAsync());
            Assert.Equal(user.Id, membership.UserId);
            Assert.Equal(installation.Id, membership.InstallationId);
            Assert.Equal("Owner", membership.Role);
            Assert.Empty(await db.IntegrationInstances.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.AppSettings.IgnoreQueryFilters().ToListAsync());

            await ProvisionAsync("BootstrapFixture!42");
            Assert.Equal(installation.Id, Assert.Single(await db.Installations.AsNoTracking().ToListAsync()).Id);
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT AppSettings(Section,[Key],Value) VALUES ('Fixture','MissingOwner','Rejected')"));
        }
        finally { await database.DisposeAsync(); }

        async Task ProvisionAsync(string? password)
        {
            var configuration = new ConfigurationBuilder().Build();
            var deployment = new DeploymentConfiguration(configuration, configuration, AuthProviderOptions.Capture(configuration),
                new AppleBillingOptions(), connection.ConnectionString, password, null, DatabaseStartupMode.Migrate,
                false, 120, null, Path.Combine(Path.GetTempPath(), "solar-test-integration-keys"), null);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<DeyeSolarDbContext>(builder => builder.UseSqlServer(connection.ConnectionString));
            services.AddIdentityCore<IdentityUser>(policy => policy.Password.RequiredLength = 12)
                .AddEntityFrameworkStores<DeyeSolarDbContext>();
            services.AddSingleton(deployment);
            services.AddSingleton<TimeProvider>(new Clock());
            services.AddScoped<IApplicationSeedData, ApplicationSeedData>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IApplicationSeedData>().SeedAsync(default);
        }
    }

    [SqlServerFact]
    public async Task ForwardSchemaCleanupDoesNotTreatMissingMeasurementValidityAsUsableData()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarValidityCleanup", SqlTestSchema.None);
        await using var db = new DeyeSolarDbContext(database.Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261004215555_RecoverableAccountOffboarding");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT Readings(InstallationId,Timestamp,BatterySoc,BatteryTemperature,BatteryVoltage,BatteryPower,
                    BatteryCurrent,SolarProduction,GridConsumption,LoadPower,DataSource,ConfigurationRevision,RuntimeGeneration,BatterySocValid)
                VALUES ('legacy',SYSUTCDATETIME(),90,20,48,0,0,5000,0,0,'validity-fixture',0,0,NULL);
                INSERT AppSettings(Section,[Key],Value) VALUES ('DeyeCloud','Password','obsolete-test-only');
                INSERT AppSettings(Section,[Key],Value) VALUES ('IntegrationMigration','Completed','1');
                """);
            await db.Database.MigrateAsync();
            var reading = await db.Readings.IgnoreQueryFilters().SingleAsync();
            Assert.False(reading.BatterySocValid);
            Assert.Equal(90, reading.BatterySoc);
            Assert.Empty(await db.AppSettings.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
                WHERE c.name='InstallationId'
                """).SingleAsync());
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task RuntimeSchemaValidationRejectsUnknownMigrationsAndPhysicalSchemaDrift()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarSchemaDrift", SqlTestSchema.None);
        await using var db = new DeyeSolarDbContext(database.Options);
        try
        {
            await db.Database.MigrateAsync();
            await DatabaseSchemaVerifier.VerifyAsync(db, false, default);
            await db.Database.ExecuteSqlRawAsync("INSERT __EFMigrationsHistory(MigrationId,ProductVersion) VALUES ('20991231000000_UnknownRelease','8.0.31')");
            var newer = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSchemaVerifier.VerifyAsync(db, false, default));
            Assert.Contains("newer release", newer.Message);
            await db.Database.ExecuteSqlRawAsync("DELETE __EFMigrationsHistory WHERE MigrationId='20991231000000_UnknownRelease'; DROP TABLE AppSettings");
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSchemaVerifier.VerifyAsync(db, false, default));
            Assert.Contains("missing", drift.Message);
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task UnavailableSignedBootstrapCannotStartExistingUsersBillingTrial()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarPreflight", SqlTestSchema.None);
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        var directory = Path.Combine(Path.GetTempPath(), "solar-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261004010452_DynamicIntegrationOAuth");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT AspNetUsers (Id, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed,
                    TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('preflight-existing', 'preflight-existing', 'PREFLIGHT-EXISTING', 0, 0, 0, 0, 0);
                """);
            var configuration = new ConfigurationBuilder().Build();
            var deployment = new DeploymentConfiguration(configuration, configuration, AuthProviderOptions.Capture(configuration),
                new AppleBillingOptions(), connection.ConnectionString, null, null, DatabaseStartupMode.Migrate, false, 120,
                Path.Combine(directory, "keys"), Path.Combine(directory, "keys", "integrations"), null);
            var runtime = Options.Create(new IntegrationRuntimeOptions { PackageDirectory = Path.Combine(directory, "packages"),
                BootstrapPackages = [new() { ArchivePath = Path.Combine(directory, "missing.zip"), ExpectedSha256 = new string('A', 64) }] });
            var store = new IntegrationPackageStore(runtime);
            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddSingleton<IIntegrationPackageManager>(store);
            services.AddSingleton<IIntegrationProviderCatalog>(store);
            services.AddSingleton(new IntegrationPackageBootstrap(store, runtime));
            await using var provider = services.BuildServiceProvider();
            var initializer = new ApplicationDatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(), deployment);
            await Assert.ThrowsAsync<FileNotFoundException>(() => initializer.InitializeAsync(default));
            Assert.DoesNotContain(await db.Database.GetAppliedMigrationsAsync(), migration => migration.EndsWith("_AccountBilling", StringComparison.Ordinal));
            Assert.Contains(await db.Database.GetPendingMigrationsAsync(), migration => migration.EndsWith("_AccountBilling", StringComparison.Ordinal));
        }
        finally { await database.DisposeAsync(); Directory.Delete(directory, true); }
    }

    [SqlServerFact]
    public async Task RuntimeSchemaValidationRejectsAdministrativeLoginAndAcceptsDmlOnlyPrincipal()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarLeastPrivilege", SqlTestSchema.None);
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        var login = "solar_test_" + Guid.NewGuid().ToString("N");
        var password = "Runtime!" + Guid.NewGuid().ToString("N") + "Aa1";
        var passwordFile = Path.GetTempFileName();
        try
        {
            await db.Database.MigrateAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSchemaVerifier.VerifyAsync(db, true, default));
            await File.WriteAllTextAsync(passwordFile, password);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Operations:RuntimeDatabaseUser"] = login, ["Operations:RuntimeDatabasePasswordFile"] = passwordFile }).Build();
            await RuntimeDatabaseProvisioner.ProvisionAsync(db, configuration, default);
            var restricted = new SqlConnectionStringBuilder(connection.ConnectionString) { UserID = login, Password = password };
            await using var runtime = new DeyeSolarDbContext(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(restricted.ConnectionString).Options);
            await DatabaseSchemaVerifier.VerifyAsync(runtime, true, default);
            await Assert.ThrowsAsync<SqlException>(() => runtime.Database.ExecuteSqlRawAsync("CREATE TABLE ForbiddenSchemaChange (Id int)"));
        }
        finally
        {
            await database.DisposeAsync();
            var master = new SqlConnectionStringBuilder(connection.ConnectionString) { InitialCatalog = "master" };
            await using var cleanup = new SqlConnection(master.ConnectionString);
            await cleanup.OpenAsync();
            await using var command = cleanup.CreateCommand();
            command.CommandText = $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{login}') DROP LOGIN [{login}]";
            await command.ExecuteNonQueryAsync();
            File.Delete(passwordFile);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class UncancellableCheck : IHealthCheck
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HealthCheckResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Completion.Task;
        }
    }
    private sealed class ForbiddenSqlConnection : DbConnectionInterceptor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        private InterceptionResult Fail()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Unknown API requests must not open SQL.");
        }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) => Fail();
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => new(Fail());
    }
}
