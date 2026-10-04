import { translate as t } from "../../core/i18n";
import type {
  IntegrationConfiguration,
  IntegrationConfigurationChange,
  IntegrationField,
  IntegrationProvider,
  IntegrationOAuthStatus,
  IntegrationUiCondition,
  IntegrationValue,
  SecretOperation
} from "../../core/api/IntegrationApi";
import { integrationBlank, integrationFieldActive } from "./integrationUi";

const supportedKinds = new Set(["text", "secret", "integer", "number", "boolean", "select"]);
const supportedFeatures = new Set([...supportedKinds, "wizard", "groups", "instructions", "conditional-fields", "oauth", "device-selector"]);

export type IntegrationDraft = {
  configuration: IntegrationConfiguration;
  provider: IntegrationProvider;
  values: Record<string, string>;
  secrets: Record<string, SecretOperation>;
  oauth?: { flowId: string; expiresAt: string; secretPresent: Record<string, boolean>; publicOverrides: Record<string, IntegrationValue> };
};

export function isSecretField(field: IntegrationField): boolean {
  return field.secret || field.kind === "secret";
}

export function unsupportedProvider(provider: IntegrationProvider): string | null {
  if (provider.uiContractVersion !== 1 || provider.requiredUiFeatures.some(feature => !supportedFeatures.has(feature))) {
    return t("This integration needs a newer app. Update the app before editing its settings.");
  }
  if (provider.fields.some(field => field.required && !supportedKinds.has(field.kind))) {
    return t("This integration requires a field this app cannot display. Update the app before editing its settings.");
  }
  const validCondition = (condition?: IntegrationUiCondition | null) => !condition ||
    provider.fields.some(field => field.key === condition.field && !isSecretField(field))
    && ["eq", "notEq", "present", "absent"].includes(condition.operator)
    && (!["eq", "notEq"].includes(condition.operator) || condition.value != null && ["string", "boolean", "number"].includes(typeof condition.value));
  if (provider.fields.some(field => !validCondition(field.activeWhen))) return t("This integration has unsupported conditional settings. Update the app before editing it.");
  if (provider.uiLayout) {
    const layout = provider.uiLayout;
    if (layout.version !== 1 || !Array.isArray(layout.steps) || !layout.steps.length || layout.steps.some(step => !Array.isArray(step.groups))) {
      return t("This integration needs a newer settings layout. Update the app before editing it.");
    }
    const groups = layout.steps.flatMap(step => step.groups);
    if (groups.some(group => !Array.isArray(group.fieldKeys) || !Array.isArray(group.actions) || !validCondition(group.activeWhen)
      || group.actions.some(action => !["test", "discover", "oauth"].includes(action) || !provider.actions.includes(action)))) {
      return t("This integration has unsupported settings groups. Update the app before editing it.");
    }
    const keys = groups.flatMap(group => group.fieldKeys);
    if (keys.length !== provider.fields.length || new Set(keys).size !== keys.length || provider.fields.some(field => !keys.includes(field.key))) {
      return t("This integration has unsupported settings groups. Update the app before editing it.");
    }
  }
  if (provider.actions.includes("oauth") && (!provider.oauthDefinition?.secretFieldKeys.length
    || provider.oauthDefinition.secretFieldKeys.some(key => !provider.fields.some(field => field.key === key && isSecretField(field))))) {
    return t("This integration has unsupported authorization settings. Update the app before editing it.");
  }
  return null;
}

export function supportsField(field: IntegrationField): boolean {
  return supportedKinds.has(field.kind);
}

export function createIntegrationDraft(provider: IntegrationProvider, configuration: IntegrationConfiguration): IntegrationDraft {
  const values: Record<string, string> = Object.create(null);
  const secrets: Record<string, SecretOperation> = Object.create(null);
  const publicValues: Record<string, IntegrationValue> = Object.create(null);
  for (const field of provider.fields) {
    if (isSecretField(field)) {
      secrets[field.key] = { operation: "keep" };
    } else {
      const value = Object.hasOwn(configuration.values, field.key) ? configuration.values[field.key] : field.defaultValue;
      values[field.key] = value === undefined || value === null ? "" : String(value);
      if (Object.hasOwn(configuration.values, field.key)) publicValues[field.key] = configuration.values[field.key]!;
    }
  }
  // Never retain a secret accidentally included in a server's public values map.
  return { provider, configuration: { ...configuration, values: { ...publicValues } }, values: { ...values }, secrets: { ...secrets } };
}

export function integrationPublicValues(draft: IntegrationDraft, validate = false): Record<string, IntegrationValue> {
  const values: Record<string, IntegrationValue> = Object.create(null);
  for (const field of draft.provider.fields.filter(field => !isSecretField(field))) {
    if (!supportsField(field)) {
      if (Object.hasOwn(draft.configuration.values, field.key)) values[field.key] = draft.configuration.values[field.key]!;
      continue;
    }
    const text = draft.values[field.key] ?? "";
    if (draft.oauth && Object.hasOwn(draft.oauth.publicOverrides, field.key) && draft.oauth.publicOverrides[field.key] === null && !text) {
      values[field.key] = null;
      continue;
    }
    if (!text && draft.configuration.values[field.key] == null && field.defaultValue == null) {
      if (Object.hasOwn(draft.configuration.values, field.key)) values[field.key] = null;
      continue;
    }
    if (!text && Object.hasOwn(draft.configuration.values, field.key) && draft.configuration.values[field.key] === null) {
      values[field.key] = null;
    } else if (field.kind === "integer" || field.kind === "number") {
      if (!text.trim()) { values[field.key] = null; continue; }
      const number = Number(text);
      if (!Number.isFinite(number) || field.kind === "integer" && (!Number.isInteger(number) || number < -2147483648 || number > 2147483647)) {
        if (validate) throw new Error(t("Enter a valid {0} for {1}.", field.kind === "integer" ? t("whole number") : t("number"), t(field.label)));
        values[field.key] = null;
        continue;
      }
      if (validate && (field.minimum != null && number < field.minimum || field.maximum != null && number > field.maximum)) {
        throw new Error(t("{0} is outside the allowed range.", t(field.label)));
      }
      values[field.key] = number;
    } else if (field.kind === "boolean") {
      if (text !== "true" && text !== "false") {
        if (validate) throw new Error(t("Choose a value for {0}.", t(field.label)));
        values[field.key] = null;
      } else values[field.key] = text === "true";
    } else {
      if (validate && field.kind === "select" && text && !field.options?.some(option => option.value === text)) {
        throw new Error(t("Choose a supported value for {0}.", t(field.label)));
      }
      values[field.key] = field.kind === "select" && !text ? null : text;
    }
  }
  return { ...values };
}

export function integrationChange(draft: IntegrationDraft, options: { allowMissingOAuthSecrets?: boolean } = {}): IntegrationConfigurationChange {
  const unsupported = unsupportedProvider(draft.provider);
  if (unsupported) throw new Error(unsupported);
  if (draft.oauth && Date.parse(draft.oauth.expiresAt) <= Date.now()) throw new Error(t("Authorization has expired. Authorize again before saving settings."));
  const instance = draft.configuration.instance;
  const values = integrationPublicValues(draft, true);
  const secretOperations: Record<string, SecretOperation> = Object.create(null);
  for (const field of draft.provider.fields) {
    const required = field.required && integrationFieldActive(draft.provider, field, values);
    if (isSecretField(field)) {
      const secret = draft.secrets[field.key] ?? { operation: "keep" };
      const present = secret.operation === "replace" ? Boolean(secret.value && !integrationBlank(secret.value))
        : secret.operation === "keep" && (draft.configuration.secretPresent[field.key] === true || draft.oauth?.secretPresent[field.key] === true);
      const oauthOwned = options.allowMissingOAuthSecrets && draft.provider.oauthDefinition?.secretFieldKeys.includes(field.key);
      if (required && !present && !oauthOwned) throw new Error(t("{0} is required.", t(field.label)));
      if (secret.operation === "replace" && !secret.value) throw new Error(t("Enter a replacement for {0}.", t(field.label)));
      secretOperations[field.key] = secret.operation === "replace"
        ? { operation: "replace", value: secret.value }
        : { operation: secret.operation };
      continue;
    }
    const value = values[field.key];
    if (required && (value == null || typeof value === "string" && integrationBlank(value))) throw new Error(t("{0} is required.", t(field.label)));
  }
  return {
    expectedRevision: instance.revision,
    packageVersion: instance.packageVersion,
    packageDigest: instance.packageDigest,
    descriptorDigest: instance.descriptorDigest,
    values,
    secretOperations: { ...secretOperations },
    ...(draft.oauth ? { oauthFlowId: draft.oauth.flowId } : {})
  };
}

export function applyIntegrationOAuth(draft: IntegrationDraft, status: IntegrationOAuthStatus): IntegrationDraft {
  if (status.status !== "ready" || !Number.isFinite(Date.parse(status.expiresAt)) || Date.parse(status.expiresAt) <= Date.now()) {
    throw new Error(t("Authorization is not ready or has expired. Authorize again."));
  }
  const values: Record<string, string> = Object.assign(Object.create(null), draft.values);
  for (const [key, value] of Object.entries(status.values)) {
    if (!draft.provider.fields.some(field => field.key === key && !isSecretField(field))
      || value !== null && !["string", "boolean", "number"].includes(typeof value) || typeof value === "number" && !Number.isFinite(value)) {
      throw new Error(t("Authorization returned unsupported public settings."));
    }
    values[key] = value == null ? "" : String(value);
  }
  const secretPresent: Record<string, boolean> = Object.create(null);
  const secrets: Record<string, SecretOperation> = Object.assign(Object.create(null), draft.secrets);
  for (const key of draft.provider.oauthDefinition?.secretFieldKeys ?? []) {
    secretPresent[key] = status.secretPresent[key] === true;
    secrets[key] = { operation: "keep" };
  }
  return { ...draft, values: { ...values }, secrets: { ...secrets }, oauth: { flowId: status.flowId, expiresAt: status.expiresAt,
    secretPresent: { ...secretPresent }, publicOverrides: { ...status.values } } };
}

export function integrationDraftChanged(draft: IntegrationDraft): boolean {
  if (draft.oauth) return true;
  const saved = createIntegrationDraft(draft.provider, draft.configuration);
  if (draft.provider.fields.some(field => isSecretField(field)
    ? draft.secrets[field.key]?.operation !== "keep"
    : draft.values[field.key] !== saved.values[field.key])) return true;
  try {
    const values = integrationChange(draft).values;
    return Object.keys(values).some(key => !Object.hasOwn(draft.configuration.values, key)
      || values[key] !== draft.configuration.values[key]);
  } catch { return true; }
}
