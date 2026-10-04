namespace DeyeSolar.Domain.Options;

public class ShellyOptions
{
    public const string Section = "Shelly";

    public string ServerUri { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int RequestIntervalMilliseconds { get; set; } = 1100;
}
