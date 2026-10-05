using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Operations;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace DeyeSolar.Web.Api;

public static class IntegrationManagementEndpoints
{
    public static IServiceCollection AddIntegrationManagement(this IServiceCollection services)
    {
        services.AddAntiforgery();
        services.AddHttpClient(IntegrationTestService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(16))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(ProviderEndpointPolicy.CreateHandler);
        services.AddSingleton<IntegrationProbeGate>();
        services.AddScoped<IIntegrationTestService, IntegrationTestService>();
        services.AddScoped<IDeviceLabelStore, AppSettingsDeviceLabelStore>();
        services.AddScoped<DeviceNameService>();
        services.AddScoped<SiteSettingsService>();
        return services;
    }

    public static void MapIntegrationManagement(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        api.MapGet("/settings/site", async (SiteSettingsService site) => Results.Ok(await site.LoadAsync()));
        api.MapPut("/settings/site", async Task<IResult> (SiteSettingsDto request, SiteSettingsService site,
            HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await AuthenticatedMutationPolicy.IsAllowedAsync(context, antiforgery)) return ApiProblems.InvalidAntiforgery();
            if (!SiteSettingsService.TryValidate(request, out var error)) return ApiProblems.Error(error, code: "validation");
            try { await site.SaveAsync(request); }
            catch (ArgumentException exception) { return ApiProblems.Error(exception.Message, code: "validation"); }
            return Results.NoContent();
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));
        api.MapPost("/settings/test/{kind:regex(^(openmeteo|pse)$)}", async Task<IResult> (string kind, IntegrationTestRequest request,
            HttpContext context, IAntiforgery antiforgery, IIntegrationTestService tests, CancellationToken ct) =>
        {
            if (!await AuthenticatedMutationPolicy.IsAllowedAsync(context, antiforgery)) return ApiProblems.InvalidAntiforgery();
            return Results.Ok(await tests.TestAsync(kind, request, ct));
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));
        api.MapPatch("/devices/{id}/name", async Task<IResult> (string id, DeviceNameRequest request,
            HttpContext context, IAntiforgery antiforgery, DeviceNameService names, CancellationToken ct) =>
        {
            if (!await AuthenticatedMutationPolicy.IsAllowedAsync(context, antiforgery)) return ApiProblems.InvalidAntiforgery();
            if (!DeviceNameService.TryName(request.Name, out var name)) return ApiProblems.Error("Use a device name of up to 80 characters without control characters.", code: "validation");
            var device = await names.RenameAsync(id, name, ct);
            return device is null ? ApiProblems.Error("Refresh devices and choose a device from this installation.", 404) : Results.Ok(device);
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));
    }

    // Native requests authenticate using bearer tokens and never ambient cookies. Cookie requests
    // need the Identity/Razor antiforgery pair before provider probes or local label writes.
}
