namespace DeyeSolar.Domain.Options;

public static class SocketBackendModes
{
    public const string CloudTuya = "CloudTuya";
    public const string HomeBridge = "HomeBridge";

    public static bool IsHomeBridge(string? mode)
        => string.Equals(mode, HomeBridge, StringComparison.OrdinalIgnoreCase);
}

public class SocketBackendOptions
{
    public const string Section = "SocketBackend";

    public string Mode { get; set; } = SocketBackendModes.CloudTuya;
    public string BridgeId { get; set; } = "home-main";
    public string BearerToken { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 8;
    public int StateStaleAfterSeconds { get; set; } = 30;
}
