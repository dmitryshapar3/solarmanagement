namespace DeyeSolar.Domain.Models;

public static class SocketDeviceSources
{
    public const string Tuya = "tuya";
    public const string Shelly = "shelly";
}

public static class SocketEntityIds
{
    public static string Create(string source, string rawId)
        => $"{source}:{rawId}";

    public static bool TryParse(string entityId, out string source, out string rawId)
    {
        source = string.Empty;
        rawId = entityId;

        var separatorIndex = entityId.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == entityId.Length - 1)
            return false;

        var candidateSource = entityId[..separatorIndex].ToLowerInvariant();
        if (candidateSource is not SocketDeviceSources.Tuya and
            not SocketDeviceSources.Shelly)
        {
            return false;
        }

        source = candidateSource;
        rawId = entityId[(separatorIndex + 1)..];
        return true;
    }

    public static string RawIdOrSelf(string entityId)
        => TryParse(entityId, out _, out var rawId) ? rawId : entityId;
}
