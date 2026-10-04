import type { IntegrationConfiguration, IntegrationProvider } from "../../src/core/api/IntegrationApi";

export function integrationFixture(): { provider: IntegrationProvider; configuration: IntegrationConfiguration } {
  const provider: IntegrationProvider = {
    providerId: "new-manufacturer", packageVersion: "1.0.0", packageDigest: "package-a", descriptorDigest: "schema-a",
    displayName: "New manufacturer", uiContractVersion: 1, configurationVersion: 1, requiredUiFeatures: ["text", "secret", "integer"],
    fields: [
      { key: "region", kind: "text", label: "Region", required: true, secret: false, options: [], defaultValue: "eu" },
      { key: "token", kind: "secret", label: "API key", required: true, secret: true, options: [] },
      { key: "interval", kind: "integer", label: "Interval", required: true, secret: false, minimum: 5, maximum: 60, options: [], defaultValue: 10 },
      { key: "mode", kind: "select", label: "Mode", required: true, secret: false, options: [{ value: "cloud", label: "Cloud" }] },
      { key: "readOnly", kind: "boolean", label: "Read only", required: true, secret: false, options: [] }
    ], actions: ["test", "discover"]
  };
  const configuration: IntegrationConfiguration = {
    instance: { id: "instance-a", providerId: provider.providerId, name: "My equipment", status: "disabled", revision: 7, generation: 3,
      packageVersion: provider.packageVersion, packageDigest: provider.packageDigest, descriptorDigest: provider.descriptorDigest },
    values: { region: "eu", interval: 10, mode: "cloud", readOnly: false }, secretPresent: { token: true }
  };
  return { provider, configuration };
}
