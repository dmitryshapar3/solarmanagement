import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { globals, uiHarness } from "./support/uiHarness";

test("Activity and Readings are sibling subpages with the correct selection and destination", async () => {
  const calls: unknown[][] = [];
  const nativeGlobals = globalThis as typeof globalThis & { __activityNavigate?: (...args: unknown[]) => void };
  nativeGlobals.__activityNavigate = (...args) => calls.push(args);
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  globals.__smartUi = { auth: { api: { sessionEpoch: 0, onSessionChange: () => () => {} }, isDemo: false }, resources: {} };
  const components = await uiHarness('export { ActivityScreen } from "./src/features/activity/ActivityScreen";export { ReadingsLogScreen } from "./src/features/readings/ReadingsLogScreen";', {
    stubComponents: true, resources: true,
    stubs: { "@react-navigation/native": 'export const useNavigation=()=>({navigate:(...args)=>globalThis.__activityNavigate(...args)});' }
  });
  let renderer: ReturnType<typeof create> | undefined;
  let header: ReturnType<typeof create> | undefined;
  try {
    await act(async () => { renderer = create(React.createElement(components.ActivityScreen!)); });
    const activity = renderer!.root.findAllByType("SegmentedControl").find(item => item.props.options.some((option: { label: string }) => option.label === "Readings"))!;
    assert.equal(activity.props.value, "automations");
    assert.deepEqual(activity.props.options.map((option: { label: string }) => option.label), ["Readings", "Automation"]);
    await act(async () => { activity.props.onChange("automations"); });
    assert.deepEqual(calls, [], "Selecting the current subpage preserves its filters and loaded records");
    await act(async () => { activity.props.onChange("readings"); });
    assert.deepEqual(calls.pop(), ["MainTabs", { screen: "Automations", params: { screen: "ReadingsLog" } }]);
    await act(async () => { renderer!.update(React.createElement(components.ReadingsLogScreen!)); });
    const list = renderer!.root.findByType("FlatList");
    await act(async () => { header = create(list.props.ListHeaderComponent); });
    assert.equal(header!.root.findByType("Header").props.title, "Activity");
    const readings = header!.root.findAllByType("SegmentedControl").find(item => item.props.options.some((option: { label: string }) => option.label === "Readings"))!;
    assert.equal(readings.props.value, "readings");
    await act(async () => { readings.props.onChange("readings"); });
    assert.deepEqual(calls, []);
    await act(async () => { readings.props.onChange("automations"); });
    assert.deepEqual(calls.pop(), ["MainTabs", { screen: "Automations", params: { screen: "Activity" } }]);
  } finally {
    await act(async () => { renderer?.unmount(); header?.unmount(); });
    delete nativeGlobals.__activityNavigate; delete globals.__smartUi; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
