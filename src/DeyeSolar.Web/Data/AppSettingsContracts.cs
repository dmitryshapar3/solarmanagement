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
