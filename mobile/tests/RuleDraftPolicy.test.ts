import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import type { RuleRequest } from "../src/core/api/types";
import { normalizeRuleDraft, validateRuleDraft } from "../src/features/rules/RuleDraftPolicy";
import { DemoApiClient } from "../src/features/demo/DemoApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";

const vectors = JSON.parse(readFileSync(path.join(process.cwd(), "../tests/fixtures/rule-configuration-vectors.json"), "utf8")) as {
  name: string; input: RuleRequest; expectedNormalization: Pick<RuleRequest, "enabled" | "socTurnOffThreshold" | "minAverageSolarProductionWatts">;
  expectedError: string | null;
}[];
for (const vector of vectors) test(`shared rule configuration: ${vector.name}`, () => {
  const normalized = normalizeRuleDraft(vector.input);
  for (const [key, value] of Object.entries(vector.expectedNormalization)) assert.equal(normalized[key as keyof RuleRequest], value);
  assert.equal(validateRuleDraft(normalized)?.code ?? null, vector.expectedError);
});

test("demo CRUD applies the current defaults and strips caller-supplied runtime state", async () => {
  const api = new DeyeSolarApi(new DemoApiClient(() => new Date("2026-10-05T11:00:00Z")));
  const original = (await api.getRules())[0]!;
  const target = (await api.getDevices()).devices.find(device => device.stateKnown && !device.isOn)!;
  assert.ok(target, "The spoofed ON state must contradict an actually observed-OFF target");
  const draft = { ...original, entityId: target.id, name: "Draft", useSeparateSocTurnOffThreshold: false,
    socTurnOffThreshold: 99, minAverageSolarProductionWatts: 0, currentState: true };
  const projected = normalizeRuleDraft(draft);
  for (const field of ["id", "currentState", "currentStateChangedAt", "lastEvaluated"])
    assert.equal(field in projected, false, `Draft cannot contain runtime field ${field}`);
  const created = await api.createRule(draft);
  assert.equal(created.socTurnOffThreshold, original.socTurnOnThreshold);
  assert.equal(created.minAverageSolarProductionWatts, 3000);
  assert.notEqual(created.configurationVersion, original.configurationVersion);
  assert.equal(created.currentState, false);
  await assert.rejects(api.createRule({ ...draft, cooldownMinutes: 241 }), error =>
    typeof error === "object" && error !== null && "status" in error && error.status === 400);
});

test("the target and HH:mm requirements remain explicit UI adapters", () => {
  const normalized = normalizeRuleDraft({ ...vectors[0]!.input, entityId: " " });
  assert.equal(normalized.enabled, false);
  assert.equal(validateRuleDraft(normalized), null);
  assert.equal(validateRuleDraft(normalized, true)?.code, "target_required");
  assert.equal(validateRuleDraft({ ...normalized, activeFrom: "24:00", activeTo: "08:00" })?.code, "time_window_format");
});
