using System.Reflection;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;

// This executable is exclusively a test launcher. Production provider entry points
// construct CloudHttpClient and never accept this fixture transport or its address.
if (args.Length != 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var fixture)
    || fixture.Scheme != "http" || !fixture.IsLoopback)
    throw new ArgumentException("A provider suffix and a loopback fixture URL are required.");
var suffixes = new[] { "ShellyCloud", "SolisCloud", "SungrowCloud", "HuaweiFusionSolar", "GrowattCloud",
    "TuyaCloud", "EWeLinkCloud", "AqaraCloud", "NetatmoControl" };
if (!suffixes.Contains(args[0], StringComparer.Ordinal)) throw new ArgumentException("Unknown fixture provider.");
var assembly = Assembly.Load("SolarManagement.Providers." + args[0]);
var providerType = assembly.GetType("SolarManagement.Providers." + args[0] + "." + args[0] + "Provider", throwOnError: true)!;
var providerId = (string)providerType.GetField("ProviderId", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;
var operations = (IReadOnlyList<string>)providerType.GetProperty("Operations", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
await IntegrationWorkerHost.RunAsync(providerId, operations, configuration =>
{
    var http = new HttpClient(new FixtureTransport(fixture)) { Timeout = TimeSpan.FromSeconds(15) };
    return (IIntegrationWorkerProvider)Activator.CreateInstance(providerType, configuration, http)!;
});

internal sealed class FixtureTransport(Uri fixture) : DelegatingHandler(new SocketsHttpHandler
{
    AllowAutoRedirect = false, UseCookies = false, UseProxy = false
})
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var original = request.RequestUri ?? throw new InvalidDataException("Missing provider URL.");
        request.Headers.Add("X-Fixture-Original-Url", original.AbsoluteUri);
        request.RequestUri = new Uri(fixture, original.PathAndQuery);
        return base.SendAsync(request, ct);
    }
}
