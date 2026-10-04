using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task RuntimeSchemaValidationRejectsUnknownMigrationsAndPhysicalSchemaDrift()
    {
        var connection = Connection("SolarSchemaDrift_");
        await using var db = new DeyeSolarDbContext(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
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
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [SqlServerFact]
    public async Task UnavailableSignedBootstrapCannotStartExistingUsersBillingTrial()
    {
        var connection = Connection("SolarPreflight_");
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new DeyeSolarDbContext(options);
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
        finally { await db.Database.EnsureDeletedAsync(); Directory.Delete(directory, true); }
    }

    [SqlServerFact]
    public async Task RuntimeSchemaValidationRejectsAdministrativeLoginAndAcceptsDmlOnlyPrincipal()
    {
        var connection = Connection("SolarLeastPrivilege_");
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new DeyeSolarDbContext(options);
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
            await db.Database.EnsureDeletedAsync();
            var master = new SqlConnectionStringBuilder(connection.ConnectionString) { InitialCatalog = "master" };
            await using var cleanup = new SqlConnection(master.ConnectionString);
            await cleanup.OpenAsync();
            await using var command = cleanup.CreateCommand();
            command.CommandText = $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{login}') DROP LOGIN [{login}]";
            await command.ExecuteNonQueryAsync();
            File.Delete(passwordFile);
        }
    }

    private static SqlConnectionStringBuilder Connection(string prefix) => new(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
        { InitialCatalog = prefix + Guid.NewGuid().ToString("N") };
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
}
