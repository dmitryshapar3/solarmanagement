import assert from "node:assert/strict";
import { test } from "node:test";
import { RefreshGroup } from "../src/features/energy/RefreshGroup";

test("a failed member cannot release the refresh group while a sibling still updates its resource", async () => {
  const group = new RefreshGroup();
  let finish!: () => void;
  const slow = new Promise<void>(resolve => { finish = resolve; });
  let calls = 0;
  const first = group.run(false, [async () => { calls++; throw new Error("Weather unavailable"); }, () => slow]);
  const expectedFailure = assert.rejects(first, /Weather unavailable/);
  await Promise.resolve(); await Promise.resolve();
  assert.equal(await group.run(false, [async () => { calls++; }]), false);
  assert.equal(calls, 1);
  finish(); await expectedFailure;
  assert.equal(await group.run(false, [async () => { calls++; }]), true);
  assert.equal(calls, 2);
});
