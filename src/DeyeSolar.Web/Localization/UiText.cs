using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;

namespace DeyeSolar.Web.Localization;

public sealed record UiLanguage(string Code, string Name);

/// <summary>Shared, offline translations; phrase keys also serve as the English fallback.</summary>
public sealed class UiText(IHttpContextAccessor context, IAntiforgery antiforgery)
{
    public static readonly IReadOnlyList<UiLanguage> SupportedLanguages = [
        new("en", "English"), new("ru", "Русский"), new("uk", "Українська"), new("pl", "Polski"),
        new("de", "Deutsch"), new("fr", "Français"), new("es", "Español"), new("it", "Italiano"),
        new("pt", "Português"), new("nl", "Nederlands"), new("cs", "Čeština"), new("tr", "Türkçe"),
        new("zh", "中文"), new("ja", "日本語"), new("ko", "한국어")];
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Catalogs = new(LoadCatalogs);
    private static readonly Lazy<(string Key, Regex Pattern)[]> Templates = new(() =>
        Catalogs.Value.GetValueOrDefault("en", []).Keys.Where(key => Regex.IsMatch(key, @"\{\d+\}"))
            .OrderByDescending(key => Regex.Replace(key, @"\{\d+\}", "").Length)
            .Select(key => (key, new Regex("^" + string.Concat(Regex.Split(key, @"(\{\d+\})")
                .Select(part => Regex.IsMatch(part, @"^\{\d+\}$") ? $"(?<p{part[1..^1]}>.+?)" : Regex.Escape(part))) + "$", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))).ToArray());
    private string? token;
    private bool circuitInitialized;
    private CultureInfo culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
    private CultureInfo uiCulture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone());
    // A request or circuit owns its locale. Background telemetry must not select a user's language.
    public CultureInfo Culture => culture;
    public CultureInfo UICulture => uiCulture;
    public string Language => Normalize(uiCulture.Name) ?? "en";
    public void InitializeCircuit(string? cultureName, string? uiCultureName)
    {
        if (circuitInitialized) return;
        circuitInitialized = true;
        if (Normalize(cultureName) is null || Normalize(uiCultureName) is null) return;
        try
        {
            var formatCulture = CultureInfo.GetCultureInfo(cultureName!);
            var translationCulture = CultureInfo.GetCultureInfo(uiCultureName!);
            culture = formatCulture;
            uiCulture = translationCulture;
        }
        catch (CultureNotFoundException) { }
    }
    public string Number(int? value, string format = "0") => Components.Ui.UiFormat.Number(value, format, culture);
    public string Number(double? value, string format = "0.00") => Components.Ui.UiFormat.Number(value, format, culture);
    public string Number(decimal? value, string format = "0.00") => Components.Ui.UiFormat.Number(value, format, culture);
    public string Signed(double? value, string format = "0") => Components.Ui.UiFormat.Signed(value, format, culture);
    public string Time(DateTimeOffset value, string zone, string format = "d MMM · HH:mm zzz")
        => Components.Ui.UiFormat.Time(value, zone, format, culture);
    public IReadOnlyList<UiLanguage> Languages => SupportedLanguages;
    public string this[string phrase] => Translate(phrase);
    public string Format(string phrase, params object?[] args)
        => string.Format(culture, Lookup(phrase), args);
    public string Translate(string? phrase)
    {
        if (string.IsNullOrEmpty(phrase)) return phrase ?? "";
        var translated = Lookup(phrase);
        if (translated != phrase || Language == "en") return translated;
        // Older rule logs combine independent reasons; localize each reason at display time.
        if (phrase.Contains("; ")) return string.Join("; ", phrase.Split("; ").Select(Translate));
        foreach (var (key, pattern) in Templates.Value)
        {
            var match = pattern.Match(phrase);
            if (!match.Success) continue;
            return Regex.Replace(Lookup(key), @"\{(\d+)\}", part =>
            {
                var value = match.Groups["p" + part.Groups[1].Value].Value;
                return value == phrase ? value : Translate(value);
            });
        }
        return phrase;
    }
    private string Lookup(string phrase) => Catalogs.Value.TryGetValue(Language, out var catalog)
        && catalog.TryGetValue(phrase, out var value) ? value : phrase;
    public string GetAntiforgeryToken()
    {
        if (token is not null) return token;
        if (context.HttpContext is not { } http || http.Response.HasStarted) return "";
        return token = antiforgery.GetAndStoreTokens(http).RequestToken ?? "";
    }
    public static string? Normalize(string? value)
    {
        var code = value?.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return SupportedLanguages.Any(language => language.Code == code) ? code : null;
    }
    private static IReadOnlyDictionary<string, Dictionary<string, string>> LoadCatalogs()
    {
        var assembly = typeof(UiText).Assembly;
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in SupportedLanguages)
        {
            using var stream = assembly.GetManifestResourceStream($"DeyeSolar.i18n.{language.Code}.json");
            if (stream is not null) result[language.Code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        return result;
    }
}
