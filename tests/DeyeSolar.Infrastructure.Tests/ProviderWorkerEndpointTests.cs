using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class ProviderWorkerEndpointTests
{
    [Theory]
    [InlineData("https://foreign.example/v1.0")]
    [InlineData("http://eu1-developer.deyecloud.com/v1.0")]
    [InlineData("https://eu1-developer.deyecloud.com.evil.example/v1.0")]
    [InlineData("https://eu1-developer.deyecloud.com:444/v1.0")]
    [InlineData("https://user@eu1-developer.deyecloud.com/v1.0")]
    [InlineData("https://eu1-developer.deyecloud.com/v1.0?token=fixture")]
    [InlineData("https://eu1-developer.deyecloud.com/v1.0#fragment")]
    [InlineData("https://eu1-developer.deyecloud.com/other-api")]
    public void DeyeWorkerRejectsUnauthorizedOrWrongApiEndpointBeforeCreatingSession(string baseUrl)
        => Assert.Throws<ArgumentException>(() => new DeyeProvider(Config("deye.cloud", new { baseUrl },
            ["https://eu1-developer.deyecloud.com", "https://us1-developer.deyecloud.com"])));
    [Theory]
    [InlineData("http://shelly-1-eu.shelly.cloud")]
    [InlineData("https://shelly.cloud.evil.example")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://shelly-1-eu.shelly.cloud/other")]
    [InlineData("https://shelly-1-eu.shelly.cloud?key=fixture")]
    [InlineData("https://user@shelly-1-eu.shelly.cloud")]
    public void ShellyWorkerRejectsUnauthorizedEndpointBeforeCreatingSession(string serverUri)
        => Assert.Throws<ArgumentException>(() => new SolarManagement.Providers.ShellyCloud.ShellyCloudProvider(Config("shelly.cloud", new { serverUri }, ["https://*.shelly.cloud"])));
    [Theory]
    [InlineData("https://eu1-developer.deyecloud.com/v1.0")]
    [InlineData("https://us1-developer.deyecloud.com/v1.0/")]
    public async Task DeyeWorkerAcceptsApprovedRegionWithoutCallingCloudDuringConstruction(string baseUrl)
    {
        await using var provider = new DeyeProvider(Config("deye.cloud", new { baseUrl },
            ["https://eu1-developer.deyecloud.com", "https://us1-developer.deyecloud.com"]));
    }
    [Theory]
    [InlineData("https://shelly-1-eu.shelly.cloud")]
    [InlineData("shelly-1-eu.shelly.cloud")]
    public async Task ShellyWorkerAcceptsApprovedRegionAndLegacyMissingSchemeWithoutCallingCloud(string serverUri)
    {
        await using var provider = new SolarManagement.Providers.ShellyCloud.ShellyCloudProvider(Config("shelly.cloud", new { serverUri }, ["https://*.shelly.cloud"]));
    }
    [Theory]
    [InlineData(30000, 120)]
    [InlineData(120000, 300)]
    public async Task ShellyPreservesLargeLegacyIntervalsWithinBoundedExecutionDeadline(int interval, int deadline)
    {
        await using var provider = new SolarManagement.Providers.ShellyCloud.ShellyCloudProvider(Config("shelly.cloud", new
        {
            serverUri = "https://shelly-1-eu.shelly.cloud",
            requestIntervalMilliseconds = interval
        }, ["https://*.shelly.cloud"]));
        Assert.Equal(deadline, provider.MinimumOperationTimeoutSeconds);
    }
    [Theory]
    [InlineData(120001, 300)]
    [InlineData(30000, 119)]
    [InlineData(int.MaxValue, 300)]
    public void ShellyRejectsIntervalsBeyondRepresentableOperatorDeadline(int interval, int deadline)
        => Assert.Throws<ArgumentException>(() => new SolarManagement.Providers.ShellyCloud.ShellyCloudProvider(Config("shelly.cloud", new
        {
            serverUri = "https://shelly-1-eu.shelly.cloud",
            requestIntervalMilliseconds = interval
        }, ["https://*.shelly.cloud"]) with
        { MaximumOperationTimeoutSeconds = deadline }));
    private static WorkerConfiguration Config(string provider, object values, string[] origins) => new(provider, Guid.NewGuid(), 1, 1,
        new(IntegrationJson.Element(values), new Dictionary<string, string> { ["appSecret"] = "fixture-private", ["password"] = "fixture-private", ["authKey"] = "fixture-private" }), origins);
}
