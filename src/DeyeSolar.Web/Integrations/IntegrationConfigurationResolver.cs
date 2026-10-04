using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

public interface IIntegrationConfigurationResolver
{
    Dictionary<string, string> ReadSecrets(IntegrationInstanceEntity instance, IntegrationConfigurationEntity config);
    IntegrationDraftConfiguration Resolve(IntegrationInstanceEntity instance, IntegrationConfigurationEntity saved,
        IntegrationProviderDescriptor descriptor, IntegrationConfigurationChange draft, bool allowMissingOAuthSecrets = false);
}

/// <summary>Owns configuration shape, active-field validation and explicit credential keep/clear/replace semantics.</summary>
public sealed class IntegrationConfigurationResolver(IntegrationSecretStore secrets) : IIntegrationConfigurationResolver
{
    public Dictionary<string, string> ReadSecrets(IntegrationInstanceEntity instance, IntegrationConfigurationEntity config)
        => secrets.Decrypt(instance.InstallationId, instance.Id, config.Revision, config.SecretsCiphertext);
    public static Dictionary<string, JsonElement> ReadValues(IntegrationConfigurationEntity config)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ValuesJson, IntegrationJson.Options) ?? new(StringComparer.Ordinal);
    public IntegrationDraftConfiguration Resolve(IntegrationInstanceEntity instance, IntegrationConfigurationEntity saved,
        IntegrationProviderDescriptor descriptor, IntegrationConfigurationChange draft, bool allowMissingOAuthSecrets = false)
    {
        IntegrationConfigurationIdentity.Guard(instance, draft.ExpectedRevision, draft.PackageVersion, draft.PackageDigest, draft.DescriptorDigest);
        if (draft.Values is null || draft.SecretOperations is null || draft.Values.Count > 100 || draft.SecretOperations.Count > 100)
            throw new IntegrationRequestException("validation", "The configuration fields are invalid.");
        var fields = descriptor.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (draft.Values.Keys.Any(k => !fields.TryGetValue(k, out var f) || f.Secret || f.Kind == "secret")
            || draft.SecretOperations.Keys.Any(k => !fields.TryGetValue(k, out var f) || !f.Secret && f.Kind != "secret"))
            throw new IntegrationRequestException("validation", "The configuration contains an unknown field.");
        var resolvedSecrets = ReadSecrets(instance, saved);
        var values = new Dictionary<string, JsonElement>(draft.Values, StringComparer.Ordinal);
        var conditionValues = ReadValues(saved);
        foreach (var (key, value) in values) conditionValues[key] = value;
        var effectiveValues = IntegrationUiConditions.EffectiveValues(descriptor, IntegrationJson.Element(conditionValues));
        foreach (var (key, operation) in draft.SecretOperations)
        {
            if (operation is null) throw new IntegrationRequestException("validation", "Choose a credential operation.");
            switch (operation.Operation)
            {
                case "keep": break;
                case "clear": resolvedSecrets.Remove(key); break;
                case "replace" when operation.Value is { Length: > 0 and <= 8192 }: resolvedSecrets[key] = operation.Value; break;
                default: throw new IntegrationRequestException("validation", "Enter a credential or choose keep or clear.");
            }
        }
        foreach (var field in descriptor.Fields)
        {
            var active = IntegrationUiConditions.IsFieldActive(descriptor, field, effectiveValues);
            var required = field.Required && active;
            if (field.Secret || field.Kind == "secret")
            {
                var pendingOAuth = allowMissingOAuthSecrets && descriptor.OAuthDefinition?.SecretFieldKeys.Contains(field.Key, StringComparer.Ordinal) == true;
                if (required && !pendingOAuth && (!resolvedSecrets.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)))
                    throw new IntegrationRequestException("validation", $"Enter {field.Label}.");
                continue;
            }
            if (!active && !values.ContainsKey(field.Key) && conditionValues.TryGetValue(field.Key, out var retained))
                values[field.Key] = retained;
            if (!values.TryGetValue(field.Key, out var json) || json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (required) throw new IntegrationRequestException("validation", $"Enter {field.Label}.");
                continue;
            }
            try { IntegrationDescriptorValidator.ValidateValue(field, json); }
            catch (InvalidDataException) { throw new IntegrationRequestException("validation", $"Check {field.Label}."); }
            if (required && json.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(json.GetString()))
                throw new IntegrationRequestException("validation", $"Enter {field.Label}.");
        }
        return new(IntegrationJson.Element(values), resolvedSecrets);
    }
    public static string Fingerprint(IntegrationDraftConfiguration value)
    {
        var orderedValues = value.Values.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var orderedSecrets = value.Secrets.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { values = orderedValues, secrets = orderedSecrets }, IntegrationJson.Options))));
    }
}

internal static class IntegrationConfigurationIdentity
{
    public static void Guard(IntegrationInstanceEntity instance, long revision, string version, string digest, string descriptor)
    {
        if (instance.Revision != revision || instance.PackageVersion != version || instance.PackageDigest != digest || instance.DescriptorDigest != descriptor)
            throw new IntegrationRequestException("configuration_conflict", "The integration changed. Reload its settings before continuing.", 409);
    }
}
