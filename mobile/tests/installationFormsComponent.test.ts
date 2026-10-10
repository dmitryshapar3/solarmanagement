import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
import { createDemoState } from "../src/features/demo/fixtures";
import type { InstallationSettings } from "../src/features/settings/settingsResource";
import { globals, uiHarness } from "./support/uiHarness";

async function harness(name: "SolarSiteScreen" | "TariffExportScreen" | "DataRefreshScreen", bearing = 180, legacyPanelFields = false) {
  const fixture = createDemoState(new Date("2026-10-06T12:00:00Z")); let saved: InstallationSettings = { site: structuredClone(fixture.site), polling: { intervalSeconds: 30 }, display: { timeZoneId: "Europe/Warsaw" }, primaryInverterId: null, inverters: [], version: "revision-original", integrationVersions: {} };
  saved.site.solarEstimate.roof1Azimuth = bearing;
  if (legacyPanelFields) for (const key of ["roof1PanelCount", "roof2PanelCount", "roof1PanelsPerRow", "roof2PanelsPerRow"] as const) delete saved.site.solarEstimate[key];
  const puts: any[] = []; let header: { headerRight?: () => React.ReactElement<any> } = {};
  const navigation = { setOptions: (options: typeof header) => { header = options; }, dispatch() {}, navigate() {} };
  const client = new ApiClient({ baseUrl: "https://fixture.invalid", token: "fixture", transport: async (url, init) => {
    const route = new URL(url).pathname;
    if (route.endsWith("/permissions")) return new Response(JSON.stringify({ role: "Owner", permissions: ["Read", "ManageSettings", "ManageIntegrations"] }));
    assert.equal(route,"/api/settings/installation");
    if (init.method === "PUT") { const body = JSON.parse(init.body!); puts.push(body); saved = { ...saved, ...body, version: "revision-next" }; }
    return new Response(JSON.stringify(saved));
  } });
  globals.IS_REACT_ACT_ENVIRONMENT = true; globals.__smartUi = { auth: { api: new DeyeSolarApi(client), apiBaseUrl: "https://fixture.invalid", isDemo: false }, navigation } as any;
  const components = await uiHarness('export { SolarSiteScreen, TariffExportScreen, DataRefreshScreen } from "./src/features/settings/SettingsPages";', { stubComponents: true, stubs: {
    "@react-navigation/native": 'export const useNavigation=()=>globalThis.__smartUi.navigation;export const usePreventRemove=(enabled,callback)=>{globalThis.__smartUi.prevention={enabled,callback}};',
    "expo-location": 'export const Accuracy={Balanced:1};export const requestForegroundPermissionsAsync=async()=>({status:"denied"});export const getCurrentPositionAsync=async()=>{};',
    "expo-file-system": 'export const Paths={cache:"cache"};export class File{}',
    "expo-sharing": 'export const isAvailableAsync=async()=>false;export const shareAsync=async()=>{};',
    "AccountProofForm": 'export const AccountProofForm=()=>null;', "appleSignIn": 'export const appleIdentity=async()=>null;', "googleSignIn": 'export const googleSignIn=async()=>null;', "LanguageDropdown": 'export const LanguageDropdown=()=>null;'
  } });
  let renderer!: ReturnType<typeof create>; await act(async () => { renderer = create(React.createElement(components[name]!)); });
  return { renderer, puts, header: () => header.headerRight!(), field: (label: string, index = 0) => renderer.root.findAllByType("TextField").filter(item => item.props.label === label)[index]!,
    change: async (label: string, value: string, index = 0) => { await act(async () => { renderer.root.findAllByType("TextField").filter(item => item.props.label === label)[index]!.props.onChangeText(value); }); },
    preset: (label: string, index = 0) => renderer.root.findAllByType("Pressable").filter(item => item.props.accessibilityLabel?.endsWith(` ${label}`))[index]!,
    pressSave: async () => { await act(async () => { header.headerRight!().props.onPress(); }); },
    close: async () => { await act(async () => renderer.unmount()); delete globals.__smartUi; delete globals.IS_REACT_ACT_ENVIRONMENT; }
  };
}

test("solar compass presets and numeric fields stay bound; header saves the guarded aggregate and discard restores it", async () => {
  const h = await harness("SolarSiteScreen"); try {
    assert.equal(h.header().props.label,"Save"); assert.equal(h.header().props.disabled,true);
    assert.equal(h.renderer.root.findAllByType("button").some(item => item.props.label === "Save changes"),false);
    await h.change("Compass bearing · degrees","120");
    assert.equal(h.header().props.disabled,false); assert.equal(h.field("Compass bearing · degrees").props.value,"120");
    await act(async () => { h.preset("W").props.onPress(); });
    assert.equal(h.field("Compass bearing · degrees").props.value,"270"); assert.equal(h.preset("W").props.accessibilityState.selected,true);
    assert.equal(h.renderer.root.findAllByProps({testID:"compass-needle"})[0]!.props.x2,50);
    await h.pressSave(); assert.equal(h.puts.length,1); assert.equal(h.puts[0].site.solarEstimate.roof1Azimuth,270);
    assert.equal(h.puts[0].expectedVersion,"revision-original"); assert.deepEqual(h.puts[0].expectedIntegrationVersions,{});
    assert.ok(h.puts[0].polling && h.puts[0].display); assert.equal(h.header().props.disabled,true);
    await h.change("Compass bearing · degrees","180");
    await act(async () => { h.renderer.root.findAllByType("button").find(item => item.props.label === "Discard changes")!.props.onPress(); });
    assert.equal(h.field("Compass bearing · degrees").props.value,"270"); assert.equal(h.header().props.disabled,true);
  } finally { await h.close(); }
});

test("header save rejects invalid site and polling edits even through a captured enabled callback", async () => {
  for (const [screen, label, invalid] of [["SolarSiteScreen","Compass bearing · degrees","360"],["DataRefreshScreen","Custom interval","30.5"]] as const) {
    const h = await harness(screen); try {
      await h.change(label,"90"); const prior = h.header().props.onPress; assert.equal(h.header().props.disabled,false);
      await h.change(label,invalid); assert.equal(h.header().props.disabled,true); assert.ok(h.field(label).props.error);
      await act(async () => { prior(); }); await h.pressSave(); assert.equal(h.puts.length,0);
    } finally { await h.close(); }
  }
});

test("tariff header validates calendar date and timezone while keeping an atomic guarded save", async () => {
  const h = await harness("TariffExportScreen"); try {
    await h.change("Contract start date","2026-02-30"); assert.equal(h.header().props.disabled,true); assert.ok(h.field("Contract start date").props.error); await h.pressSave(); assert.equal(h.puts.length,0);
    await h.change("Contract start date","2026-02-28"); await h.change("Export time zone","Unknown/Zone"); assert.equal(h.header().props.disabled,true); assert.ok(h.field("Export time zone").props.error);
    await h.change("Export time zone","Europe/Warsaw"); assert.equal(h.header().props.disabled,false); await h.pressSave(); assert.equal(h.puts.length,1); assert.equal(h.puts[0].site.solarSales.contractStartDate,"2026-02-28"); assert.equal(h.puts[0].expectedVersion,"revision-original");
  } finally { await h.close(); }
});

test("stored bearing360 displays north without changing it; an explicit north preset saves zero", async () => {
  const h = await harness("SolarSiteScreen",360); try {
    assert.equal(h.field("Compass bearing · degrees").props.value,"360"); assert.equal(h.field("Compass bearing · degrees").props.error,null); assert.equal(h.preset("N").props.accessibilityState.selected,true); assert.equal(h.puts.length,0);
    await act(async () => { h.preset("N").props.onPress(); }); assert.equal(h.field("Compass bearing · degrees").props.value,"0"); await h.pressSave(); assert.equal(h.puts[0].site.solarEstimate.roof1Azimuth,0);
  } finally { await h.close(); }
});


test("tariff saves exact manual price, rejects excess precision, and preserves it when choosing a feed", async () => {
  const h = await harness("TariffExportScreen"); try {
    const select = async (label: string) => { await act(async () => h.renderer.root.findAllByType("button").find(item => item.props.label === label)!.props.onPress()); };
    await select("Fixed price"); await h.change("Sale price", "0.123456");
    assert.equal(h.header().props.disabled, false); const captured = h.header().props.onPress;
    await h.change("Sale price", "0.1234567"); assert.equal(h.header().props.disabled, true);
    await act(async () => captured()); assert.equal(h.puts.length, 0);
    await h.change("Sale price", "0.123456"); await h.pressSave();
    assert.equal(h.puts[0].site.solarSales.priceSource, "manual"); assert.equal(h.puts[0].site.solarSales.manualPricePlnPerKwh, 0.123456);
    await select("CSV / XML feed"); await h.change("Price feed URL", "http://localhost/prices.csv");
    assert.equal(h.header().props.disabled, true); assert.ok(h.field("Price feed URL").props.error);
    await h.change("Price feed URL", "https://prices.example.com/feed.xml"); await h.pressSave();
    assert.equal(h.puts[1].site.solarSales.priceSource, "feed"); assert.equal(h.puts[1].site.solarSales.priceFeedUrl, "https://prices.example.com/feed.xml");
    assert.equal(h.puts[1].site.solarSales.manualPricePlnPerKwh, 0.123456);
  } finally { await h.close(); }
});

test("panel count drafts redraw exact modules, validate rows, and atomically save both roofs", async () => {
  const h = await harness("SolarSiteScreen"); try {
    assert.equal(h.field("Panel count").props.value, "8"); assert.equal(h.field("Panels per row").props.value, "4");
    await h.change("Panel count", "17"); const captured = h.header().props.onPress;
    await h.change("Panels per row", "18"); assert.equal(h.header().props.disabled, true); assert.ok(h.field("Panels per row").props.error);
    await act(async () => captured()); assert.equal(h.puts.length, 0);
    await h.change("Panels per row", "6"); await h.change("Panel count", "1.5", 1); assert.equal(h.header().props.disabled, true); assert.ok(h.field("Panel count", 1).props.error);
    await h.change("Panel count", "7", 1); await h.change("Panels per row", "0", 1); await h.pressSave();
    assert.equal(h.puts.length, 1); const saved = h.puts[0].site.solarEstimate;
    assert.deepEqual([saved.roof1PanelCount, saved.roof1PanelsPerRow, saved.roof2PanelCount, saved.roof2PanelsPerRow], [17, 6, 7, 0]);
    assert.equal(saved.roof1Kwp, 3.5); assert.equal(saved.roof1Tilt, 25); assert.equal(saved.roof1Azimuth, 180);
    assert.equal(h.puts[0].expectedVersion, "revision-original");
    assert.equal(h.renderer.root.findAll(node => typeof node.props.testID === "string" && node.props.testID.startsWith("roof-sun-panel-1-")).length, 17);
  } finally { await h.close(); }
});

test("legacy solar settings stay unknown and omitted during unrelated edits until panel count is explicitly entered", async () => {
  const h = await harness("SolarSiteScreen", 180, true); try {
    assert.equal(h.field("Panel count").props.value, ""); assert.equal(h.field("Panels per row").props.value, "0");
    assert.equal(h.renderer.root.findAll(node => typeof node.props.testID === "string" && node.props.testID.startsWith("roof-sun-panel-")).length, 0);
    await h.change("Site name", "Updated site"); await h.pressSave();
    const saved = h.puts[0].site.solarEstimate;
    for (const key of ["roof1PanelCount", "roof2PanelCount", "roof1PanelsPerRow", "roof2PanelsPerRow"]) assert.equal(Object.hasOwn(saved, key), false);
    await h.change("Panel count", "0"); await h.pressSave(); assert.equal(h.puts[1].site.solarEstimate.roof1PanelCount, 0);
  } finally { await h.close(); }
});

test("house-direction controls keep fractional roof separation and change only the unsaved draft until Save", async () => {
  const h = await harness("SolarSiteScreen"); try {
    await h.change("Compass bearing · degrees", "349.5"); await h.change("Compass bearing · degrees", "169.25", 1);
    const beforePanels = [h.field("Panel count").props.value, h.field("Panel count", 1).props.value];
    await act(async () => { h.renderer.root.findByProps({ accessibilityLabel: "Set house direction" }).props.onPress(); });
    const captured = h.renderer.root.findByProps({ accessibilityLabel: "Rotate sun path right" }).props.onPress;
    await act(async () => { captured(); captured(); });
    assert.equal(h.puts.length, 0); assert.equal(h.field("Compass bearing · degrees").props.value, "9.5"); assert.equal(h.field("Compass bearing · degrees", 1).props.value, "189.25");
    assert.equal(h.field("Compass bearing · degrees").props.error, null); assert.equal(h.header().props.disabled, false);
    assert.deepEqual([h.field("Panel count").props.value, h.field("Panel count", 1).props.value], beforePanels);
    await h.pressSave(); assert.equal(h.puts.length, 1);
    assert.deepEqual([h.puts[0].site.solarEstimate.roof1Azimuth, h.puts[0].site.solarEstimate.roof2Azimuth], [9.5, 189.25]);
    assert.equal(h.puts[0].expectedVersion, "revision-original");
    await h.change("Compass bearing · degrees", "359.5"); assert.equal(h.field("Compass bearing · degrees").props.error, null); assert.equal(h.header().props.disabled, false);
    await h.change("Compass bearing · degrees", "360"); assert.ok(h.field("Compass bearing · degrees").props.error); assert.equal(h.header().props.disabled, true);
  } finally { await h.close(); }
});
