import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create } from "react-test-renderer";
import { homeSunState, homeUsesBattery } from "../src/features/dashboard/homePolicy";
import { demoInverter, createDemoState, demoProduction } from "../src/features/demo/fixtures";
import { zonedDate } from "../src/features/energy/chartPolicy";
import { globals, uiHarness } from "./support/uiHarness";

test("Home astronomy handles before dawn and sunset boundaries without deriving night from missing data", () => {
  const data = { date: "2026-10-06", today: "2026-10-06", sunrise: "2026-10-06T06:00:00Z", sunset: "2026-10-06T18:00:00Z", nextSunrise: "2026-10-07T06:01:00Z" };
  assert.deepEqual(homeSunState(data,Date.parse("2026-10-06T02:00:00Z")),{night:true,nextSunrise:data.sunrise});
  assert.equal(homeSunState(data,Date.parse(data.sunrise)).night,false);
  assert.deepEqual(homeSunState(data,Date.parse(data.sunset)),{night:true,nextSunrise:data.nextSunrise});
  assert.equal(homeSunState({...data,sunrise:null},Date.parse("2026-10-06T02:00:00Z")).night,false);
  assert.equal(homeSunState({...data,sunset:null},Date.parse("2026-10-06T20:00:00Z")).night,false);
  assert.equal(homeSunState({...data,nextSunrise:null},Date.parse("2026-10-06T20:00:00Z")).night,false);
  assert.equal(homeSunState({...data,date:"2026-10-05"},Date.parse("2026-10-06T02:00:00Z")).night,false);
  assert.equal(homeSunState(null).night,false);
});

test("the battery night sentence requires current observation provenance rather than a fresh poll alone", () => {
  const now = Date.parse("2026-10-06T02:00:00Z");
  const reading = { ...demoInverter(new Date(now),"UTC"), solarProduction:0, gridConsumption:0, batteryPower:900, loadPower:900 };
  assert.equal(homeUsesBattery(reading,now),true);
  for (const change of [{ solarObservedAt:"2026-10-06T01:40:00Z" }, {gridObservedAt:"2026-10-06T01:40:00Z"}, {solarObservedAt:null}, {gridDeviceSn:null}, {solarDeviceSn:"other"}, {inverterId:null}, {solarObservedAt:"2026-10-06T02:01:00Z"}, {gridConsumption:100}, {batteryPowerValid:false}, {loadPowerValid:false}, {solarProduction:-1}, {batteryPower:NaN}]) assert.equal(homeUsesBattery({...reading,...change},now),false,JSON.stringify(change));
});

test("Home renders provisional current-hour export separately from completed totals", async () => {
  const now = new Date(); const state = createDemoState(now); const date = zonedDate(now,"UTC");
  const currentHour = { start:now.toISOString(),observedThrough:now.toISOString(),exportKwh:3.5,energyValuePln:2.25,creditedExportKwh:3.5,estimatedDepositPln:2.77,observedSeconds:900 };
  const permissions = ["Read"]; const dashboard = { inverter:null,devices:[],rules:[],timeZoneId:"UTC" };
  globals.IS_REACT_ACT_ENVIRONMENT=true; globals.__smartUi={auth:{api:{sessionEpoch:0,socketCommands:{}},isDemo:true,username:"Fixture"},resources:{
    "home-dashboard":{data:dashboard,loading:false,error:null},"home-production":{data:demoProduction("Today",undefined,now,"UTC"),loading:false,error:null},"home-estimate":{data:null,loading:false,error:null},"home-activity":{data:null,loading:false,error:null},[`home-export:${date}`]:{data:{exportKwh:10,energyValuePln:7.5,currentHour,isPartial:false},loading:false,error:null},"home-setup:":{data:{site:state.site,sources:[],details:new Map(),permissions},loading:false,error:null}
  }};
  let renderer:ReturnType<typeof create>|undefined;
  try {
    const {DashboardScreen}=await uiHarness('export { DashboardScreen } from "./src/features/dashboard/DashboardScreen";',{stubComponents:true,resources:true});
    await act(async()=>{renderer=create(React.createElement(DashboardScreen!));});
    const title=renderer!.root.findAllByType("SectionTitle").find(item=>item.props.title==="Current hour · in progress");assert.ok(title);
    const texts=renderer!.root.findAllByType("Text").map(item=>(Array.isArray(item.props.children)?item.props.children:[item.props.children]).filter(child=>typeof child==="string").join(""));
    assert.ok(texts.includes("10.00 kWh")); assert.ok(texts.includes("7.50 PLN"));assert.ok(texts.includes("3.50 kWh · 2.25 PLN"));assert.ok(texts.some(text=>text.includes("Not in totals")));assert.ok(!texts.includes("13.50 kWh"));
  } finally {await act(async()=>renderer?.unmount());delete globals.__smartUi;delete globals.IS_REACT_ACT_ENVIRONMENT;}
});
