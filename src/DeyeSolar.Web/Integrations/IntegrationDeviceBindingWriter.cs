using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

public interface IIntegrationDeviceBindingWriter
{
    Task<IntegrationDeviceBindingEntity> BindAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance,
        IntegrationDiscoveredDevice device, ClaimsPrincipal actor, CancellationToken ct);
}

/// <summary>Writes binding identity and the atomic default-inverter cutover inside the caller's guarded transaction.</summary>
public sealed class IntegrationDeviceBindingWriter : IIntegrationDeviceBindingWriter
{
    public async Task<IntegrationDeviceBindingEntity> BindAsync(DeyeSolarDbContext db, IntegrationInstanceEntity instance,
        IntegrationDiscoveredDevice device, ClaimsPrincipal actor, CancellationToken ct)
    {
        var channel = device.Channel ?? "";
        var binding = await db.Set<IntegrationDeviceBindingEntity>().SingleOrDefaultAsync(b => b.InstanceId == instance.Id && b.Kind == device.Kind && b.RemoteId == device.RemoteId && b.Channel == channel, ct);
        if (binding is null)
        {
            binding = new()
            {
                Id = Guid.NewGuid(),
                InstallationId = instance.InstallationId,
                InstanceId = instance.Id,
                Kind = device.Kind,
                AddedByUserId = device.Kind == "socket" ? actor.FindFirstValue(ClaimTypes.NameIdentifier) : null,
                RemoteId = device.RemoteId,
                Channel = channel,
                Name = device.Name,
                AccountIdentity = device.AccountIdentity,
                MetadataJson = device.Metadata?.GetRawText() ?? "{}"
            };
            db.Add(binding);
        }
        if (device.Kind == "inverter")
        {
            // Clear the previous selection inside this transaction before the filtered
            // unique index sees the new binding; readers cannot see a partial cutover.
            await db.Set<IntegrationDeviceBindingEntity>().Where(b => b.Kind == "inverter" && b.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.IsDefault, false), ct);
            binding.IsDefault = true;
            if (db.Entry(binding).State != EntityState.Added)
                db.Entry(binding).Property(b => b.IsDefault).IsModified = true;
        }
        return binding;
    }
}
