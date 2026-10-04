using System.Text.Json;
using System.Text.Json.Nodes;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

// Host-owned metadata is separate from the capabilities accepted from discovery.
public static class IntegrationSocketAssociation
{
    public sealed record Association(Guid? SourceInverterId, int PhaseCount);

    public static Association Read(IntegrationDeviceBindingEntity binding)
    {
        if (binding.Kind != "socket") return new(null, 1);
        try
        {
            using var json = JsonDocument.Parse(binding.MetadataJson);
            if (!json.RootElement.TryGetProperty("automation", out var settings) || settings.ValueKind != JsonValueKind.Object)
                return new(null, 1);
            var source = settings.TryGetProperty("sourceInverterId", out var value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty ? (Guid?)id : null;
            var phases = settings.TryGetProperty("phaseCount", out var count) && count.TryGetInt32(out var number) && number == 3 ? 3 : 1;
            return new(source, phases);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new(null, 1); }
    }

    public static string Write(IntegrationDeviceBindingEntity binding, Guid? source, int phaseCount)
    {
        var metadata = JsonNode.Parse(binding.MetadataJson) as JsonObject ?? new JsonObject();
        metadata["automation"] = new JsonObject
        {
            ["sourceInverterId"] = source?.ToString("D"), ["phaseCount"] = phaseCount
        };
        return metadata.ToJsonString();
    }

    public static async Task<Dictionary<int, Guid?>> ResolveSourcesAsync(DeyeSolarDbContext db,
        IReadOnlyList<TriggerRule> rules, CancellationToken ct)
    {
        if (rules.Count == 0 || rules.All(rule => rule.SourceInverterId.HasValue))
            return rules.ToDictionary(rule => rule.Id, rule => rule.SourceInverterId);
        var bindings = await db.IntegrationDeviceBindings.AsNoTracking().Where(b => b.Kind == "socket" && b.Enabled).ToListAsync(ct);
        var sources = bindings.ToDictionary(b => b.Id, b => Read(b).SourceInverterId);
        return rules.ToDictionary(r => r.Id, r => r.SourceInverterId ??
            (Guid.TryParse(r.EntityId, out var id) ? sources.GetValueOrDefault(id) : null));
    }
}
