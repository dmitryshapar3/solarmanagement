using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public class BackendSocketInventoryService : ISocketInventoryService
{
    private readonly IOptionsMonitor<TuyaOptions> _tuyaOptions;
    private readonly IOptionsMonitor<ShellyOptions> _shellyOptions;
    private readonly CloudSocketInventoryService _cloudInventoryService;
    private readonly ShellySocketInventoryService _shellyInventoryService;
    private readonly ILogger<BackendSocketInventoryService> _logger;

    public BackendSocketInventoryService(
        IOptionsMonitor<TuyaOptions> tuyaOptions,
        IOptionsMonitor<ShellyOptions> shellyOptions,
        CloudSocketInventoryService cloudInventoryService,
        ShellySocketInventoryService shellyInventoryService,
        ILogger<BackendSocketInventoryService> logger)
    {
        _tuyaOptions = tuyaOptions;
        _shellyOptions = shellyOptions;
        _cloudInventoryService = cloudInventoryService;
        _shellyInventoryService = shellyInventoryService;
        _logger = logger;
    }

    public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
        => GetDevicesAsync(forceRefresh: false, ct);

    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
        => GetDevicesAsync(forceRefresh: true, ct);

    private async Task<IReadOnlyList<DevicePowerInfo>> GetDevicesAsync(bool forceRefresh, CancellationToken ct)
    {
        var devices = new List<DevicePowerInfo>();
        var errors = new List<Exception>();

        if (IsTuyaConfigured())
        {
            await AddDevicesAsync(
                devices,
                errors,
                _cloudInventoryService,
                SocketDeviceSources.Tuya,
                "Tuya",
                forceRefresh,
                ct);
        }

        if (IsShellyConfigured())
        {
            await AddDevicesAsync(
                devices,
                errors,
                _shellyInventoryService,
                SocketDeviceSources.Shelly,
                "Shelly",
                forceRefresh,
                ct);
        }

        if (devices.Count == 0 && errors.Count > 0)
            throw new InvalidOperationException(
                "Failed to fetch socket devices from configured cloud providers.",
                errors[0]);

        return devices;
    }

    private async Task AddDevicesAsync(
        List<DevicePowerInfo> target,
        List<Exception> errors,
        ISocketInventoryService service,
        string source,
        string displaySource,
        bool forceRefresh,
        CancellationToken ct)
    {
        try
        {
            var devices = forceRefresh
                ? await service.RefreshDevicesAsync(ct)
                : await service.GetCachedDevicesAsync(ct);

            target.AddRange(PrefixDevices(devices, source, displaySource));
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            _logger.LogWarning(ex, "Failed to fetch {Source} socket devices", displaySource);
        }
    }

    private static IReadOnlyList<DevicePowerInfo> PrefixDevices(
        IReadOnlyList<DevicePowerInfo> devices,
        string source,
        string displaySource)
        => devices
            .Select(d => new DevicePowerInfo(
                SocketEntityIds.TryParse(d.Id, out _, out _)
                    ? d.Id
                    : SocketEntityIds.Create(source, d.Id),
                d.Name,
                FormatCategory(displaySource, d.Category),
                d.Online,
                d.IsOn,
                d.CurrentPowerW))
            .ToList();

    private bool IsTuyaConfigured()
    {
        var opts = _tuyaOptions.CurrentValue;
        return !string.IsNullOrWhiteSpace(opts.AccessId) &&
            !string.IsNullOrWhiteSpace(opts.AccessSecret);
    }

    private bool IsShellyConfigured()
    {
        var opts = _shellyOptions.CurrentValue;
        return !string.IsNullOrWhiteSpace(opts.ServerUri) &&
            !string.IsNullOrWhiteSpace(opts.AuthKey);
    }

    private static string FormatCategory(string source, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return source;

        return category.StartsWith(source, StringComparison.OrdinalIgnoreCase)
            ? category
            : $"{source} {category}";
    }
}
