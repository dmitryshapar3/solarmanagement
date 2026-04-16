namespace DeyeSolar.Domain.Options;

public class TuyaOptions
{
    public const string Section = "Tuya";

    public string AccessId { get; set; } = string.Empty;
    public string AccessSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://openapi.tuyaeu.com";
    public string DeviceId { get; set; } = string.Empty;
}
