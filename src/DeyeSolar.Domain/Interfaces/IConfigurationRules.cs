using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

/// <summary>Interactive rule configuration; runtime bookkeeping belongs to the worker repository.</summary>
public interface IConfigurationRules
{
    Task<List<TriggerRule>> GetAllAsync(CancellationToken ct);
    Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct);
    Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct);
    Task UpdateAsync(TriggerRule rule, CancellationToken ct);
    Task DeleteAsync(int id, string configurationVersion, CancellationToken ct);
}
