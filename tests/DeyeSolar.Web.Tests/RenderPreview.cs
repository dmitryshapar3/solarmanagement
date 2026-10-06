using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
namespace DeyeSolar.Web.Tests;
/// <summary>Exports optional native component review artifacts without changing render-test assertions.</summary>
internal static class RenderPreview
{
    public static async Task ExportAsync(HtmlRenderer renderer, string html, string? scenario, int maxWidth)
    {
        if (scenario is null || Environment.GetEnvironmentVariable("SOLAR_RENDER_OUTPUT") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var page = "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
            + "<link rel='stylesheet' href='tokens.css'><link rel='stylesheet' href='smartsolar.css'>"
            + "<body style='margin:0;padding:16px;background:var(--bg)'><div style='margin:0 auto;max-width:"
            + maxWidth.ToString(CultureInfo.InvariantCulture) + "px'>" + html + "</div></body></html>";
        await File.WriteAllTextAsync(Path.Combine(folder, scenario + ".html"), page);
    }
}
