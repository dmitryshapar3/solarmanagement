using DeyeSolar.Web.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

internal static class ComponentTestLocalization
{
    public static void AddComponentLocalization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddAntiforgery();
        services.AddScoped<UiText>();
    }
}
