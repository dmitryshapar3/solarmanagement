using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
namespace DeyeSolar.Web.Auth;
public sealed class AuthorizedRuleRepository(IConfigurationRules inner, InteractiveSecurityContext security) : IConfigurationRules
{
    public async Task<List<TriggerRule>> GetAllAsync(CancellationToken ct)
    { await security.EnsureAsync(InstallationPermission.Read, ct); return await inner.GetAllAsync(ct); }
    public async Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct)
    { await security.EnsureAsync(InstallationPermission.Read, ct); return await inner.GetByIdAsync(id, ct); }
    public async Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct)
    { await security.EnsureAsync(InstallationPermission.ManageRules, ct); return await inner.CreateAsync(rule, ct); }
    public async Task UpdateAsync(TriggerRule rule, CancellationToken ct)
    { await security.EnsureAsync(InstallationPermission.ManageRules, ct); await inner.UpdateAsync(rule, ct); }
    public async Task DeleteAsync(int id, CancellationToken ct)
    { await security.EnsureAsync(InstallationPermission.ManageRules, ct); await inner.DeleteAsync(id, ct); }
}
