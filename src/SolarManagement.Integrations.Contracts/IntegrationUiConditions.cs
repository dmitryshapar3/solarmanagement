using System.Text.Json;

namespace SolarManagement.Integrations.Contracts;

public static class IntegrationUiConditions
{
    public static bool IsActive(IntegrationUiCondition? condition, JsonElement values)
    {
        if (condition is null) return true;
        if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Public configuration must be an object.", nameof(values));
        var exists = values.TryGetProperty(condition.Field, out var actual)
            && actual.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && (actual.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(actual.GetString()));
        return condition.Operator switch
        {
            "present" => exists,
            "absent" => !exists,
            "eq" => exists && condition.Value is { } expected && EqualsScalar(actual, expected),
            "notEq" => exists && condition.Value is { } expected && !EqualsScalar(actual, expected),
            _ => throw new ArgumentException("Unsupported UI condition operator.", nameof(condition))
        };
    }

    public static bool IsFieldActive(IntegrationProviderDescriptor descriptor, IntegrationFieldDescriptor field, JsonElement values)
    {
        var effective = EffectiveValues(descriptor, values);
        if (!IsActive(field.ActiveWhen, effective)) return false;
        var group = descriptor.UiLayout?.Steps.SelectMany(step => step.Groups)
            .SingleOrDefault(group => group.FieldKeys.Contains(field.Key, StringComparer.Ordinal));
        return IsActive(group?.ActiveWhen, effective);
    }

    public static JsonElement EffectiveValues(IntegrationProviderDescriptor descriptor, JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object) throw new ArgumentException("Public configuration must be an object.", nameof(values));
        var result = values.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        foreach (var field in descriptor.Fields.Where(field => !IsSecret(field)))
            if (!result.ContainsKey(field.Key) && field.DefaultValue is { } value) result.Add(field.Key, value.Clone());
        return IntegrationJson.Element(result);
    }

    public static bool IsSecret(IntegrationFieldDescriptor field) => field.Secret || field.Kind == "secret";

    private static bool EqualsScalar(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)
            return actual.TryGetDouble(out var first) && expected.TryGetDouble(out var second)
                && double.IsFinite(first) && double.IsFinite(second) && first == second;
        if (actual.ValueKind != expected.ValueKind) return false;
        return actual.ValueKind switch
        {
            JsonValueKind.String => string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => actual.GetBoolean() == expected.GetBoolean(),
            _ => false
        };
    }
}
