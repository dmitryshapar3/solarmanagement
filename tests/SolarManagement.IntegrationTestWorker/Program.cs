using System.Text.Json;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;

await IntegrationWorkerHost.RunAsync("test.provider", ["test", "discover", "inverter.read", "socket.read", "socket.set", "socket.result", "oauth.begin", "oauth.complete"],
    config => new TestProvider(config));

internal sealed class TestProvider(WorkerConfiguration configuration) : IIntegrationWorkerProvider
{
    public int MinimumOperationTimeoutSeconds => configuration.Configuration.Values.TryGetProperty("minimumOperationTimeoutSeconds", out var value) ? value.GetInt32() : 0;
    private int _reads;
    private bool _isOn;
    private bool _pendingState;
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        var account = configuration.Configuration.Values.GetProperty("account").GetString()!;
        if (method == "oauth.begin")
        {
            var request = parameters.Deserialize<IntegrationOAuthBeginRequest>(IntegrationJson.Options)!;
            var origin = configuration.Configuration.Values.TryGetProperty("oauthForeignOrigin", out var foreign) && foreign.GetBoolean()
                ? "https://foreign.example.test" : "https://oauth.example.test";
            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = "fixture-client",
                ["state"] = request.State,
                ["redirect_uri"] = request.RedirectUri,
                ["code_challenge"] = request.CodeChallenge,
                ["code_challenge_method"] = request.CodeChallengeMethod
            };
            return IntegrationJson.Element(new IntegrationOAuthBeginResult(origin + "/authorize?"
                + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))));
        }
        if (method == "oauth.complete")
        {
            var request = parameters.Deserialize<IntegrationOAuthCompleteRequest>(IntegrationJson.Options)!;
            var challenge = IntegrationOAuthProtocol.Challenge(request.CodeVerifier);
            var foreign = request.Code == "foreign-code:" + challenge;
            if (request.Code != "fixture-code:" + challenge && !foreign)
                return IntegrationJson.Element(new IntegrationOAuthCompleteResult(false, IntegrationJson.Element(new { }), new Dictionary<string, string>()));
            var values = configuration.Configuration.Values.TryGetProperty("oauthPublicSecret", out var publicSecret) && publicSecret.GetBoolean()
                ? IntegrationJson.Element(new { apiKey = "must-never-escape" }) : IntegrationJson.Element(new { account });
            var key = configuration.Configuration.Values.TryGetProperty("oauthExtraSecret", out var extraSecret) && extraSecret.GetBoolean()
                ? "undeclaredToken" : "apiKey";
            return IntegrationJson.Element(new IntegrationOAuthCompleteResult(true, values,
                new Dictionary<string, string> { [key] = "fixture-oauth-token:" + account }, foreign ? "foreign-account" : account));
        }
        if (method == "test") return IntegrationJson.Element(new IntegrationTestResult(
            Environment.GetEnvironmentVariable("SOLAR_WORKER_SECRET_TEST") is null, "connected", "Connected", account));
        if (method == "discover") return IntegrationJson.Element(new[] { new IntegrationDiscoveredDevice("same-id", "0", "socket", account, account,
            IntegrationJson.Element(new { capabilities = new { canSwitch = true, canMeasurePower = true } })) });
        if (method == "socket.read") return IntegrationJson.Element(new ProviderSocketTelemetry(parameters.GetProperty("remoteId").GetString()!, "0",
            _isOn, true, _isOn ? 100 : 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        if (method == "socket.set")
        {
            _pendingState = parameters.TryGetProperty("isOn", out var desired) && desired.GetBoolean();
            var lower = parameters.TryGetProperty("lowercaseStatus", out var lowerStatus) && lowerStatus.GetBoolean();
            var mismatch = parameters.TryGetProperty("mismatchId", out var mismatchId) && mismatchId.GetBoolean();
            return IntegrationJson.Element(new ProviderSocketCommandResult(mismatch ? Guid.NewGuid().ToString("D") : parameters.GetProperty("commandId").GetString()!, lower ? "pending" : "Pending", "test-operation"));
        }
        if (method == "socket.result")
        {
            _isOn = _pendingState;
            return IntegrationJson.Element(new ProviderSocketCommandResult(parameters.GetProperty("commandId").GetString()!, "Acknowledged"));
        }
        var remoteId = parameters.GetProperty("remoteId").GetString()!;
        if (remoteId == "hang") await Task.Delay(Timeout.Infinite, ct);
        if (remoteId == "barrier")
        {
            await File.WriteAllTextAsync(parameters.GetProperty("startedPath").GetString()!, "started", ct);
            while (!File.Exists(parameters.GetProperty("releasePath").GetString()!)) await Task.Delay(10, ct);
        }
        if (remoteId == "crash") Environment.Exit(7);
        if (remoteId == "delay") await Task.Delay(300, ct);
        if (remoteId == "negotiated-delay") await Task.Delay(1500, ct);
        var at = DateTimeOffset.UtcNow;
        var measurement = new ProviderMeasurement(++_reads, at, ProviderMeasurementQuality.Good);
        return IntegrationJson.Element(new ProviderInverterTelemetry(remoteId, at, "PvDc", measurement,
            measurement, measurement, measurement, measurement, measurement, measurement,
            new(account == "one" ? 1100 : 2200, at, ProviderMeasurementQuality.Good)));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
