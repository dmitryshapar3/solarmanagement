import { useEffect, useState, type ReactNode } from "react";
import { StyleSheet, Text, View } from "react-native";
import type { IntegrationField, SecretOperation } from "../../core/api/IntegrationApi";
import { AppButton, SwitchRow, TextField } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { integrationPublicValues, isSecretField, supportsField, unsupportedProvider, type IntegrationDraft } from "./integrationDraft";
import { effectiveIntegrationValues, integrationConditionActive, integrationFieldActive } from "./integrationUi";

export function IntegrationFields({ draft, disabled, onValue, onSecret, renderAction }: {
  draft: IntegrationDraft; disabled: boolean;
  onValue(key: string, value: string): void;
  onSecret(key: string, value: SecretOperation): void;
  renderAction(action: string): ReactNode;
}) {
  const [stepIndex, setStepIndex] = useState(0);
  useEffect(() => setStepIndex(0), [draft.configuration.instance.id, draft.provider.descriptorDigest]);
  const provider = draft.provider;
  const values = effectiveIntegrationValues(provider, integrationPublicValues(draft));
  const layout = unsupportedProvider(provider) ? null : provider.uiLayout;

  function fieldControl(field: IntegrationField) {
    if (!integrationFieldActive(provider, field, values)) return null;
    if (!supportsField(field)) return <Text key={field.key} style={styles.detail}>{field.label}: this field needs a newer app. Its saved value is preserved.</Text>;
    if (isSecretField(field)) {
      const secret = draft.secrets[field.key] ?? { operation: "keep" };
      const oauthOwned = provider.oauthDefinition?.secretFieldKeys.includes(field.key);
      return <View key={field.key} style={styles.form}>
        <Text style={styles.title}>{field.label}{field.required ? " *" : ""}</Text>
        <Text style={styles.detail}>{draft.oauth?.secretPresent[field.key]
          ? "Authorization is ready in this draft. Save settings to retain it. The value is never shown."
          : draft.configuration.secretPresent[field.key] ? "A value is saved. It is never shown." : "No value is saved."}</Text>
        {oauthOwned ? <Text style={styles.detail}>Use provider authorization to obtain this value.</Text> : null}
        <View style={styles.row}>
          {(["keep", "replace", "clear"] as const).filter(action => !oauthOwned || action !== "replace").map(action => <AppButton key={action}
            label={action === "keep" ? "Keep saved" : action === "replace" ? "Replace" : "Clear"}
            accessibilityLabel={`${field.label}: ${action}`} compact
            variant={secret.operation === action ? "primary" : "secondary"} disabled={disabled}
            onPress={() => onSecret(field.key, action === "replace" ? { operation: action, value: "" } : { operation: action })} />)}
        </View>
        {!oauthOwned && secret.operation === "replace" ? <TextField label={`New ${field.label}`} secureTextEntry value={secret.value ?? ""}
          editable={!disabled} onChangeText={value => onSecret(field.key, { operation: "replace", value })} /> : null}
      </View>;
    }
    const value = draft.values[field.key] ?? "";
    if (field.kind === "boolean" && value === "") return <View key={field.key} style={styles.form}>
      <Text style={styles.title}>{field.label}{field.required ? " *" : ""}</Text>
      <Text style={styles.detail}>No value selected.</Text>
      <AppButton label={`${field.label}: false`} variant="secondary" disabled={disabled} onPress={() => onValue(field.key, "false")} />
      <AppButton label={`${field.label}: true`} variant="secondary" disabled={disabled} onPress={() => onValue(field.key, "true")} />
    </View>;
    if (field.kind === "boolean") return <SwitchRow key={field.key} title={field.label} disabled={disabled}
      value={value === "true"} onValueChange={next => onValue(field.key, String(next))} />;
    if (field.kind === "select") return <View key={field.key} style={styles.form}>
      <Text style={styles.title}>{field.label}{field.required ? " *" : ""}</Text>
      {!field.required ? <AppButton label="No selection" variant={value === "" ? "primary" : "secondary"}
        disabled={disabled} onPress={() => onValue(field.key, "")} /> : null}
      {(field.options ?? []).map(option => <AppButton key={option.value} label={option.label}
        variant={value === option.value ? "primary" : "secondary"} disabled={disabled}
        onPress={() => onValue(field.key, option.value)} />)}
    </View>;
    return <TextField key={field.key} label={`${field.label}${field.required ? " *" : ""}`} value={value}
      keyboardType={field.kind === "number" || field.kind === "integer" ? "numbers-and-punctuation" : "default"}
      editable={!disabled} onChangeText={next => onValue(field.key, next)} />;
  }

  if (!layout) return <>{provider.fields.map(fieldControl)}{provider.actions.map(renderAction)}</>;
  const step = layout.steps[Math.min(stepIndex, layout.steps.length - 1)]!;
  return <View style={styles.form}>
    <Text style={styles.detail}>Step {stepIndex + 1} of {layout.steps.length}</Text>
    <View style={styles.row}>{layout.steps.map((item, index) => <AppButton key={item.id} label={item.title}
      variant={index === stepIndex ? "primary" : "secondary"} disabled={disabled} onPress={() => setStepIndex(index)} />)}</View>
    <Text style={styles.title}>{step.title}</Text>
    {step.instructions ? <Text style={styles.detail}>{step.instructions}</Text> : null}
    {step.groups.filter(group => integrationConditionActive(group.activeWhen, values)).map(group => <View key={group.id} style={styles.form}>
      <Text style={styles.title}>{group.title}</Text>
      {group.instructions ? <Text style={styles.detail}>{group.instructions}</Text> : null}
      {group.fieldKeys.map(key => fieldControl(provider.fields.find(field => field.key === key)!))}
      {group.actions.map(renderAction)}
    </View>)}
    <View style={styles.row}>
      <AppButton label="Previous step" variant="secondary" disabled={disabled || stepIndex === 0} onPress={() => setStepIndex(index => index - 1)} />
      <AppButton label="Next step" variant="secondary" disabled={disabled || stepIndex === layout.steps.length - 1} onPress={() => setStepIndex(index => index + 1)} />
    </View>
  </View>;
}

const styles = StyleSheet.create({
  form: { gap: spacing.md },
  row: { flexDirection: "row", flexWrap: "wrap", gap: spacing.sm },
  title: { color: colors.text, fontSize: typography.body, fontWeight: "700" },
  detail: { color: colors.muted, fontSize: typography.caption }
});
