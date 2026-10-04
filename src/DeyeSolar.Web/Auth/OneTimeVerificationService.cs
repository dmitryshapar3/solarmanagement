using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeyeSolar.Web.Auth;

public sealed record VerificationStartRequest(string Channel, string Destination, string Purpose);
public sealed record VerificationStartResponse(string VerificationId, DateTimeOffset ExpiresAt, int RetryAfterSeconds);
public sealed record VerifiedIdentity(string Channel, string Destination);

// Deliberately process-local: restarting invalidates pending codes. No codes or destinations are logged.
public sealed class OneTimeVerificationService(IIdentityVerificationDelivery delivery, AuthProviderOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _destinations = new();
    private readonly object _sync = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public static bool TryNormalize(string channel, string destination, out string normalized)
    {
        normalized = (destination ?? "").Trim();
        if (channel == "phone") return Regex.IsMatch(normalized, "^\\+[1-9][0-9]{7,14}$");
        if (channel != "email" || normalized.Length is < 3 or > 254 || normalized.Contains('\r') || normalized.Contains('\n')
            || !MailAddress.TryCreate(normalized, out var address) || address.Address != normalized || !normalized.Contains('@')) return false;
        normalized = normalized.ToLowerInvariant();
        return true;
    }

    public async Task<VerificationStartResponse> StartAsync(VerificationStartRequest request, string? linkingUserId, CancellationToken ct)
    {
        if (!TryNormalize(request.Channel, request.Destination, out var destination)
            || request.Purpose is not ("register" or "login" or "link" or "security") || request.Purpose is ("link" or "security") && linkingUserId is null)
            throw new ArgumentException("Enter a valid email address or phone number, including the country code.");
        if (request.Channel == "email" && !options.EmailEnabled || request.Channel == "phone" && !options.PhoneEnabled
            || request.Purpose == "register" && !options.RegistrationEnabled) throw new VerificationDeliveryException();
        var now = clock.GetUtcNow();
        var destinationKey = Digest($"destination:{request.Channel}:{destination}");
        lock (_sync)
        {
            foreach (var expired in _challenges.Where(c => c.Value.ExpiresAt <= now).Select(c => c.Key).ToArray()) _challenges.TryRemove(expired, out _);
            foreach (var stale in _destinations.Where(d => d.Value.Count == 0 || d.Value.Last() < now.AddHours(-1)).Select(d => d.Key).ToArray()) _destinations.Remove(stale);
            if (_challenges.Count >= 10000 || _destinations.Count >= 10000) throw new VerificationRateLimitException();
            if (!_destinations.TryGetValue(destinationKey, out var sends)) _destinations[destinationKey] = sends = new();
            while (sends.TryPeek(out var sent) && sent <= now.AddHours(-1)) sends.Dequeue();
            if (sends.Count >= 5 || sends.Count > 0 && sends.Last() > now.AddSeconds(-60)) throw new VerificationRateLimitException();
            sends.Enqueue(now);
            // A resend replaces earlier challenges for this destination, preventing cross-purpose reuse.
            foreach (var old in _challenges.Where(c => c.Value.DestinationKey == destinationKey).Select(c => c.Key).ToArray()) _challenges.TryRemove(old, out _);
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6", CultureInfo.InvariantCulture);
        var challenge = new Challenge(request.Channel, destination, destinationKey, request.Purpose, linkingUserId,
            now.AddMinutes(10), Digest($"code:{id}:{code}"));
        _challenges[id] = challenge;
        try
        {
            if (request.Channel == "email") await delivery.SendEmailAsync(destination, code, ct);
            else await delivery.SendPhoneAsync(destination, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _challenges.TryRemove(id, out _);
            throw;
        }
        catch
        {
            _challenges.TryRemove(id, out _);
            throw new VerificationDeliveryException();
        }
        return new(id, challenge.ExpiresAt, 60);
    }

    public async Task<VerifiedIdentity?> VerifyAsync(string id, string code, string purpose, string? linkingUserId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || code is null || !Regex.IsMatch(code, "^[0-9]{6}$")
            || !_challenges.TryGetValue(id, out var challenge)) return null;
        await challenge.Gate.WaitAsync(ct);
        try
        {
            if (challenge.ExpiresAt <= clock.GetUtcNow() || challenge.Attempts >= 5 || challenge.Purpose != purpose
                || challenge.LinkingUserId != linkingUserId || !_challenges.TryGetValue(id, out var current) || current != challenge) return null;
            challenge.Attempts++;
            var valid = challenge.Channel == "email"
                ? CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.CodeHash), Convert.FromHexString(Digest($"code:{id}:{code}")))
                : await delivery.CheckPhoneAsync(challenge.Destination, code, ct);
            if (!valid || challenge.ExpiresAt <= clock.GetUtcNow()) return null;
            if (!_challenges.TryRemove(new KeyValuePair<string, Challenge>(id, challenge))) return null;
            return new(challenge.Channel, challenge.Destination);
        }
        finally { challenge.Gate.Release(); }
    }

    private string Digest(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
    private sealed class Challenge(string channel, string destination, string destinationKey, string purpose, string? linkingUserId, DateTimeOffset expiresAt, string codeHash)
    {
        public string Channel { get; } = channel;
        public string Destination { get; } = destination;
        public string DestinationKey { get; } = destinationKey;
        public string Purpose { get; } = purpose;
        public string? LinkingUserId { get; } = linkingUserId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string CodeHash { get; } = codeHash;
        public int Attempts { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

public sealed class VerificationRateLimitException : Exception
{
    public VerificationRateLimitException() : base("Please wait before requesting another verification code.") { }
}
