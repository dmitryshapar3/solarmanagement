using System.Net;
using SolarManagement.Integrations.WorkerSdk;

namespace SolarManagement.Integrations.Tests;

public sealed class CloudTransportTests
{
    [Theory]
    [InlineData("http://eu1-developer.deyecloud.com/v1.0/account/token", false)]
    [InlineData("https://foreign.example/v1.0/account/token", false)]
    [InlineData("https://eu1-developer.deyecloud.com.evil.example/v1.0/account/token", false)]
    [InlineData("https://eu1-developer.deyecloud.com:444/v1.0/account/token", false)]
    [InlineData("https://user@eu1-developer.deyecloud.com/v1.0/account/token", false)]
    [InlineData("https://eu1-developer.deyecloud.com/v1.0/account/token", true)]
    [InlineData("https://us1-developer.deyecloud.com/v1.0/account/token", true)]
    [InlineData("https://shelly.cloud.evil.example/device/all_status", false)]
    [InlineData("https://shelly-1-eu.shelly.cloud/device/all_status", true)]
    public async Task ApprovedCloudOriginsAreCheckedBeforeSendingCredentials(string address, bool permitted)
    {
        var transport = new FixtureTransport(() => new StringContent("{}"));
        using var client = new HttpClient(new CloudHttpClient.OriginGuard(
            ["https://eu1-developer.deyecloud.com", "https://us1-developer.deyecloud.com", "https://*.shelly.cloud"], transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent("fixture-private-credential") };
        if (permitted)
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, transport.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
            Assert.Equal(0, transport.Calls);
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedCloudBodiesAreRejectedWithAndWithoutDeclaredLength(bool declaredLength)
    {
        var body = new byte[5 * 1024 * 1024];
        var transport = new FixtureTransport(() => declaredLength ? new ByteArrayContent(body) : new StreamContent(new NonSeekableStream(body)));
        using var client = new HttpClient(new CloudHttpClient.OriginGuard(["https://cloud.example"], transport));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("https://cloud.example/telemetry"));
        Assert.Equal(1, transport.Calls);
    }
    [Fact]
    public async Task BoundedCloudResponseRetainsPayloadAndContentType()
    {
        var transport = new FixtureTransport(() => new StringContent("{\"value\":42}", System.Text.Encoding.UTF8, "application/json"));
        using var client = new HttpClient(new CloudHttpClient.OriginGuard(["https://cloud.example"], transport));
        using var response = await client.GetAsync("https://cloud.example/telemetry");
        Assert.Equal("{\"value\":42}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
    }
    private sealed class FixtureTransport(Func<HttpContent> content) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(), RequestMessage = request });
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
