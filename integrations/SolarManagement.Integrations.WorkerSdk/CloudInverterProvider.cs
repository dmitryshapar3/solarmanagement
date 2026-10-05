using System.Globalization;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.WorkerSdk;

public abstract class CloudInverterProvider : IIntegrationWorkerProvider
{
    protected readonly WorkerConfiguration Configuration;
    protected readonly string BaseUrl;
    private readonly ICloudJsonTransport _transport;
    protected CloudInverterProvider(WorkerConfiguration configuration, ICloudJsonTransport transport, string defaultUrl, string requiredPath = "/")
    {
        Configuration = configuration;
        BaseUrl = CloudHttpClient.ValidateEndpoint(Text(configuration.Configuration.Values, "baseUrl") ?? defaultUrl,
            configuration.AllowedOrigins, requiredPath).TrimEnd('/');
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }
    protected string? Value(string key) => Text(Configuration.Configuration.Values, key);
    protected string? Secret(string key) => Configuration.Configuration.Secrets.GetValueOrDefault(key);
    protected string RequiredSecret(string key)
    {
        var value = Secret(key);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            throw new ArgumentException("Integration credential is missing or invalid.");
        return value;
    }
    protected Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct, Action<HttpResponseMessage>? observeResponse = null)
        => _transport.SendAsync(request, ct, observeResponse);
    protected HttpRequestMessage JsonPost(string path, object body) => new(HttpMethod.Post, BaseUrl + path)
        { Content = new StringContent(JsonSerializer.Serialize(body, IntegrationJson.Options), Encoding.UTF8, "application/json") };
    protected static string Identity(JsonElement parameters)
    {
        var value = Text(parameters, "remoteId");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Device identity is invalid.");
        return value;
    }
    protected static string? Text(JsonElement value, string key) => CloudJson.Text(value, key, allowNumber: true);
    protected static decimal? Number(JsonElement value, string key) => CloudJson.Number(value, key);
    protected static DateTimeOffset? EpochMilliseconds(JsonElement value, string key)
        => CloudJson.Timestamp(Number(value, key), milliseconds: true, requireWhole: true);
    protected DateTimeOffset? LocalTime(string? text, string format, string zoneField = "deviceTimeZone")
    {
        if (string.IsNullOrWhiteSpace(Value(zoneField)) || !DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) return null;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Value(zoneField)!);
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) return null;
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
    protected static ProviderMeasurement Difference(ProviderMeasurement positive, ProviderMeasurement negative)
    {
        if (positive.Quality == ProviderMeasurementQuality.Missing || negative.Quality == ProviderMeasurementQuality.Missing) return Missing(positive.ObservedAt);
        if (positive.Quality != ProviderMeasurementQuality.Good || negative.Quality != ProviderMeasurementQuality.Good) return Invalid(positive.ObservedAt);
        var value = positive.Value!.Value - negative.Value!.Value;
        return value is < int.MinValue or > int.MaxValue ? Invalid(positive.ObservedAt) : new(value, positive.ObservedAt, ProviderMeasurementQuality.Good);
    }
    protected static ProviderMeasurement Missing(DateTimeOffset? time = null) => new(null, time, ProviderMeasurementQuality.Missing);
    protected static ProviderMeasurement Invalid(DateTimeOffset? time = null) => new(null, time, ProviderMeasurementQuality.Invalid);
    protected static ProviderMeasurement Measure(JsonElement value, string key, DateTimeOffset? time, decimal minimum, decimal maximum, decimal factor = 1)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null) return Missing(time);
        var number = Number(value, key);
        if (number is null) return Invalid(time);
        try
        {
            var normalized = number.Value * factor;
            return normalized < minimum || normalized > maximum ? Invalid(time) : new(normalized, time, ProviderMeasurementQuality.Good);
        }
        catch (OverflowException) { return Invalid(time); }
    }
    protected static ProviderMeasurement Power(JsonElement value, string key, string unitKey, DateTimeOffset? time, bool nonnegative = false, decimal sign = 1)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null) return Missing(time);
        var factor = Text(value, unitKey) switch { "W" => 1m, "kW" => 1000m, "MW" => 1000000m, _ => 0m };
        return factor == 0 || sign == 0 ? Invalid(time) : Measure(value, key, time, nonnegative ? 0 : int.MinValue, int.MaxValue, factor * sign);
    }
    protected decimal Sign(string field, string positive, string negative) => Value(field) switch
        { var text when text == positive => 1, var text when text == negative => -1, _ => 0 };
    protected static (DateTimeOffset Start, DateTimeOffset End) HistoryInterval(JsonElement parameters)
    {
        if (parameters.TryGetProperty("continuationToken", out var continuation) && continuation.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("This history source does not use continuation tokens.");
        var start = parameters.GetProperty("start").GetDateTimeOffset().ToUniversalTime();
        var end = parameters.GetProperty("end").GetDateTimeOffset().ToUniversalTime();
        if (end <= start || end - start > TimeSpan.FromDays(7)) throw new ArgumentException("History interval must be positive and at most seven days.");
        return (start, end);
    }
    protected static JsonElement Connection(bool success, string code, string message) => IntegrationJson.Element(new IntegrationTestResult(success, code, message));
    protected static JsonElement UnsupportedHistory(string remoteId) => IntegrationJson.Element(new ProviderGridHistory(remoteId, [], null, false));
    public virtual int MinimumOperationTimeoutSeconds => 0;
    public abstract Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct);
    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
