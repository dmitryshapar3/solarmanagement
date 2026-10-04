import type {
  IntegrationConfiguration,
  IntegrationConfigurationChange,
  IntegrationField,
  IntegrationProvider,
  IntegrationValue,
  SecretOperation
} from "../../core/api/IntegrationApi";

const supportedKinds = new Set(["text", "secret", "integer", "number", "boolean", "select"]);

export type IntegrationDraft = {
  configuration: IntegrationConfiguration;
  provider: IntegrationProvider;
  values: Record<string, string>;
  secrets: Record<string, SecretOperation>;
};

export function isSecretField(field: IntegrationField): boolean {
  return field.secret || field.kind === "secret";
}

export function unsupportedProvider(provider: IntegrationProvider): string | null {
  if (provider.uiContractVersion !== 1 || provider.requiredUiFeatures.some(feature => !supportedKinds.has(feature))) {
    return "This integration needs a newer app. Update the app before editing its settings.";
  }
  if (provider.fields.some(field => field.required && !supportedKinds.has(field.kind))) {
    return "This integration requires a field this app cannot display. Update the app before editing its settings.";
  }
  return null;
}

export function supportsField(field: IntegrationField): boolean {
  return supportedKinds.has(field.kind);
}

export function createIntegrationDraft(provider: IntegrationProvider, configuration: IntegrationConfiguration): IntegrationDraft {
  const values: Record<string, string> = {};
  const secrets: Record<string, SecretOperation> = {};
  const publicValues: Record<string, IntegrationValue> = {};
  for (const field of provider.fields) {
    if (isSecretField(field)) {
      secrets[field.key] = { operation: "keep" };
    } else {
      const value = configuration.values[field.key] ?? field.defaultValue;
      values[field.key] = value === undefined || value === null ? field.kind === "boolean" ? "false" : "" : String(value);
      if (Object.hasOwn(configuration.values, field.key)) publicValues[field.key] = configuration.values[field.key]!;
    }
  }
  // Never retain a secret accidentally included in a server's public values map.
  return { provider, configuration: { ...configuration, values: publicValues }, values, secrets };
}

export function integrationChange(draft: IntegrationDraft): IntegrationConfigurationChange {
  const unsupported = unsupportedProvider(draft.provider);
  if (unsupported) throw new Error(unsupported);
  const instance = draft.configuration.instance;
  const values: Record<string, IntegrationValue> = {};
  const secretOperations: Record<string, SecretOperation> = {};
  for (const field of draft.provider.fields) {
    if (isSecretField(field)) {
      const secret = draft.secrets[field.key] ?? { operation: "keep" };
      const present = secret.operation === "replace" ? Boolean(secret.value?.trim())
        : secret.operation === "keep" && draft.configuration.secretPresent[field.key] === true;
      if (field.required && !present) throw new Error(`${field.label} is required.`);
      if (secret.operation === "replace" && !secret.value) throw new Error(`Enter a replacement for ${field.label}.`);
      secretOperations[field.key] = secret.operation === "replace"
        ? { operation: "replace", value: secret.value }
        : { operation: secret.operation };
      continue;
    }
    if (!supportsField(field)) {
      if (Object.hasOwn(draft.configuration.values, field.key)) values[field.key] = draft.configuration.values[field.key]!;
      continue;
    }
    const text = draft.values[field.key] ?? "";
    if (field.required && !text.trim()) throw new Error(`${field.label} is required.`);
    if (field.kind === "integer" || field.kind === "number") {
      if (!text.trim()) { values[field.key] = null; continue; }
      const number = Number(text);
      if (!Number.isFinite(number) || field.kind === "integer" && !Number.isSafeInteger(number)) {
        throw new Error(`Enter a valid ${field.kind === "integer" ? "whole number" : "number"} for ${field.label}.`);
      }
      if (field.minimum != null && number < field.minimum || field.maximum != null && number > field.maximum) {
        throw new Error(`${field.label} is outside the allowed range.`);
      }
      values[field.key] = number;
    } else if (field.kind === "boolean") {
      if (text !== "true" && text !== "false") throw new Error(`Choose a value for ${field.label}.`);
      values[field.key] = text === "true";
    } else {
      if (field.kind === "select" && text && !field.options?.some(option => option.value === text)) {
        throw new Error(`Choose a supported value for ${field.label}.`);
      }
      values[field.key] = field.kind === "select" && !text ? null : text;
    }
  }
  return {
    expectedRevision: instance.revision,
    packageVersion: instance.packageVersion,
    packageDigest: instance.packageDigest,
    descriptorDigest: instance.descriptorDigest,
    values,
    secretOperations
  };
}

export function integrationDraftChanged(draft: IntegrationDraft): boolean {
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
