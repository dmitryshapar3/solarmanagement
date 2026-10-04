using System.Security.Claims;
using DeyeSolar.Web.Api;

namespace DeyeSolar.Web.Localization;

public sealed record UserLanguageRequest(string Language);
public static class LanguageEndpoints
{
    public static void MapUserLanguage(this WebApplication app)
    {
        // The API is bearer-only; the Razor form has its own antiforgery-validated cookie route.
        var api = app.MapGroup("/api/account/language").RequireAuthorization(ApiAuthorization.BearerUser);
        api.MapGet("", async (HttpContext context, UserLanguageService preferences) =>
            Results.Ok(new { language = await preferences.GetAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!) }));
        api.MapPut("", async Task<IResult> (UserLanguageRequest request, HttpContext context, UserLanguageService preferences, UiText text) =>
        {
            if (!await preferences.SetAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Language))
                return Results.BadRequest(new ApiError(text["Choose a supported language."]));
            return Results.Ok(new { language = request.Language });
        });
    }
}
