using DeyeSolar.Domain.Models;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

// Fixtures explicitly provide measured values and quality, just as the live provider gateway does.
internal static class ConfirmedInverterReading
{
    public static InverterData Create(InverterData reading)
    {
        var measured = reading.Timestamp;
        return reading with
        {
            BatterySocValid = true,
            Telemetry = new InverterTelemetry(new(Guid.Parse("00000000-0000-0000-0000-000000000001")), measured,
                new(new Percent(reading.BatterySoc), measured, MeasurementQuality.Good),
                new(new Watts(reading.BatteryPower), measured, MeasurementQuality.Good),
                new(new Celsius(reading.BatteryTemperature), measured, MeasurementQuality.Good),
                new(new Volts(reading.BatteryVoltage), measured, MeasurementQuality.Good),
                new(new Amperes(reading.BatteryCurrent), measured, MeasurementQuality.Good),
                new(new Watts(reading.SolarProduction), reading.SolarObservedAt, reading.SolarObservedAt.HasValue ? MeasurementQuality.Good : MeasurementQuality.Missing),
                new(new Watts(reading.GridConsumption), reading.GridObservedAt, reading.GridObservedAt.HasValue ? MeasurementQuality.Good : MeasurementQuality.Missing),
                new(new Watts(reading.LoadPower), measured, MeasurementQuality.Good), SolarManagement.Inverters.Contracts.SolarPowerBasis.PvDc)
        };
    }
}
