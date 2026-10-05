using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace DeyeSolar.Web.Tests;

/// <summary>Exports optional component review artifacts without changing their render-test assertions.</summary>
internal static class RenderPreview
{
    public static async Task ExportAsync(HtmlRenderer renderer, string html, string? scenario, int maxWidth)
    {
        if (scenario is null || Environment.GetEnvironmentVariable("SOLAR_RENDER_OUTPUT") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var theme = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<MudThemeProvider>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["IsDarkMode"] = true,
                ["Theme"] = new MudTheme { PaletteDark = new PaletteDark {
                    Primary = "#42a5f5", Secondary = "#ffb74d", Success = "#66bb6a",
                    Surface = "#1e1e2e", Background = "#121212", AppbarBackground = "#1e1e2e" } }
            }));
            return output.ToHtmlString();
        });
        var page = "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
            + "<link rel='stylesheet' href='mud.css'><link rel='stylesheet' href='solar.css'>"
            + "<body style='margin:0;padding:16px;background:#121212'><div style='margin:0 auto;max-width:"
            + maxWidth.ToString(CultureInfo.InvariantCulture) + "px'>" + theme + html + "</div></body></html>";
        await File.WriteAllTextAsync(Path.Combine(folder, scenario + ".html"), page);
    }
}
