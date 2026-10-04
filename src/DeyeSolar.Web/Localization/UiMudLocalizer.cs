using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace DeyeSolar.Web.Localization;

/// <summary>Routes the installed MudBlazor version's own labels through the same account language catalog.</summary>
public sealed class UiMudLocalizer(UiText text) : MudLocalizer
{
    private static readonly ResourceManager EnglishResources = new(
        "MudBlazor.Resources.LanguageResource", typeof(MudLocalizer).Assembly);

    public override LocalizedString this[string key]
    {
        get
        {
            var phrase = EnglishResources.GetString(key, CultureInfo.GetCultureInfo("en"));
            return new(key, phrase is null ? key : text[phrase], resourceNotFound: phrase is null);
        }
    }

    public override LocalizedString this[string key, params object[] arguments]
    {
        get
        {
            var phrase = EnglishResources.GetString(key, CultureInfo.GetCultureInfo("en"));
            return new(key, phrase is null ? key : text.Format(phrase, arguments), resourceNotFound: phrase is null);
        }
    }
}
