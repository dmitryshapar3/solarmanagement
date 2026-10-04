import type { IntegrationField, IntegrationProvider, IntegrationUiCondition, IntegrationValue } from "../../core/api/IntegrationApi";

// Match the server's String.IsNullOrWhiteSpace, including Unicode NEL and excluding BOM.
export function integrationBlank(value: string): boolean { return /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/.test(value); }

export function effectiveIntegrationValues(provider: IntegrationProvider, values: Record<string, IntegrationValue>): Record<string, IntegrationValue> {
  const result: Record<string, IntegrationValue> = Object.assign(Object.create(null), values);
  for (const field of provider.fields) {
    if (!field.secret && field.kind !== "secret" && !Object.hasOwn(result, field.key) && field.defaultValue !== undefined) {
      result[field.key] = field.defaultValue;
    }
  }
  return { ...result };
}

export function integrationConditionActive(condition: IntegrationUiCondition | null | undefined, values: Record<string, IntegrationValue>): boolean {
  if (!condition) return true;
  const actual = values[condition.field];
  const present = Object.hasOwn(values, condition.field) && actual != null && (typeof actual !== "string" || !integrationBlank(actual));
  switch (condition.operator) {
    case "present": return present;
    case "absent": return !present;
    case "eq": return present && actual === condition.value;
    case "notEq": return present && actual !== condition.value;
    default: return false;
  }
}

export function integrationFieldActive(provider: IntegrationProvider, field: IntegrationField, values: Record<string, IntegrationValue>): boolean {
  const effective = effectiveIntegrationValues(provider, values);
  const group = Array.isArray(provider.uiLayout?.steps) ? provider.uiLayout.steps.flatMap(step => Array.isArray(step.groups) ? step.groups : [])
    .find(group => Array.isArray(group.fieldKeys) && group.fieldKeys.includes(field.key)) : undefined;
  return integrationConditionActive(field.activeWhen, effective) && integrationConditionActive(group?.activeWhen, effective);
}
