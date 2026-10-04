namespace DeyeSolar.Domain.Options;

public class DeyeCloudOptions
{
    public const string Section = "DeyeCloud";

    public string BaseUrl { get; set; } = "https://eu1-developer.deyecloud.com/v1.0";
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public long StationId { get; set; }
    public string DeviceSn { get; set; } = string.Empty;
}
