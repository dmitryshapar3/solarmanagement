using DeyeSolar.Web.Api;
using DeyeSolar.Web.Operations;
using Microsoft.AspNetCore.Antiforgery;
namespace DeyeSolar.Web.Auth;
public static class AccountSecurityEndpoints
{
    public static void MapAccountSecurityApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/auth/security").RequireAuthorization(ApiAuthorization.AuthenticatedUser).RequireRateLimiting("identity-auth");
        api.MapGet("/permissions", async (HttpContext context, Data.InstallationMembershipService memberships) =>
        {
            var membership = await memberships.ResolveAsync(context.User, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { role = membership?.Role, permissions = membership is null ? Array.Empty<string>()
                : Enum.GetValues<InstallationPermission>().Where(p => InstallationAccessAuthorizer.Allows(membership.Role, p)).Select(p => p.ToString()).ToArray() });
        });
        api.MapPost("/verification/start", (VerificationStartRequest request, AccountSecurityService security, HttpContext context, IAntiforgery csrf, CancellationToken ct)
            => WriteAsync(context, csrf, () => security.StartProofAsync(context.User, request.Channel, request.Destination, ct)));
        api.MapPost("/password", (AccountPasswordChange request, AccountSecurityService security, HttpContext context, IAntiforgery csrf, CancellationToken ct)
            => WriteAsync(context, csrf, async () => { await security.ChangePasswordAsync(context.User, request, ct); return new { signedOut = true }; }));
        api.MapPost("/revoke-all", (AccountSecurityProof proof, AccountSecurityService security, HttpContext context, IAntiforgery csrf, CancellationToken ct)
            => WriteAsync(context, csrf, async () => { await security.RevokeAllAsync(context.User, proof, ct); return new { signedOut = true }; }));
        api.MapPost("/export", (AccountSecurityProof proof, AccountSecurityService security, HttpContext context, IAntiforgery csrf, CancellationToken ct)
            => WriteAsync(context, csrf, () => security.ExportAsync(context.User, proof, ct)));
        api.MapPost("/delete", (AccountSecurityProof proof, AccountSecurityService security, HttpContext context, IAntiforgery csrf, CancellationToken ct)
            => WriteAsync(context, csrf, async () => { await security.DeleteAsync(context.User, proof, ct); return new { deleted = true }; }));
    }
    private static async Task<IResult> WriteAsync<T>(HttpContext context, IAntiforgery csrf, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        await AuthenticatedMutationPolicy.EnsureAsync(context, csrf);
        try { return Results.Ok(await action()); }
        catch (Exception error) when (error is AccountSecurityException or VerificationRateLimitException) { return ApiProblems.Describe(error); }
    }
}
