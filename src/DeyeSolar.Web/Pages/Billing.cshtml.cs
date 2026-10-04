using System.Security.Claims;
using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeyeSolar.Web.Pages;

[Authorize]
public sealed class BillingModel(IBillingAccessReader billing) : PageModel
{
    public BillingAccess? Access { get; private set; }
    public async Task OnGetAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Access = await billing.ReadAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
    }
}
