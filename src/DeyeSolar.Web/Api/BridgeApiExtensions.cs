using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Api;

public static class BridgeApiExtensions
{
    public static IEndpointRouteBuilder MapBridgeApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/bridge/sync",
            async Task<Results<Ok<BridgeSyncResponse>, UnauthorizedHttpResult, BadRequest<string>>> (
                HttpContext httpContext,
                BridgeSyncRequest request,
                IOptionsMonitor<SocketBackendOptions> backendOptions,
                BridgeStateService bridgeState,
                CancellationToken ct) =>
            {
                if (!IsAuthorized(httpContext.Request, backendOptions.CurrentValue))
                    return TypedResults.Unauthorized();

                var configuredBridgeId = backendOptions.CurrentValue.BridgeId;
                if (string.IsNullOrWhiteSpace(request.BridgeId))
                    return TypedResults.BadRequest("BridgeId is required.");

                if (!string.Equals(request.BridgeId, configuredBridgeId, StringComparison.Ordinal))
                {
                    return TypedResults.BadRequest(
                        $"BridgeId '{request.BridgeId}' does not match configured bridge '{configuredBridgeId}'.");
                }

                var response = await bridgeState.SyncAsync(request, ct);
                return TypedResults.Ok(response);
            });

        return endpoints;
    }

    private static bool IsAuthorized(HttpRequest request, SocketBackendOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BearerToken))
            return false;

        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var providedToken = header["Bearer ".Length..].Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(options.BearerToken);
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);

        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
