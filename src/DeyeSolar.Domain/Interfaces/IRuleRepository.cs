using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface IRuleRepository : IConfigurationRules
{
    Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct);
}
