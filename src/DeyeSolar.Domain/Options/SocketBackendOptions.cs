namespace DeyeSolar.Domain.Options;

public static class SocketBackendModes
{
    public const string CloudTuya = "CloudTuya";
    public const string CloudShelly = "CloudShelly";

    public static bool IsCloudShelly(string? mode)
        => string.Equals(mode, CloudShelly, StringComparison.OrdinalIgnoreCase);
}

public class SocketBackendOptions
{
    public const string Section = "SocketBackend";

    public string Mode { get; set; } = SocketBackendModes.CloudTuya;
}
