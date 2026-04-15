namespace DeyeSolar.Domain.Options;

public class SocketBackendOptions
{
    public const string Section = "SocketBackend";

    public string BridgeId { get; set; } = "home-main";
    public string BearerToken { get; set; } = string.Empty;
    public string DefaultDeviceId { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 8;
    public int StateStaleAfterSeconds { get; set; } = 30;
}
