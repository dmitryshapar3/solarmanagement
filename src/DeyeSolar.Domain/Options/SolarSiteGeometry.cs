namespace DeyeSolar.Domain.Options;

/// <summary>Physical location and roof invariants shared by editable site settings and the generation model.</summary>
public readonly record struct SolarSiteGeometry(double Latitude, double Longitude, double Roof1Kwp, double Roof2Kwp,
    double Roof1Tilt, double Roof2Tilt, double Roof1Azimuth, double Roof2Azimuth)
{
    public double TotalKwp => Roof1Kwp + Roof2Kwp;
    public bool IsValid => double.IsFinite(Latitude) && double.IsFinite(Longitude)
        && double.IsFinite(Roof1Kwp) && double.IsFinite(Roof2Kwp) && double.IsFinite(TotalKwp)
        && double.IsFinite(Roof1Tilt) && double.IsFinite(Roof2Tilt)
        && double.IsFinite(Roof1Azimuth) && double.IsFinite(Roof2Azimuth)
        && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180
        && Roof1Kwp >= 0 && Roof2Kwp >= 0 && TotalKwp > 0
        && Roof1Tilt is >= 0 and <= 90 && Roof2Tilt is >= 0 and <= 90
        && Roof1Azimuth is >= 0 and < 360 && Roof2Azimuth is >= 0 and < 360;
}
