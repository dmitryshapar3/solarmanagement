namespace SolarManagement.Inverters.Contracts;

// This identity contains no credentials and changes whenever a selected source is replaced.
public sealed class InverterConnectionOptions
{
    public const string Section = "Inverter";
    public string DeviceKey { get; set; } = string.Empty;
    public string ConnectionIdentity { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long Generation { get; set; }
}
