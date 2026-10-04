using System.Text.Json;
using System.Collections.Frozen;

namespace SolarManagement.Integrations.Contracts;

public static class IntegrationDescriptorValidator
{
    public static IReadOnlySet<string> SupportedUiFeatures { get; } = new[]
        { "text", "secret", "integer", "number", "boolean", "select", "wizard", "groups", "instructions", "conditional-fields", "oauth", "device-selector" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly HashSet<string> FieldKinds = new(StringComparer.Ordinal) { "text", "secret", "integer", "number", "boolean", "select" };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "test", "discover", "oauth" };

    public static void Validate(IntegrationProviderDescriptor descriptor)
    {
        if (descriptor.UiContractVersion != 1 || descriptor.ConfigurationVersion < 1 || !Text(descriptor.DisplayName, 256)
            || descriptor.Fields is null || descriptor.Fields.Count > 64
            || descriptor.Actions is null || descriptor.Actions.Count > 3 || descriptor.Actions.Distinct(StringComparer.Ordinal).Count() != descriptor.Actions.Count
            || descriptor.Actions.Any(action => !Actions.Contains(action))
            || descriptor.RequiredUiFeatures is null || descriptor.RequiredUiFeatures.Count > SupportedUiFeatures.Count
            || descriptor.RequiredUiFeatures.Distinct(StringComparer.Ordinal).Count() != descriptor.RequiredUiFeatures.Count
            || descriptor.RequiredUiFeatures.Any(feature => !SupportedUiFeatures.Contains(feature)))
            throw Invalid("Descriptor version, fields, actions or UI features are invalid.");
        var fields = new Dictionary<string, IntegrationFieldDescriptor>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            if (field is null || !Identifier(field.Key) || !fields.TryAdd(field.Key, field) || !FieldKinds.Contains(field.Kind)
                || !Text(field.Label, 256) || field.Secret && field.Kind is not ("secret" or "text")
                || field.Minimum is not null && field.Kind is not ("integer" or "number")
                || field.Maximum is not null && field.Kind is not ("integer" or "number")
                || field.Minimum > field.Maximum)
                throw Invalid("Configuration field identity or constraints are invalid.");
            if (field.Kind == "select")
            {
                if (field.Options is null || field.Options.Count is < 1 or > 100
                    || field.Options.Any(option => option is null || !Text(option.Value, 256) || !Text(option.Label, 256))
                    || field.Options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != field.Options.Count)
                    throw Invalid("Select options are invalid.");
            }
            else if (field.Options is not null) throw Invalid("Only select fields can declare options.");
            if (IntegrationUiConditions.IsSecret(field) && field.DefaultValue is not null)
                throw Invalid("A secret field cannot contain a descriptor default.");
            if (field.DefaultValue is { } value) ValidateValue(field, value);
        }
        foreach (var field in fields.Values) ValidateCondition(field.ActiveWhen, fields);
        var dependencies = fields.Values.ToDictionary(field => field.Key,
            field => field.ActiveWhen is { } condition ? new HashSet<string>([condition.Field], StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (fields.Values.Any(field => field.ActiveWhen is not null)) required.Add("conditional-fields");
        foreach (var field in fields.Values)
        {
            required.Add(field.Kind);
            if (IntegrationUiConditions.IsSecret(field)) required.Add("secret");
        }
        if (descriptor.UiLayout is { } layout)
        {
            required.Add("wizard"); required.Add("groups");
            if (layout.Version != 1 || layout.Steps is null || layout.Steps.Count is < 1 or > 12) throw Invalid("UI layout is invalid.");
            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var presented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in layout.Steps)
            {
                if (step is null || !Identifier(step.Id) || !stepIds.Add(step.Id) || !Text(step.Title, 256)
                    || !Instructions(step.Instructions) || step.Groups is null || step.Groups.Count is < 1 or > 12)
                    throw Invalid("UI step identity or content is invalid.");
                if (step.Instructions is not null) required.Add("instructions");
                foreach (var group in step.Groups)
                {
                    if (group is null || !Identifier(group.Id) || !groupIds.Add(group.Id) || !Text(group.Title, 256)
                        || !Instructions(group.Instructions) || group.FieldKeys is null || group.FieldKeys.Count > 64
                        || group.Actions is null || group.Actions.Count > 3
                        || group.Actions.Distinct(StringComparer.Ordinal).Count() != group.Actions.Count
                        || group.Actions.Any(action => !descriptor.Actions.Contains(action, StringComparer.Ordinal)))
                        throw Invalid("UI group identity or references are invalid.");
                    if (group.Instructions is not null) required.Add("instructions");
                    if (group.ActiveWhen is not null) required.Add("conditional-fields");
                    if (group.Actions.Contains("discover", StringComparer.Ordinal)) required.Add("device-selector");
                    ValidateCondition(group.ActiveWhen, fields);
                    foreach (var key in group.FieldKeys)
                        if (!fields.ContainsKey(key) || !presented.Add(key)) throw Invalid("Layout fields must have unique known references.");
                    if (group.ActiveWhen is { } groupCondition)
                        foreach (var key in group.FieldKeys) dependencies[key].Add(groupCondition.Field);
                    if (group.ActiveWhen is { } condition && group.FieldKeys.Contains(condition.Field, StringComparer.Ordinal))
                        throw Invalid("A group cannot hide its own controlling field.");
                }
            }
            if (presented.Count != fields.Count) throw Invalid("Every configuration field must appear once in the layout.");
            if (groupIds.Count > 48) throw Invalid("UI layout has too many groups.");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string key)
        {
            if (visited.Contains(key)) return;
            if (!active.Add(key)) throw Invalid("UI condition dependencies must not contain cycles.");
            foreach (var dependency in dependencies[key]) Visit(dependency);
            active.Remove(key);
            visited.Add(key);
        }
        foreach (var key in fields.Keys) Visit(key);
        if (descriptor.OAuthDefinition is { } oauth)
        {
            required.Add("oauth");
            if (!descriptor.Actions.Contains("oauth", StringComparer.Ordinal) || oauth.SecretFieldKeys is null || oauth.SecretFieldKeys.Count is < 1 or > 32
                || oauth.SecretFieldKeys.Distinct(StringComparer.Ordinal).Count() != oauth.SecretFieldKeys.Count
                || oauth.SecretFieldKeys.Any(key => !fields.TryGetValue(key, out var field) || !IntegrationUiConditions.IsSecret(field)))
                throw Invalid("OAuth outputs must name unique declared secret fields.");
        }
        else if (descriptor.Actions.Contains("oauth", StringComparer.Ordinal)) throw Invalid("OAuth action requires an OAuth definition.");
        if (required.Any(feature => !descriptor.RequiredUiFeatures.Contains(feature, StringComparer.Ordinal)))
            throw Invalid("Descriptor does not declare every required UI feature.");
    }

    public static void ValidateValue(IntegrationFieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        var valid = field.Kind switch
        {
            "text" or "secret" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= (IntegrationUiConditions.IsSecret(field) ? 8192 : 2048),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && InRange(field, number),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && InRange(field, number),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "select" => value.ValueKind == JsonValueKind.String && field.Options?.Any(option => option.Value == value.GetString()) == true,
            _ => false
        };
        if (!valid) throw Invalid("Configuration value does not match its declared field type or constraints.");
    }

    private static void ValidateCondition(IntegrationUiCondition? condition, IReadOnlyDictionary<string, IntegrationFieldDescriptor> fields)
    {
        if (condition is null) return;
        if (!fields.TryGetValue(condition.Field, out var source) || IntegrationUiConditions.IsSecret(source)
            || condition.Operator is not ("eq" or "notEq" or "present" or "absent"))
            throw Invalid("Conditions must use a known public field and supported operator.");
        if (condition.Operator is "present" or "absent")
        {
            if (condition.Value is not null) throw Invalid("Presence conditions cannot contain a comparison value.");
        }
        else
        {
            if (condition.Value is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw Invalid("Comparison condition value is missing.");
            ValidateValue(source, value);
        }
    }
    private static bool InRange(IntegrationFieldDescriptor field, decimal value) => (field.Minimum is null || value >= field.Minimum) && (field.Maximum is null || value <= field.Maximum);
    private static bool Identifier(string? value) => value is { Length: >= 1 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'));
    private static bool Instructions(string? value) => value is null || Text(value, 4096);
    private static InvalidDataException Invalid(string message) => new(message);
}
