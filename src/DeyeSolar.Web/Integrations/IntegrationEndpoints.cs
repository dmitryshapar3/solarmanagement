using DeyeSolar.Web.Auth;
using System.Security.Cryptography;
using System.Security.Claims;
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
        services.AddSingleton(provider => new IntegrationOAuthOptions
        {
            PublicBaseUrl = provider.GetRequiredService<DeyeSolar.Web.Auth.AuthProviderOptions>().PublicBaseUrl
        });
        services.AddSingleton<IntegrationOAuthService>();
        services.AddSingleton<IIntegrationOriginPolicyStore, IntegrationOriginPolicyFileStore>();
        services.AddScoped<IIntegrationManagerAccess, IntegrationManagerAccess>();
        services.AddSingleton<IIntegrationConnectionLifecycle, IntegrationConnectionLifecycle>();
        services.AddSingleton<IIntegrationConfigurationWriter, IntegrationConfigurationWriter>();
        services.AddSingleton<IIntegrationConfigurationResolver, IntegrationConfigurationResolver>();
        services.AddSingleton<IIntegrationSelectionTokens, IntegrationSelectionTokens>();
        services.AddSingleton<IIntegrationDeviceBindingWriter, IntegrationDeviceBindingWriter>();
        services.AddScoped<IntegrationSetupService>();
        services.AddScoped<IIntegrationRegistry, IntegrationRegistry>();
        return services;
    }

    public static void MapDynamicIntegrations(this WebApplication app)
    {
        app.MapGet("/integrations/oauth/callback", async Task<IResult> (HttpContext context, IntegrationOAuthService oauth, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            try
            {
                if (new[] { "state", "code", "error" }.Any(key => context.Request.Query[key].Count > 1))
                    return Results.BadRequest("The authorization callback is invalid.");
                var destination = await oauth.CallbackAsync(context.Request.Query["state"], context.Request.Query["code"],
                    context.Request.Query["error"], context.User, ct);
                return destination is null ? Results.Text("Authorization returned. Close this tab and check authorization in integration settings. Save the draft to apply it.")
                    : Results.Redirect(destination);
            }
            catch (IntegrationRequestException) { return Results.BadRequest("The authorization is unavailable. Return to integration settings and start again."); }
        }).AllowAnonymous();
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
        api.MapGet("/integration-socket-sources", (IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.SocketSourceOptionsAsync(ct)));
        api.MapPost("/integrations", (CreateIntegrationRequest request, IntegrationSetupService service, HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
            => WriteAsync(context, antiforgery, () => service.CreateAsync(request, context.User, ct)));
        api.MapGet("/integrations/{id:guid}/configuration", (Guid id, IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.ReadAsync(id, ct)));
        api.MapPost("/integrations/{id:guid}/oauth/start", (Guid id, IntegrationOAuthStartRequest request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () =>
                service.StartAuthorizationAsync(id, request, context.User, ct,
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? context.Request.Headers.Authorization.ToString()[7..].Trim() : null)));
        api.MapGet("/integrations/{id:guid}/oauth/{flowId:guid}", (Guid id, Guid flowId, IntegrationSetupService service,
            HttpContext context, CancellationToken ct) => ReadAsync(() => service.AuthorizationStatusAsync(id, flowId, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/oauth/{flowId:guid}/cancel", (Guid id, Guid flowId, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.CancelAuthorizationAsync(id, flowId, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/package", (Guid id, IntegrationPackageChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SwitchPackageAsync(id, request, context.User, ct)));
        api.MapPut("/integrations/{id:guid}/configuration", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SaveAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/test", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.TestAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/discovery", (Guid id, IntegrationConfigurationChange request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.DiscoverAsync(id, request, context.User, ct)));
        api.MapGet("/integrations/{id:guid}/devices", (Guid id, IntegrationSetupService service, CancellationToken ct) => ReadAsync(() => service.DevicesAsync(id, ct)));
        api.MapPut("/integrations/{id:guid}/devices/{deviceId:guid}/source", (Guid id, Guid deviceId, IntegrationSocketSourceChange request,
            IntegrationSetupService service, HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
            => WriteAsync(context, antiforgery, () => service.SetSocketSourceAsync(id, deviceId, request, context.User, ct))).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageIntegrations));
        api.MapPost("/integrations/{id:guid}/devices/selection", (Guid id, SelectIntegrationDeviceRequest request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SelectDeviceAsync(id, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/enable", (Guid id, IntegrationVersionGuard request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SetEnabledAsync(id, true, request, context.User, ct)));
        api.MapPost("/integrations/{id:guid}/disable", (Guid id, IntegrationVersionGuard request, IntegrationSetupService service,
            HttpContext context, IAntiforgery antiforgery, CancellationToken ct) => WriteAsync(context, antiforgery, () => service.SetEnabledAsync(id, false, request, context.User, ct)));
        api.MapPost("/devices/{id:guid}/commands", (Guid id, IntegrationSocketCommandRequest request, DynamicSocketGateway gateway,
            HttpContext context, IAntiforgery antiforgery, InteractiveSecurityContext security, CancellationToken ct) => WriteAsync(context, antiforgery, async () =>
            {
                var socket = await gateway.GetForUserAsync(new(id), context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    token => security.EnsureAsync(InstallationPermission.ControlDevices, token), ct);
                await socket.SetPowerAsync(new(new(request.CommandId), request.IsOn ? SwitchState.On : SwitchState.Off), ct);
                return await gateway.DescribeResultAsync(id, request.CommandId, ct);
            })).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ControlDevices));
        api.MapGet("/devices/{id:guid}/commands/{commandId:guid}", (Guid id, Guid commandId, DynamicSocketGateway gateway, CancellationToken ct)
            => ReadAsync(() => gateway.DescribeResultAsync(id, commandId, ct)));
        api.MapGet("/devices/{id:guid}/commands", (Guid id, DynamicSocketGateway gateway, CancellationToken ct)
            => ReadAsync(() => gateway.UnresolvedAsync(id, ct)));
        api.MapPost("/devices/{id:guid}/commands/{commandId:guid}/release", (Guid id, Guid commandId, DynamicSocketGateway gateway,
            HttpContext context, IAntiforgery antiforgery, InteractiveSecurityContext security, CancellationToken ct) => WriteAsync(context, antiforgery, async () =>
            {
                await gateway.ReleaseForUserAsync(new(id), new(commandId), context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    token => security.EnsureAsync(InstallationPermission.ControlDevices, token), ct);
                return await gateway.DescribeResultAsync(id, commandId, ct);
            })).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ControlDevices));
        api.MapPost("/integration-packages/install", async Task<IResult> (IntegrationPackageInstallRequest request, IIntegrationPackageManager manager,
            HttpContext context, IAntiforgery antiforgery, UserManager<IdentityUser> users,
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
                return installed;
            });
        });
        api.MapPost("/integration-packages/approved-origins", async Task<IResult> (IntegrationOriginApprovalRequest request,
            IIntegrationPackageManager manager, HttpContext context, IAntiforgery antiforgery, UserManager<IdentityUser> users, CancellationToken ct) =>
        {
            if (!await AllowedRequestAsync(context, antiforgery)) return Results.BadRequest(new IntegrationApiError("antiforgery", "A valid request verification token is required."));
            var user = await users.GetUserAsync(context.User);
            if (user is null || !await users.IsInRoleAsync(user, "PlatformOperator"))
                return Results.Json(new IntegrationApiError("forbidden", "Only a platform operator can approve provider network origins."), statusCode: 403);
            return await ReadAsync(async () =>
            {
                await manager.ApproveOriginAsync(request.Origin, ct);
                return new { approved = true };
            });
        });
    }

    private static async Task<IResult> ReadAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (DeyeSolar.Web.Billing.BillingAccessException ex) { return Results.Json(new IntegrationApiError("subscription_required", ex.Message), statusCode: 402); }
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
