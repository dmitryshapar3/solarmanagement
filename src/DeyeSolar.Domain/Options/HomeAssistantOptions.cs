namespace DeyeSolar.Domain.Options;

public class HomeAssistantOptions
{
    public const string Section = "HomeAssistant";

    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";
    public string AccessToken { get; set; } = string.Empty;
    public string DefaultEntityId { get; set; } = "switch.sp401m";
}
