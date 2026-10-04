import assert from "node:assert/strict";
import { test } from "node:test";

test("the installed navigation parser and serializer preserve Unicode and reserved query characters", async () => {
  const { getStateFromPath } = await import(new URL("../node_modules/@react-navigation/core/lib/module/getStateFromPath.js", import.meta.url).href);
  const { getPathFromState } = await import(new URL("../node_modules/@react-navigation/core/lib/module/getPathFromState.js", import.meta.url).href);
  const configuration = { screens: { Settings: "settings" } };
  const params = { name: "Тест", proof: "one+two/three=", count: "42" };
  const state = getStateFromPath("/settings?name=%D0%A2%D0%B5%D1%81%D1%82&proof=one%2Btwo%2Fthree%3D&count=42", configuration);
  assert.deepEqual(state.routes[0].params, params);
  const serialized = getPathFromState(state, configuration);
  assert.deepEqual(getStateFromPath(serialized, configuration).routes[0].params, params);
});
