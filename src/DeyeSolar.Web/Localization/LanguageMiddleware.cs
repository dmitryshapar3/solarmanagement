using System.Globalization;
using System.Security.Claims;

namespace DeyeSolar.Web.Localization;

public sealed class LanguageMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserLanguageService preferences)
    {
        // Native clients send their locally selected language on every request, even before sign-in.
        var language = ResolveHeader(context.Request.Headers.AcceptLanguage.ToString());
        if (!context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
                language = await preferences.GetAsync(userId) ?? language;
            else language = UiText.Normalize(context.Request.Cookies[UserLanguageService.CookieName]) ?? language;
        }
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        context.Response.Headers.ContentLanguage = language;
        await next(context);
    }
    public static string ResolveHeader(string value)
    {
        // Respect quality weights, regional variants and q=0 exclusions.
        var candidates = value.Split(',').Select(part =>
        {
            var pieces = part.Trim().Split(';');
            var quality = 1d;
            foreach (var option in pieces.Skip(1))
                if (option.Trim().StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && !double.TryParse(option.Trim()[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out quality)) quality = 0;
            return (Language: UiText.Normalize(pieces[0]), Quality: quality);
        }).Where(candidate => candidate.Language is not null && candidate.Quality is > 0 and <= 1)
            .OrderByDescending(candidate => candidate.Quality);
        return candidates.FirstOrDefault().Language ?? "en";
    }
}
