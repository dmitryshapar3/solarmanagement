using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class RuleRepository : IRuleRepository
{
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;

    public RuleRepository(IDbContextFactory<DeyeSolarDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<TriggerRule>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TriggerRules.ToListAsync(ct);
    }

    public async Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TriggerRules.FindAsync(new object[] { id }, ct);
    }

    public async Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.TriggerRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task UpdateAsync(TriggerRule rule, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var originalState = await db.TriggerRules
            .AsNoTracking()
            .Where(r => r.Id == rule.Id)
            .Select(r => (bool?)r.CurrentState)
            .FirstOrDefaultAsync(ct);

        if (originalState.HasValue && originalState.Value != rule.CurrentState)
            rule.CurrentStateChangedAt = DateTime.UtcNow;

        db.TriggerRules.Update(rule);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.TriggerRules.FindAsync(new object[] { id }, ct);
        if (rule != null)
        {
            db.TriggerRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
    }
}
