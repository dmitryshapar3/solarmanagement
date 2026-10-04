using System.Net;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace DeyeSolar.Web.Operations;

public enum DatabaseStartupMode { Migrate, Validate }

/// <summary>Operator-only values are captured before editable installation configuration is loaded.</summary>
public sealed record DeploymentConfiguration(IConfiguration Configuration, IConfiguration IntegrationConfiguration,
    AuthProviderOptions AuthProviders, AppleBillingOptions AppleBilling, string ConnectionString,
    string? BootstrapAdminPassword, string? OpenMeteoApiKey, DatabaseStartupMode DatabaseMode,
    bool RequireLeastPrivilege, int StartupTimeoutSeconds, string? DataProtectionKeysPath,
    string IntegrationKeysPath, string? TrustedProxyAddresses)
{
    public static DeploymentConfiguration Capture(WebApplicationBuilder builder, bool migrateOnly)
    {
        builder.Configuration.AddJsonFile("integration-bootstrap.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        var configuration = builder.Configuration;
        if (configuration.GetValue("ASPNETCORE_FORWARDEDHEADERS_ENABLED", false))
            throw new InvalidOperationException("Disable automatic forwarded headers and configure explicit Auth:TrustedProxyAddresses.");
        var integration = new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable()
            .Where(value => value.Key.StartsWith("IntegrationRuntime:", StringComparison.OrdinalIgnoreCase)
                || value.Key.StartsWith("Integrations:", StringComparison.OrdinalIgnoreCase))).Build();
        var mode = configuration["Operations:DatabaseMode"] ?? (builder.Environment.IsDevelopment() ? "migrate" : "validate");
        if (!Enum.TryParse<DatabaseStartupMode>(mode, true, out var startupMode) || !Enum.IsDefined(startupMode))
            throw new InvalidOperationException("Operations:DatabaseMode must be migrate or validate.");
        if (migrateOnly) startupMode = DatabaseStartupMode.Migrate;
        if (!builder.Environment.IsDevelopment() && !migrateOnly && startupMode != DatabaseStartupMode.Validate)
            throw new InvalidOperationException("Production HTTP runtime requires validate mode. Run --migrate-only separately with the privileged connection.");
        var timeout = configuration.GetValue("Operations:StartupTimeoutSeconds", 120);
        if (timeout is < 10 or > 1800) throw new InvalidOperationException("Operations:StartupTimeoutSeconds must be 10 to 1800.");
        var keys = configuration["Auth:DataProtectionKeysPath"];
        if (keys is { Length: > 0 })
        {
            if (!Path.IsPathFullyQualified(keys)) throw new InvalidOperationException("Auth:DataProtectionKeysPath must be absolute.");
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys));
        }
        if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(keys))
            throw new InvalidOperationException("Production requires durable Auth:DataProtectionKeysPath.");
        var integrationKeys = integration["Integrations:KeyRingPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "integration-keys");
        if (!Path.IsPathFullyQualified(integrationKeys)) throw new InvalidOperationException("Integrations:KeyRingPath must be absolute.");
        var proxies = configuration["Auth:TrustedProxyAddresses"];
        if (!string.IsNullOrWhiteSpace(proxies))
        {
            var addresses = proxies.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed
                    : throw new InvalidOperationException("Auth:TrustedProxyAddresses must contain proxy IP addresses.")).ToArray();
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.KnownNetworks.Clear(); options.KnownProxies.Clear();
                foreach (var address in addresses) options.KnownProxies.Add(address);
            });
        }
        var connection = ReadSecret(configuration, "ConnectionStrings:DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection or its File setting is required.");
        var operatorConfiguration = new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable()).Build();
        return new(operatorConfiguration, integration, AuthProviderOptions.Capture(configuration), AppleBillingOptions.Capture(configuration),
            connection, ReadSecret(configuration, "Auth:BootstrapAdminPassword"), configuration["SolarEstimate:ApiKey"], startupMode,
            !builder.Environment.IsDevelopment() || configuration.GetValue("Operations:RequireLeastPrivilege", false), timeout, keys, integrationKeys, proxies);
    }

    private static string? ReadSecret(IConfiguration configuration, string key)
    {
        var file = configuration[key + "File"];
        if (string.IsNullOrWhiteSpace(file)) return configuration[key];
        if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException($"{key}File must be absolute.");
        return File.ReadAllText(file).TrimEnd('\r', '\n');
    }
}
