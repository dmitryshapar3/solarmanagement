using System.Security.Claims;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

public sealed partial class IntegrationSetupService
{
    public async Task<IReadOnlyList<IntegrationSourceInverterDto>> SocketSourceOptionsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var devices = await (from binding in db.IntegrationDeviceBindings.AsNoTracking()
            join instance in db.IntegrationInstances.AsNoTracking() on binding.InstanceId equals instance.Id
            where binding.Kind == "inverter" && binding.Enabled && instance.State == "enabled"
            orderby binding.Name, binding.Id select binding).ToListAsync(ct);
        return devices.Where(binding => IntegrationCapabilities.Read(binding).HasBattery)
            .Select(binding => new IntegrationSourceInverterDto(binding.Id, binding.Name, binding.IsDefault)).ToArray();
    }

    public async Task<IntegrationBindingDto> SetSocketSourceAsync(Guid instanceId, Guid deviceId,
        IntegrationSocketSourceChange request, ClaimsPrincipal actor, CancellationToken ct)
    {
        await EnsureManagerAsync(actor, ct);
        if (request.Guard is null || request.PhaseCount is not (1 or 3) || request.SourceInverterId == Guid.Empty)
            throw new IntegrationRequestException("validation", "Choose an inverter and a single-phase or three-phase circuit.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var instance = await FindAsync(db, instanceId, ct);
        Guard(instance, request.Guard.ExpectedRevision, request.Guard.PackageVersion, request.Guard.PackageDigest, request.Guard.DescriptorDigest);
        await LockSettingsMutationAsync(db, instance, ct);
        await EnsureNoActiveCommandsAsync(db, instanceId, ct);
        var device = await db.IntegrationDeviceBindings.SingleOrDefaultAsync(b => b.Id == deviceId && b.InstanceId == instanceId
            && b.Kind == "socket" && b.Enabled, ct) ?? throw new IntegrationRequestException("device_not_found", "This socket is unavailable.", 404);
        var previous = IntegrationSocketAssociation.Read(device);
        if (previous.SourceInverterId != request.ExpectedSourceInverterId || previous.PhaseCount != request.ExpectedPhaseCount)
            throw new IntegrationRequestException("association_conflict", "This socket's inverter link changed. Reload its devices before continuing.", 409);
        if (request.SourceInverterId is { } sourceId)
        {
            var source = await IntegrationDeviceEligibility.FindEnabledAsync(db, sourceId, "inverter", ct);
            var capabilities = source is null ? null : IntegrationCapabilities.Read(source);
            if (capabilities is null || !capabilities.HasBattery)
                throw new IntegrationRequestException("invalid_source", "Choose an enabled inverter with battery SOC from this installation.");
            if (!capabilities.HasSolarPower)
            {
                var identity = deviceId.ToString("D");
                if (await db.TriggerRules.AnyAsync(r => r.Enabled && r.SourceInverterId == null
                    && r.UseSolarProductionThreshold && r.EntityId == identity, ct))
                    throw new IntegrationRequestException("invalid_source", "This socket has a solar-power rule. Choose an inverter that also provides solar power.");
            }
        }
        device.MetadataJson = IntegrationSocketAssociation.Write(device, request.SourceInverterId, request.PhaseCount);
        lifecycle.Touch(instance);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        await transaction.CommitAsync(ct);
        changes.Publish(instance.InstallationId, instance.Id);
        return device.ToDto();
    }
}
