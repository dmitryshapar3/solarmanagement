import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import type { IntegrationField, IntegrationUiCondition, IntegrationValue } from "../src/core/api/IntegrationApi";
import { integrationFixture } from "./support/integrationFixture";
import { applyIntegrationOAuth, createIntegrationDraft, integrationChange, integrationPublicValues, unsupportedProvider } from "../src/features/integrations/integrationDraft";
import { integrationConditionActive, integrationFieldActive } from "../src/features/integrations/integrationUi";

const fixtures = JSON.parse(readFileSync(path.join(process.cwd(), "../tests/shared/integration-ui-conditions.json"), "utf8")) as {
  conditions: { name: string; condition: IntegrationUiCondition; values: Record<string, IntegrationValue>; expected: boolean }[];
  fields: { name: string; fields: IntegrationField[]; groupCondition?: IntegrationUiCondition; fieldKey: string; values: Record<string, IntegrationValue>; expected: boolean }[];
};
for (const fixture of fixtures.conditions) test(`shared scalar condition: ${fixture.name}`, () => {
  assert.equal(integrationConditionActive(fixture.condition, fixture.values), fixture.expected);
});
for (const fixture of fixtures.fields) test(`shared conditional field: ${fixture.name}`, () => {
  const { provider } = integrationFixture();
  provider.fields = fixture.fields;
  if (fixture.groupCondition) provider.uiLayout = { version: 1, steps: [{ id: "setup", title: "Setup", groups: [{
    id: "conditional", title: "Conditional", fieldKeys: [fixture.fieldKey], actions: [], activeWhen: fixture.groupCondition
  }] }] };
  assert.equal(integrationFieldActive(provider, provider.fields.find(field => field.key === fixture.fieldKey)!, fixture.values), fixture.expected);
});

test("conditional required fields retain inactive values and still reject invalid supplied inactive types", () => {
  const { provider, configuration } = integrationFixture();
  const interval = provider.fields.find(field => field.key === "interval")!;
  interval.activeWhen = { field: "mode", operator: "eq", value: "cloud" };
  provider.fields.find(field => field.key === "mode")!.options!.push({ value: "local", label: "Local" });
  provider.requiredUiFeatures.push("conditional-fields");
  const draft = createIntegrationDraft(provider, configuration);
  draft.values.mode = "local";
  assert.equal(integrationChange(draft).values.interval, 10);
  draft.values.interval = "";
  assert.equal(integrationChange(draft).values.interval, null);
  draft.values.interval = "5.5";
  assert.throws(() => integrationChange(draft), /Interval/);
  draft.values.interval = "10";
  draft.values.mode = "cloud";
  draft.values.interval = "";
  assert.throws(() => integrationChange(draft), /Interval is required/);
  assert.equal(configuration.values.interval, 10);
  assert.equal(configuration.values.mode, "cloud");
});

test("explicit null overrides descriptor defaults and missing required boolean is never guessed false", () => {
  const { provider, configuration } = integrationFixture();
  configuration.values.region = null;
  provider.fields.find(field => field.key === "region")!.required = false;
  provider.fields.find(field => field.key === "token")!.activeWhen = { field: "region", operator: "eq", value: "eu" };
  configuration.secretPresent.token = false;
  const draft = createIntegrationDraft(provider, configuration);
  assert.equal(draft.values.region, "");
  assert.equal(integrationChange(draft).values.region, null);
  delete configuration.values.readOnly;
  delete provider.fields.find(field => field.key === "readOnly")!.defaultValue;
  const missing = createIntegrationDraft(provider, configuration);
  assert.equal(Object.hasOwn(integrationPublicValues(missing), "readOnly"), false);
  assert.throws(() => integrationChange(missing), /Read only is required/);
  missing.values.readOnly = "false";
  assert.equal(integrationChange(missing).values.readOnly, false);
});

test("unsupported layout versions, unknown group actions and secret conditions safely prevent mutations", () => {
  const { provider, configuration } = integrationFixture();
  provider.uiLayout = { version: 2, steps: [] };
  assert.match(unsupportedProvider(provider)!, /newer settings layout/);
  assert.throws(() => integrationChange(createIntegrationDraft(provider, configuration)), /newer settings layout/);
  provider.uiLayout = { version: 1, steps: [{ id: "one", title: "One", groups: [{ id: "all", title: "All",
    fieldKeys: provider.fields.map(field => field.key), actions: ["executable-custom-action"] }] }] };
  assert.match(unsupportedProvider(provider)!, /unsupported settings groups/);
  provider.uiLayout.steps[0]!.groups[0]!.actions = [];
  provider.fields[0]!.activeWhen = { field: "token", operator: "present" };
  assert.match(unsupportedProvider(provider)!, /unsupported conditional settings/);
  assert.equal(configuration.secretPresent.token, true);
});

test("valid prototype-named public and secret field keys survive defaults, conditions and OAuth without changing neighboring objects", () => {
  const { provider, configuration } = integrationFixture();
  provider.fields.find(field => field.key === "region")!.key = "__proto__";
  configuration.values = JSON.parse('{"__proto__":"eu","interval":10,"mode":"cloud","readOnly":false}');
  provider.fields.find(field => field.key === "token")!.activeWhen = { field: "__proto__", operator: "eq", value: "eu" };
  const draft = createIntegrationDraft(provider, configuration);
  assert.equal(Object.hasOwn(draft.values, "__proto__"), true);
  assert.equal(draft.values.__proto__, "eu");
  assert.equal(integrationChange(draft).values.__proto__, "eu");
  delete configuration.values.__proto__;
  assert.equal(integrationChange(createIntegrationDraft(provider, configuration)).values.__proto__, "eu");
  const other = integrationFixture();
  other.provider.fields.find(field => field.key === "token")!.key = "__proto__";
  other.provider.actions.push("oauth"); other.provider.requiredUiFeatures.push("oauth");
  other.provider.oauthDefinition = { secretFieldKeys: ["__proto__"] };
  other.configuration.secretPresent = JSON.parse('{"__proto__":true}');
  const secretDraft = createIntegrationDraft(other.provider, other.configuration);
  assert.deepEqual(integrationChange(secretDraft).secretOperations.__proto__, { operation: "keep" });
  const authorized = applyIntegrationOAuth(secretDraft, { flowId: "prototype-flow", status: "ready", expiresAt: "2099-01-01T00:00:00Z",
    values: {}, secretPresent: JSON.parse('{"__proto__":true}') });
  assert.equal(authorized.oauth!.secretPresent.__proto__, true);
  assert.equal(Object.getPrototypeOf(integrationChange(authorized).secretOperations), Object.prototype);
  assert.equal(Object.hasOwn({}, "operation"), false);
  assert.equal(configuration.values.interval, 10);
  assert.equal(other.configuration.secretPresent.__proto__, true);
});
