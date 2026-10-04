using System.Net;

namespace SolarManagement.Integrations.Contracts;

/// <summary>Safe failure categories shared across the isolated worker boundary; never contains vendor text.</summary>
public enum IntegrationFailureKind { ProviderRejected, Authentication, RateLimited, Transient, InvalidResponse, Configuration }

public sealed class IntegrationOperationException(IntegrationFailureKind kind)
    : InvalidOperationException(kind == IntegrationFailureKind.ProviderRejected
        ? "The integration rejected the operation." : "The integration operation is unavailable.")
{
    public IntegrationFailureKind Kind { get; } = kind;
    public bool IsTransient => Kind is IntegrationFailureKind.Transient or IntegrationFailureKind.RateLimited;
}

public static class IntegrationFailureClassifier
{
    public static IntegrationFailureKind Classify(Exception error) => error switch
    {
        IntegrationOperationException typed => typed.Kind,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => IntegrationFailureKind.Authentication,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => IntegrationFailureKind.RateLimited,
        HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => IntegrationFailureKind.Transient,
        TimeoutException or OperationCanceledException => IntegrationFailureKind.Transient,
        InvalidDataException or System.Text.Json.JsonException => IntegrationFailureKind.InvalidResponse,
        ArgumentException => IntegrationFailureKind.Configuration,
        _ => IntegrationFailureKind.ProviderRejected
    };
}
