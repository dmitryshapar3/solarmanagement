using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface IRuleRepository
{
    Task<List<TriggerRule>> GetAllAsync(CancellationToken ct);
    Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct);
    Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct);
    Task UpdateAsync(TriggerRule rule, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
}
