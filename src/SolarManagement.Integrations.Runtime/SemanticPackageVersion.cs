using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SolarManagement.Integrations.Runtime;

internal sealed class SemanticPackageVersion(BigInteger major, BigInteger minor, BigInteger patch, string[]? prerelease)
    : IComparable<SemanticPackageVersion>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?\z", RegexOptions.CultureInvariant);
    private BigInteger Major { get; } = major;
    private BigInteger Minor { get; } = minor;
    private BigInteger Patch { get; } = patch;
    private string[]? Prerelease { get; } = prerelease;
    public static bool TryParse(string value, out SemanticPackageVersion result)
    {
        result = null!;
        if (value.Length > 128) return false;
        var match = Pattern.Match(value);
        if (!match.Success) return false;
        string[]? parts = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : null;
        if (parts?.Any(part => part.Length == 0 || Numeric(part) && part.Length > 1 && part[0] == '0') == true
            || match.Groups[5].Success && match.Groups[5].Value.Split('.').Any(part => part.Length == 0)) return false;
        result = new(BigInteger.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), parts);
        return true;
    }
    public static SemanticPackageVersion Parse(string value) => TryParse(value, out var parsed) ? parsed
        : throw new InvalidDataException("Package versions must use normalized semantic versioning.");
    public int CompareTo(SemanticPackageVersion? other)
    {
        if (other is null) return 1;
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        for (var index = 0; index < Math.Min(Prerelease.Length, other.Prerelease.Length); index++)
        {
            var first = Prerelease[index];
            var second = other.Prerelease[index];
            var firstNumeric = Numeric(first);
            var secondNumeric = Numeric(second);
            var comparison = firstNumeric && secondNumeric
                ? BigInteger.Parse(first, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(second, CultureInfo.InvariantCulture))
                : firstNumeric != secondNumeric ? firstNumeric ? -1 : 1 : StringComparer.Ordinal.Compare(first, second);
            if (comparison != 0) return comparison;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }
    private static bool Numeric(string value) => value.All(char.IsAsciiDigit);
}
