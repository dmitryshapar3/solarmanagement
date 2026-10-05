import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { ScopedActionScope } from "../src/application/ScopedActionScope";
import { useScopedAction } from "../src/application/useScopedAction";

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  return { promise: new Promise<void>((yes, no) => { resolve = yes; reject = no; }), resolve: () => resolve(), reject: (error: Error) => reject(error) };
}

test("a stale identity cannot publish a late result or clear the replacement action's pending guard", async () => {
  let epoch = 1;
  const scope = new ScopedActionScope(() => epoch, () => true); scope.activate();
  const old = deferred(), current = deferred();
  const events: string[] = [];
  const first = scope.run(async context => { await old.promise; context.publish(() => events.push("old-data")); }, { finished: () => events.push("old-finished") });
  epoch++; scope.invalidate();
  const second = scope.run(async context => { await current.promise; context.publish(() => events.push("new-data")); }, { finished: () => events.push("new-finished") });
  old.resolve(); await first;
  assert.deepEqual(events, []);
  assert.equal(await scope.run(async () => assert.fail("No overlapping replacement action")), false);
  current.resolve(); await second;
  assert.deepEqual(events, ["new-data", "new-finished"]);
});

test("disposing a scope suppresses a delayed failure without retrying a dispatched mutation", async () => {
  const scope = new ScopedActionScope(() => 1, () => true); scope.activate();
  const request = deferred(); let dispatches = 0, errors = 0; let signal: AbortSignal | undefined;
  const pending = scope.run(async context => { signal = context.signal; dispatches++; await request.promise; }, { failed: () => errors++ });
  scope.dispose();
  assert.equal(signal?.aborted, true);
  request.reject(new Error("Late transport failure")); await pending;
  assert.equal(errors, 0); assert.equal(dispatches, 1);
  assert.equal(await scope.run(async () => { dispatches++; }), false);
});

test("explicit authorization cancellation retains its pending guard until cleanup settles and cannot publish the result", async () => {
  const scope = new ScopedActionScope(() => 1, () => true); scope.activate();
  const request = deferred(); let published = 0, finished = 0;
  const pending = scope.run(async context => { await request.promise; context.publish(() => published++); }, { finished: () => finished++ });
  scope.cancel();
  assert.equal(await scope.run(async () => assert.fail("Authorization cleanup still owns its guard")), false);
  request.resolve(); await pending;
  assert.equal(published, 0); assert.equal(finished, 1);
  assert.equal(await scope.run(async () => {}), true);
});

test("the real hook resets busy on session revocation and fences callbacks across identity replacement", async () => {
  const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const observers = new Set<() => void>();
  const session = { sessionEpoch: 1, onSessionChange: (observer: () => void) => { observers.add(observer); return () => { observers.delete(observer); }; } };
  let actions!: ReturnType<typeof useScopedAction>; let resets = 0, published = 0;
  function Probe({ identity }: { identity: string }) { actions = useScopedAction(session, identity, () => resets++); return null; }
  let renderer: ReturnType<typeof create> | undefined;
  try {
    await act(async () => { renderer = create(React.createElement(Probe, { identity: "owner" })); });
    const old = deferred(); let first!: Promise<boolean>;
    await act(async () => { first = actions.run("old", async context => { await old.promise; context.publish(() => published++); }); });
    assert.equal(actions.busy, "old");
    await act(async () => { session.sessionEpoch++; for (const observer of observers) observer(); });
    assert.equal(actions.busy, null); assert.equal(resets, 2);
    await act(async () => { renderer!.update(React.createElement(Probe, { identity: "replacement" })); });
    await act(async () => { old.resolve(); await first; });
    assert.equal(published, 0); assert.equal(actions.busy, null);
  } finally { if (renderer) await act(async () => renderer!.unmount()); delete globals.IS_REACT_ACT_ENVIRONMENT; }
  assert.equal(observers.size, 0);
});
