using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using SolarManagement.SmartSockets.Contracts;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public static class IntegrationEndpoints
{
    public static IServiceCollection AddDynamicIntegrations(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var keyPath = configuration["Integrations:KeyRingPath"] ?? Path.Combine(contentRoot, "data", "integration-keys");
        var directory = new DirectoryInfo(Path.GetFullPath(keyPath));
        directory.Create();
        services.AddDataProtection();
        services.AddSingleton(_ => new IntegrationSecretStore(DataProtectionProvider.Create(directory,
            options => options.SetApplicationName("SolarManagement.Integrations.v1"))));
        services.AddSingleton<IntegrationChangeNotifier>();
        services.AddSingleton<IntegrationSetupGate>();
        services.AddSingleton<LegacyIntegrationBootstrap>();
        services.AddScoped<IntegrationSetupService>();
        services.AddScoped<IIntegrationRegistry, IntegrationRegistry>();
        return services;
    }

    public static void MapDynamicIntegrations(this WebApplication app)
    {
        var api = app.MapGroup("/api/v2").RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        api.MapGet("/integration-providers", async Task<IResult> (IIntegrationProviderCatalog catalog, HttpContext context, CancellationToken ct) =>
        {
            var providers = await catalog.GetProvidersAsync(ct);
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", providers.OrderBy(p => p.ProviderId, StringComparer.Ordinal)
                .Select(p => $"{p.ProviderId}:{p.PackageVersion}:{p.PackageDigest}:{p.DescriptorDigest}"))))).ToLowerInvariant();
            context.Response.Headers.ETag = '"' + revision + '"';
            if (context.Request.Headers.IfNoneMatch == context.Response.Headers.ETag) return Results.StatusCode(304);
            return Results.Ok(new { providers, revision });
        });
        api.MapGet("/integration-providers/{id}/versions/{version}/ui", (string id, string version, IIntegrationProviderCatalog catalog, CancellationToken ct)
            => ReadAsync(() => catalog.GetAsync(id, version, ct)));
        api.MapGet("/integration-providers/{id}/versions", (string id, IIntegrationProviderCatalog catalog, CancellationToken ct)
            => ReadAsync(() => catalog.GetVersionsAsync(id, ct)));
        api.MapGet("/integrations", (IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.ListAsync(ct)));
        api.MapPost("/integrations", (CreateIntegrationRequest request, IntegrationSetupService service, HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
            => WriteAsync(context, antiforgery, () => service.CreateAsync(request, context.User, ct)));
        api.MapGet("/integrations/{id:guid}/configuration", (Guid id, IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.ReadAsync(id, ct)));
        api.MapPost("/integrations/{id:guid}/package", (Guid id, IntegrationPackageChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SwitchPackageAsync(id, request, context.User, ct)));
        api.MapPut("/integrations/{id:guid}/configuration", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SaveAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/test", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.TestAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/discovery", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.DiscoverAsync(id, request, context.User, ct)));
        api.MapGet("/integrations/{id:guid}/devices", (Guid id, IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.DevicesAsync(id, ct)));
        api.MapPost("/integrations/{id:guid}/devices/selection", (Guid id, SelectIntegrationDeviceRequest request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SelectDeviceAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/enable", (Guid id, IntegrationVersionGuard request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SetEnabledAsync(id, true, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/disable", (Guid id, IntegrationVersionGuard request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SetEnabledAsync(id, false, request, context.User, ct)));
        api.MapPost("/devices/{id:guid}/commands", (Guid id, IntegrationSocketCommandRequest request, DynamicSocketGateway gateway,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, async () =>
            {
                var socket = await gateway.GetAsync(new(id), ct);
                await socket.SetPowerAsync(new(new(request.CommandId), request.IsOn ? SwitchState.On : SwitchState.Off), ct);
                return await gateway.DescribeResultAsync(id, request.CommandId, ct);
            }));
        api.MapGet("/devices/{id:guid}/commands/{commandId:guid}", (Guid id, Guid commandId, DynamicSocketGateway gateway, CancellationToken ct)
            => ReadAsync(() => gateway.DescribeResultAsync(id, commandId, ct)));
        api.MapGet("/devices/{id:guid}/commands", (Guid id, DynamicSocketGateway gateway, CancellationToken ct)
            => ReadAsync(() => gateway.UnresolvedAsync(id, ct)));
        api.MapPost("/devices/{id:guid}/commands/{commandId:guid}/release", (Guid id, Guid commandId, DynamicSocketGateway gateway,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => gateway.ReleaseAsync(id, commandId, ct)));
        api.MapPost("/integration-packages/install", async Task<IResult> (IntegrationPackageInstallRequest request, IIntegrationPackageManager manager,
            HttpContext context, IAntiforgery antiforgery, UserManager<IdentityUser> users,
            LegacyIntegrationBootstrap legacy, CurrentInstallation installation, TenantRuntimeRegistry runtimes,
            IntegrationChangeNotifier changes, CancellationToken ct) =>
        {
            if (!await AllowedRequestAsync(context, antiforgery)) return Results.BadRequest(new IntegrationApiError("antiforgery", "A valid request verification token is required."));
            var user = await users.GetUserAsync(context.User);
            if (user is null || !await users.IsInRoleAsync(user, "PlatformOperator"))
                return Results.Json(new IntegrationApiError("forbidden", "Only a platform operator can install provider packages."), statusCode: 403);
            return await ReadAsync(async () =>
            {
                var installed = await manager.InstallAsync(request, ct);
                changes.Publish("", Guid.Empty);
                var id = installation.Id ?? throw new InvalidOperationException("An installation is required.");
                var runtime = await runtimes.GetAsync(id, ct);
                if (await legacy.RunAsync(runtime.Resolve<IDbContextFactory<DeyeSolarDbContext>>(), runtime.Resolve<IConfiguration>(), ct))
                    await runtime.RefreshSettingsAsync(ct);
                return installed;
            });
        });
    }

    private static async Task<IResult> ReadAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (IntegrationRequestException ex) { return Results.Json(new IntegrationApiError(ex.Code, ex.Message), statusCode: ex.Status); }
        catch (KeyNotFoundException) { return Results.NotFound(new IntegrationApiError("provider_not_found", "The requested provider package is not installed.")); }
        catch (InvalidDataException) { return Results.BadRequest(new IntegrationApiError("invalid_package", "The provider package could not be verified.")); }
        catch (ArgumentException ex) { return Results.BadRequest(new IntegrationApiError("validation", ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(new IntegrationApiError("operation_conflict", ex.Message)); }
    }
    private static async Task<IResult> WriteAsync<T>(HttpContext context, IAntiforgery antiforgery, Func<Task<T>> action)
    {
        if (!await AllowedRequestAsync(context, antiforgery)) return Results.BadRequest(new IntegrationApiError("antiforgery", "A valid request verification token is required."));
        return await ReadAsync(action);
    }
    private static async Task<bool> AllowedRequestAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var bearer = await context.AuthenticateAsync(MobileBearerAuthenticationHandler.SchemeName);
        var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (bearer.Succeeded && !cookie.Succeeded) return true;
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
