using DeyeSolar.Domain.Models;

namespace DeyeSolar.RuleEngine;

public interface IRuleDecisionEvaluator
{
    RuleDecision Decide(InverterData? current, TriggerRule rule, DateTimeOffset now,
        string? timeZoneId = null, RuleEvaluationContext? context = null);
}
