using System.Security.Claims;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public sealed partial class IntegrationSetupService
{
    private IntegrationOAuthService OAuth => oauth ?? throw new IntegrationRequestException("authorization_unavailable", "Authorization is unavailable.", 503);

    public async Task<IntegrationOAuthStartDto> StartAuthorizationAsync(Guid id, IntegrationOAuthStartRequest request,
        ClaimsPrincipal actor, CancellationToken ct, string? mobileToken = null)
    {
        await EnsureManagerAsync(actor, ct);
        if (request.Draft is null) throw new IntegrationRequestException("validation", "Provide a settings draft before starting authorization.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var instance = await FindAsync(db, id, ct);
        var descriptor = await DescriptorAsync(instance, ct);
        if (descriptor.OAuthDefinition is null || request.Draft.OAuthFlowId is not null)
            throw new IntegrationRequestException("unsupported_action", "Start a new authorization using an ordinary settings draft.");
        var draft = Resolve(instance, await ConfigurationAsync(db, instance, ct), descriptor, request.Draft, allowMissingOAuthSecrets: true);
        return await OAuth.StartAsync(instance, descriptor, draft, actor, request.Client, mobileToken, ct);
    }
    public async Task<IntegrationOAuthStatusDto> AuthorizationStatusAsync(Guid id, Guid flowId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        return await OAuth.ReadAsync(current.Id!, id, flowId, actor, ct);
    }
    public async Task<IntegrationOAuthStatusDto> CancelAuthorizationAsync(Guid id, Guid flowId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        return await OAuth.CancelAsync(current.Id!, id, flowId, actor, ct);
    }
    private async Task<IntegrationDraftConfiguration> ResolveDraftAsync(IntegrationInstanceEntity instance,
        IntegrationConfigurationEntity saved, IntegrationProviderDescriptor descriptor, IntegrationConfigurationChange draft,
        ClaimsPrincipal actor, CancellationToken ct)
    {
        if (draft.OAuthFlowId is not { } id) return Resolve(instance, saved, descriptor, draft);
        var baseline = Resolve(instance, saved, descriptor, draft, allowMissingOAuthSecrets: true);
        if (descriptor.OAuthDefinition is null || descriptor.OAuthDefinition.SecretFieldKeys.Any(key =>
            draft.SecretOperations.TryGetValue(key, out var operation) && operation.Operation != "keep"))
            throw new IntegrationRequestException("authorization_draft_changed", "Start authorization again after changing its credentials.", 409);
        var ready = await OAuth.ReadyAsync(instance, id, baseline, actor, ct);
        var operations = new Dictionary<string, IntegrationSecretOperation>(draft.SecretOperations, StringComparer.Ordinal);
        foreach (var key in descriptor.OAuthDefinition.SecretFieldKeys)
            operations[key] = ready.Secrets.TryGetValue(key, out var value) ? new("replace", value) : new("clear");
        var values = ready.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return Resolve(instance, saved, descriptor, draft with { Values = values, SecretOperations = operations, OAuthFlowId = null });
    }
}
