using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

/// <summary>Authorization results remain encrypted drafts until the configuration transaction consumes them.</summary>
public sealed class IntegrationOAuthService(DbContextOptions<DeyeSolarDbContext> database,
    IIntegrationProviderCatalog catalog, IIntegrationSetupExecutor executor, IntegrationSecretStore secrets,
    IntegrationOAuthOptions options, TimeProvider clock, MobileSessionStore sessions, IntegrationSetupGate gate,
    IOptions<IntegrationRuntimeOptions> runtimeOptions)
{
    private sealed record Envelope(IntegrationDraftConfiguration Draft, string Verifier, string ReturnNonce,
        string? MobileToken, string[] SecretKeys, string? BindingFingerprint = null,
        Dictionary<string, JsonElement>? PublicResult = null, string? AccountIdentity = null);

    private static IntegrationRequestException Unavailable() => new("authorization_unavailable", "This authorization is unavailable. Start again using the current settings.", 409);
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string User(ClaimsPrincipal actor) => actor.Identity?.IsAuthenticated == true
        ? actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw Unavailable() : throw Unavailable();
    private Envelope Open(IntegrationOAuthFlowEntity flow) => JsonSerializer.Deserialize<Envelope>(
        secrets.UnprotectOAuth(flow.InstallationId, flow.Id, flow.Ciphertext), IntegrationJson.Options) ?? throw Unavailable();
    private void Seal(IntegrationOAuthFlowEntity flow, Envelope envelope) => flow.Ciphertext = secrets.ProtectOAuth(
        flow.InstallationId, flow.Id, JsonSerializer.Serialize(envelope, IntegrationJson.Options));
    private static string Binding(IntegrationDraftConfiguration draft, IReadOnlyCollection<string> credentialKeys)
    {
        var values = draft.Values.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var credentials = draft.Secrets.Where(p => !credentialKeys.Contains(p.Key, StringComparer.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Hash(JsonSerializer.Serialize(new { values, credentials }, IntegrationJson.Options));
    }
    private static bool Matches(IntegrationOAuthFlowEntity flow, IntegrationInstanceEntity instance)
        => flow.InstanceId == instance.Id && flow.InstallationId == instance.InstallationId && flow.Revision == instance.Revision
            && flow.Generation == instance.Generation && flow.PackageVersion == instance.PackageVersion
            && flow.PackageDigest == instance.PackageDigest && flow.DescriptorDigest == instance.DescriptorDigest;
    private async Task<bool> SessionValidAsync(DeyeSolarDbContext db, IntegrationOAuthFlowEntity flow, Envelope envelope,
        ClaimsPrincipal? callbackActor, CancellationToken ct, bool requireOwnerActor = false)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == flow.UserId, ct);
        if (user is null || user.SecurityStamp != flow.SecurityStamp || user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()
            || !await db.InstallationMemberships.AnyAsync(m => m.UserId == flow.UserId && m.InstallationId == flow.InstallationId
                && m.Installation.IsEnabled && (m.Role == "Owner" || m.Role == "IntegrationManager"), ct)) return false;
        if (requireOwnerActor && (callbackActor?.Identity?.IsAuthenticated != true
            || callbackActor.FindFirstValue(ClaimTypes.NameIdentifier) != flow.UserId
            || callbackActor.FindFirstValue("AspNet.Identity.SecurityStamp") is { } actorStamp && actorStamp != user.SecurityStamp)) return false;
        if (flow.Client == "mobile")
        {
            var session = envelope.MobileToken is null ? null : sessions.Find(envelope.MobileToken);
            return session is not null && session.UserId == flow.UserId && session.SecurityStamp == user.SecurityStamp
                && session.InstallationId == flow.InstallationId;
        }
        return callbackActor is null || callbackActor.Identity?.IsAuthenticated == true
            && callbackActor.FindFirstValue(ClaimTypes.NameIdentifier) == flow.UserId
            && (callbackActor.FindFirstValue("AspNet.Identity.SecurityStamp") is not { } stamp || stamp == user.SecurityStamp);
    }
    private IntegrationOAuthStatusDto Status(IntegrationOAuthFlowEntity flow)
    {
        var status = flow.ExpiresAt <= clock.GetUtcNow() && flow.Status is "pending" or "exchanging" or "ready" ? "expired" : flow.Status;
        if (status != "ready") return new(flow.Id, status, flow.ExpiresAt, new Dictionary<string, JsonElement>(), new Dictionary<string, bool>(), flow.Code);
        var envelope = Open(flow);
        return new(flow.Id, status, flow.ExpiresAt, envelope.PublicResult ?? [],
            envelope.SecretKeys.Where(envelope.Draft.Secrets.ContainsKey).ToDictionary(k => k, _ => true, StringComparer.Ordinal));
    }

    public async Task<IntegrationOAuthStartDto> StartAsync(IntegrationInstanceEntity instance, IntegrationProviderDescriptor descriptor,
        IntegrationDraftConfiguration draft, ClaimsPrincipal actor, string client, string? mobileToken, CancellationToken ct)
    {
        options.Validate();
        if (client is not ("web" or "mobile") || descriptor.OAuthDefinition is null) throw Unavailable();
        var userId = User(actor);
        await using var db = new TenantDbContextFactory(database, instance.InstallationId).CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw Unavailable();
        var state = RandomToken();
        var verifier = RandomToken();
        var flow = new IntegrationOAuthFlowEntity
        {
            Id = Guid.NewGuid(),
            InstallationId = instance.InstallationId,
            InstanceId = instance.Id,
            UserId = userId,
            SecurityStamp = user.SecurityStamp,
            StateHash = Hash(state),
            Client = client,
            Revision = instance.Revision,
            Generation = instance.Generation,
            PackageVersion = instance.PackageVersion,
            PackageDigest = instance.PackageDigest,
            DescriptorDigest = instance.DescriptorDigest,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddMinutes(10)
        };
        var envelope = new Envelope(draft, verifier, RandomToken(), client == "mobile" ? mobileToken : null,
            descriptor.OAuthDefinition.SecretFieldKeys.ToArray());
        if (!await SessionValidAsync(db, flow, envelope, actor, ct, requireOwnerActor: true)) throw Unavailable();
        Seal(flow, envelope);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var locked = await IntegrationPersistenceGuard.LockInstanceAsync(db, instance.Id, ct) ?? throw Unavailable();
            if (!Matches(flow, locked)) throw Unavailable();
            // Expired authorization drafts have no device effects and no reusable credentials.
            await db.IntegrationOAuthFlows.Where(f => f.InstanceId == instance.Id && f.UserId == userId && f.ExpiresAt <= clock.GetUtcNow()).ExecuteDeleteAsync(ct);
            if (await db.IntegrationOAuthFlows.CountAsync(f => f.InstanceId == instance.Id && f.UserId == userId, ct) >= 8)
                throw new IntegrationRequestException("authorization_busy", "Cancel or finish an earlier authorization before starting another.", 429);
            db.Add(flow);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        IntegrationOAuthBeginResult result;
        try
        {
            using var lease = await gate.EnterAsync(ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(runtimeOptions.Value.MaximumNegotiatedRequestTimeoutSeconds));
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            result = await executor.BeginAuthorizationAsync(new(instance.ProviderId, instance.PackageVersion, instance.PackageDigest), draft,
                new(options.CallbackUri, state, challenge), deadline.Token);
            if (!Uri.TryCreate(result.AuthorizationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.Fragment.Length != 0 || result.AuthorizationUrl.Length > 8192) throw Unavailable();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await FinishAsync(flow, null, "failed", "authorization_start_failed", CancellationToken.None);
            throw new IntegrationRequestException("authorization_start_failed", "The provider authorization could not be started. Try again later.", 503);
        }
        await using var check = new TenantDbContextFactory(database, instance.InstallationId).CreateDbContext();
        var current = await check.IntegrationInstances.AsNoTracking().SingleAsync(i => i.Id == instance.Id, ct);
        if (!Matches(flow, current)) { await FinishAsync(flow, null, "failed", "configuration_conflict", CancellationToken.None); throw Unavailable(); }
        return new(flow.Id, result.AuthorizationUrl, flow.ExpiresAt, client == "mobile" ? IntegrationOAuthOptions.MobileReturnUri : "/settings", envelope.ReturnNonce);
    }

    private async Task<IntegrationOAuthFlowEntity> OwnedAsync(DeyeSolarDbContext db, Guid instance, Guid id, ClaimsPrincipal actor, CancellationToken ct)
        => await db.IntegrationOAuthFlows.SingleOrDefaultAsync(f => f.Id == id && f.InstanceId == instance && f.UserId == User(actor), ct)
            ?? throw new IntegrationRequestException("authorization_not_found", "This authorization is not available.", 404);

    public async Task<IntegrationOAuthStatusDto> ReadAsync(string installation, Guid instance, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    {
        await using var db = new TenantDbContextFactory(database, installation).CreateDbContext();
        var flow = await OwnedAsync(db, instance, id, actor, ct);
        var current = await db.IntegrationInstances.AsNoTracking().SingleAsync(i => i.Id == instance, ct);
        if (flow.Status is "pending" or "exchanging" or "ready" && (!Matches(flow, current) || !await SessionValidAsync(db, flow, Open(flow), actor, ct, requireOwnerActor: true)))
            return new(flow.Id, "failed", flow.ExpiresAt, new Dictionary<string, JsonElement>(), new Dictionary<string, bool>(), "authorization_stale");
        return Status(flow);
    }
    public async Task<IntegrationOAuthStatusDto> CancelAsync(string installation, Guid instance, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    {
        await using var db = new TenantDbContextFactory(database, installation).CreateDbContext();
        var flow = await OwnedAsync(db, instance, id, actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        _ = await IntegrationPersistenceGuard.LockInstanceAsync(db, instance, ct) ?? throw Unavailable();
        await db.Entry(flow).ReloadAsync(ct);
        if (flow.Status is "pending" or "exchanging" or "ready")
        {
            if (!await SessionValidAsync(db, flow, Open(flow), actor, ct, requireOwnerActor: true)) throw Unavailable();
            flow.Status = "cancelled";
            flow.Ciphertext = "";
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Status(flow);
    }

    public async Task<IntegrationDraftConfiguration> ReadyAsync(IntegrationInstanceEntity instance, Guid id,
        IntegrationDraftConfiguration baseline, ClaimsPrincipal actor, CancellationToken ct)
    {
        await using var db = new TenantDbContextFactory(database, instance.InstallationId).CreateDbContext();
        var flow = await OwnedAsync(db, instance.Id, id, actor, ct);
        if (flow.Status != "ready" || flow.ExpiresAt <= clock.GetUtcNow() || !Matches(flow, instance)) throw Unavailable();
        var envelope = Open(flow);
        if (envelope.BindingFingerprint != Binding(baseline, envelope.SecretKeys) || !await SessionValidAsync(db, flow, envelope, actor, ct, requireOwnerActor: true)) throw Unavailable();
        return envelope.Draft;
    }
    public async Task ConsumeAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    {
        var flow = await OwnedAsync(db, instance.Id, id, actor, ct);
        if (flow.Status != "ready" || flow.ExpiresAt <= clock.GetUtcNow() || !Matches(flow, instance)
            || !await SessionValidAsync(db, flow, Open(flow), actor, ct, requireOwnerActor: true)) throw Unavailable();
        var envelope = Open(flow);
        if (instance.AccountIdentity is { } existing && envelope.AccountIdentity != existing) throw Unavailable();
        instance.AccountIdentity ??= envelope.AccountIdentity;
        flow.Status = "consumed";
        flow.Ciphertext = "";
    }

    public async Task<string?> CallbackAsync(string? state, string? code, string? error, ClaimsPrincipal webActor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 128 || code?.Length > 8192 || error?.Length > 256) throw Unavailable();
        await using var lookup = new DeyeSolarDbContext(database);
        var hash = Hash(state);
        var found = await lookup.IntegrationOAuthFlows.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(f => f.StateHash == hash, ct) ?? throw Unavailable();
        Envelope envelope;
        IntegrationInstanceEntity instance;
        await using (var db = new TenantDbContextFactory(database, found.InstallationId).CreateDbContext())
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            instance = await IntegrationPersistenceGuard.LockInstanceAsync(db, found.InstanceId, ct) ?? throw Unavailable();
            var flow = await db.IntegrationOAuthFlows.SingleAsync(f => f.Id == found.Id, ct);
            if (flow.Status != "pending" || flow.ExpiresAt <= clock.GetUtcNow() || !Matches(flow, instance)) throw Unavailable();
            envelope = Open(flow);
            if (!await SessionValidAsync(db, flow, envelope, webActor, ct)) throw Unavailable();
            flow.Status = !string.IsNullOrEmpty(error) || string.IsNullOrWhiteSpace(code) ? "failed" : "exchanging";
            if (flow.Status == "failed") { flow.Code = "authorization_denied"; flow.Ciphertext = ""; }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            found = flow;
        }
        if (found.Status == "exchanging")
        {
            IntegrationOAuthCompleteResult? result = null;
            try
            {
                var descriptor = await catalog.GetAsync(instance.ProviderId, instance.PackageVersion, ct);
                if (descriptor.DescriptorDigest != found.DescriptorDigest || descriptor.PackageDigest != found.PackageDigest || descriptor.OAuthDefinition is null) throw Unavailable();
                using var lease = await gate.EnterAsync(ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(runtimeOptions.Value.MaximumNegotiatedRequestTimeoutSeconds));
                result = await executor.CompleteAuthorizationAsync(new(instance.ProviderId, instance.PackageVersion, instance.PackageDigest), envelope.Draft,
                    new(code!, options.CallbackUri, envelope.Verifier), deadline.Token);
                if (!result.Success || result.PublicValues.ValueKind != JsonValueKind.Object || result.SecretValues.Count > 100
                    || result.AccountIdentity?.Length > 256 || result.AccountIdentity?.Any(char.IsControl) == true
                    || instance.AccountIdentity is not null && result.AccountIdentity != instance.AccountIdentity
                    || result.SecretValues.Any(p => !envelope.SecretKeys.Contains(p.Key, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 8192)
                    || result.PublicValues.EnumerateObject().Any(p => !descriptor.Fields.Any(f => f.Key == p.Name && !f.Secret && f.Kind != "secret"))) throw Unavailable();
                var values = envelope.Draft.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                var publicResult = result.PublicValues.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                foreach (var value in publicResult) values[value.Key] = value.Value;
                if (descriptor.Fields.Any(field => field.Required && envelope.SecretKeys.Contains(field.Key, StringComparer.Ordinal)
                    && IntegrationUiConditions.IsFieldActive(descriptor, field, IntegrationJson.Element(values)) && !result.SecretValues.ContainsKey(field.Key))) throw Unavailable();
                var credentials = envelope.Draft.Secrets.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                foreach (var value in result.SecretValues) credentials[value.Key] = value.Value;
                var ready = new IntegrationDraftConfiguration(IntegrationJson.Element(values), credentials);
                envelope = envelope with { Draft = ready, BindingFingerprint = Binding(ready, envelope.SecretKeys), PublicResult = publicResult, AccountIdentity = result.AccountIdentity };
            }
            catch (Exception) when (!ct.IsCancellationRequested) { result = null; }
            await FinishAsync(found, result is null ? null : envelope, result is null ? "failed" : "ready", result is null ? "authorization_failed" : null, CancellationToken.None, webActor);
        }
        return found.Client == "mobile" ? IntegrationOAuthOptions.MobileReturnUri + "?flowId=" + found.Id.ToString("D") + "&returnNonce=" + envelope.ReturnNonce : null;
    }

    private async Task FinishAsync(IntegrationOAuthFlowEntity expected, Envelope? ready, string status, string? code, CancellationToken ct, ClaimsPrincipal? actor = null)
    {
        await using var db = new TenantDbContextFactory(database, expected.InstallationId).CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var instance = await IntegrationPersistenceGuard.LockInstanceAsync(db, expected.InstanceId, ct);
        var flow = await db.IntegrationOAuthFlows.SingleOrDefaultAsync(f => f.Id == expected.Id, ct);
        if (flow is null || flow.Status != expected.Status) return;
        if (ready is not null && (instance is null || !Matches(flow, instance) || flow.ExpiresAt <= clock.GetUtcNow()
            || !await SessionValidAsync(db, flow, ready, actor, ct))) { ready = null; status = "failed"; code = "authorization_stale"; }
        flow.Status = status;
        flow.Code = code;
        if (ready is null) flow.Ciphertext = ""; else Seal(flow, ready);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
