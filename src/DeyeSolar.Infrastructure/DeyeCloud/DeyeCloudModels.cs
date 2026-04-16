namespace DeyeSolar.Infrastructure.DeyeCloud;

public record DeyeStation(long Id, string Name, string? Address);

public record DeyeDevice(string SerialNumber, string DeviceType, long DeviceId, long StationId);
