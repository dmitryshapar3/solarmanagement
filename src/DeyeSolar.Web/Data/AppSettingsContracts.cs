namespace DeyeSolar.Web.Data;

public interface IAppSettingsReader
{
    Task<T> LoadSectionAsync<T>(string section) where T : new();
}

public interface IAppSettingsWriter
{
    Task SaveSectionAsync<T>(string section, T options) where T : class;
    Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections, CancellationToken ct = default);
}

// Internal partial updates retain properties absent from older client payloads.
internal sealed record AppSettingsPatch(IReadOnlyDictionary<string, object?> Values,
    Action<IReadOnlyDictionary<string, string>>? ValidateCurrent = null);
