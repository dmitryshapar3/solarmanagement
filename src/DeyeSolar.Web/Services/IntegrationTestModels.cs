namespace DeyeSolar.Web.Services;

public sealed record SolarTestSettings(double Latitude, double Longitude);
public sealed record IntegrationTestRequest(
    SolarTestSettings? SolarEstimate = null);
public sealed record IntegrationTestResult(string Kind, bool Success, string Code, string Message, DateTimeOffset CheckedAt);

public interface IIntegrationTestService
{
    Task<IntegrationTestResult> TestAsync(string kind, IntegrationTestRequest request, CancellationToken ct);
}
