using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Tenancy;

namespace DeyeSolar.Web.Operations;

public static class ApplicationPipeline
{
    public static void UseSolarApplication(this WebApplication app, DeploymentConfiguration deployment)
    {
        if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
        // Probes never resolve account, billing, language or installation state, including with stale cookies.
        app.UseApplicationHealth();
        if (!string.IsNullOrWhiteSpace(deployment.TrustedProxyAddresses)) app.UseForwardedHeaders();
        app.UseMiddleware<ResponseSecurityHeaders>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAccountIdentityOrigin();
        app.UseAuthentication();
        app.UseMiddleware<LanguageMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<BillingAccessMiddleware>();
        app.UseMiddleware<InstallationBindingMiddleware>();
        app.UseMiddleware<InstallationPermissionMiddleware>();
        app.MapAppleBilling();
        app.MapMobileApi();
        app.MapDynamicIntegrations();
        app.MapAccountIdentityApi();
        app.MapAccountSecurityApi();
        app.MapUserLanguage();
        app.MapGoogleIdentity();
        app.MapExportSalesApi();
        app.MapIntegrationManagement();
        app.MapBlazorHub();
        app.MapRazorPages();
        app.MapFallbackToPage("/_Host");
    }
}
