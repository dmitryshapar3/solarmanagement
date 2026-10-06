namespace DeyeSolar.Web.Components.Ui;

public static class WebAppearance
{
    public const string CookieName = "deyeSolarAppearance";
    public static string CookieTheme(HttpContext? context) => context?.Request.Cookies[CookieName] switch
    {
        "dark" => "dark", "system" => "system", _ => "light"
    };
}
