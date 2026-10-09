using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Web.Data;

/// <summary>The persisted property contract shared by settings reads, writes and tenant defaults.</summary>
internal static class SettingsSchema
{
    private static readonly IReadOnlyDictionary<string, Type> RuntimeTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [PollingOptions.Section] = typeof(PollingOptions), [DisplayOptions.Section] = typeof(DisplayOptions),
        [SolarEstimateOptions.Section] = typeof(SolarEstimateOptions), [SolarSalesOptions.Section] = typeof(SolarSalesOptions)
    };
    private static readonly ConcurrentDictionary<(string Section, Type Type), IReadOnlyList<SettingProperty>> Cache = new();
    private static readonly ConcurrentDictionary<(string Section, Type Type), IReadOnlyList<SettingProperty>> EffectivePropertiesCache = new();

    private static IReadOnlyList<SettingProperty> EffectiveProperties(string section, Type type)
        => EffectivePropertiesCache.GetOrAdd((section, type), key => Array.AsReadOnly(key.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "Section" && property.GetIndexParameters().Length == 0
                && (property.CanRead || property.CanWrite)
                && !(key.Section == SolarEstimateOptions.Section && property.Name == nameof(SolarEstimateOptions.ApiKey)))
            .Select(property => new SettingProperty(property)).ToArray()));

    public static IReadOnlyList<SettingProperty> Properties(string section, Type type) => Cache.GetOrAdd((section, type), key =>
    {
        var contract = RuntimeTypes.TryGetValue(key.Section, out var runtimeType) ? runtimeType : null;
        var storedNames = contract is null ? null : EffectiveProperties(key.Section, contract)
            .Where(property => property.CanRead && property.CanWrite)
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return Array.AsReadOnly(EffectiveProperties(key.Section, key.Type)
            .Where(property => storedNames is null || storedNames.Contains(property.Name)).ToArray());
    });

    // A fingerprint includes computed dependencies too; persistence includes only the stored contract.
    public static IReadOnlyDictionary<string, object?> ConfigurationValues(string section, object value)
        => EffectiveProperties(section, value.GetType()).Where(property => property.CanRead).OrderBy(property => property.Name)
            .ToDictionary(property => property.Name, property => property.ReadValue(value));

    public static bool IsRuntimeSetting(string section, string name) => RuntimeTypes.TryGetValue(section, out var type)
        && Properties(section, type).Any(property => property.Name == name);

    // Null in an older DTO means an omitted non-nullable runtime field, not an empty numeric value.
    // Nullable runtime options retain explicit clearing; legacy non-runtime sections retain their codec.
    public static bool CanPersistNull(string section, string name) => !RuntimeTypes.TryGetValue(section, out var type)
        || Properties(section, type).SingleOrDefault(property => property.Name == name)?.AllowsNull == true;

    public static Dictionary<string, string?> RuntimeDefaults()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, type) in RuntimeTypes)
        {
            var defaults = Activator.CreateInstance(type)!;
            foreach (var property in Properties(section, type))
                result[$"{section}:{property.Name}"] = property.Read(defaults);
        }
        return result;
    }

    public static IEnumerable<(string Section, string Key, string Value)> RuntimeEntries(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            var separator = name.IndexOf(':');
            if (separator <= 0) continue;
            var section = name[..separator];
            var key = name[(separator + 1)..];
            if (IsRuntimeSetting(section, key)) yield return (section, key, value ?? "");
        }
    }
}

internal sealed class SettingProperty(PropertyInfo property)
{
    public string Name => property.Name;
    public bool CanRead => property.CanRead;
    public bool CanWrite => property.CanWrite;
    public bool AllowsNull => Nullable.GetUnderlyingType(property.PropertyType) is not null;
    public object? ReadValue(object value) => property.GetValue(value);
    public string Read(object value) => InvariantSettingCodec.Format(ReadValue(value));
    public void Apply(object target, string value)
    {
        if (!CanWrite) return;
        try { property.SetValue(target, InvariantSettingCodec.Parse(value, property.PropertyType)); }
        catch { /* Invalid stored values keep the installation's bound/default value. */ }
    }
}

internal static class InvariantSettingCodec
{
    public static string Format(object? value) => value switch
    {
        null => "",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static object? Parse(string value, Type targetType)
    {
        var nullable = Nullable.GetUnderlyingType(targetType);
        var type = nullable ?? targetType;
        return type == typeof(string) ? value : string.IsNullOrEmpty(value) && nullable is not null
            ? null : TypeDescriptor.GetConverter(type).ConvertFromInvariantString(value);
    }
}
