import assert from "node:assert/strict";
import { test } from "node:test";
import React from "react";
import { act, create, type ReactTestInstance } from "react-test-renderer";
import type { ActivityCheck, ActivityFeed, ReadingDetailsRow } from "../src/core/api/redesignTypes";
import { globals, uiHarness } from "./support/uiHarness";

function metric(card: ReactTestInstance, label: string) {
 const point=card.findAllByType("View").find(view=>{const text=view.findAllByType("Text");return text.length===2 && text[0]?.props.children===label;});
 assert.ok(point,`metric ${label}`);return point.findAllByType("Text")[1]!.props.children;
}
const auth={api:{sessionEpoch:0,onSessionChange:()=>()=>{}},isDemo:false};
const settle=async()=>{for(let i=0;i<5;i++) await act(async()=>{await Promise.resolve()});};
test("actual Activity expands recorded checks with unavailable SOC/PV distinct from measured zero and does not parse reason text",async()=>{
 const at="2026-10-05T12:00:00Z";
 const check:ActivityCheck={id:1,occurredAt:at,kind:"rule.checked",reasonCode:"All measurements are zero",state:null,batterySoc:null,solarWatts:null,configurationVersion:null,generation:null};
 const feed:ActivityFeed={start:at,end:at,nextCursor:null,summary:{switches:0,confirmedCommands:0,onSeconds:null,knownSeconds:0,expectedSeconds:3600,partial:true},items:[{id:1,start:at,end:at,kind:"rule.checked",reasonCode:null,ruleId:1,ruleName:"Unavailable reading",deviceId:null,state:null,checkCount:2,socMin:null,socMax:null,solarMinWatts:null,solarMaxWatts:null,actorUserId:null,client:null}]};
 globals.IS_REACT_ACT_ENVIRONMENT=true;globals.__smartUi={auth,resources:{"activity-rules":{data:[]},"activity:24:all:all":{data:feed},"activity-group:1":{data:{items:[check,{...check,id:2,reasonCode:"MeasurementUnavailable",batterySoc:0,solarWatts:0}],nextCursor:null}}}};
 const {ActivityScreen}=await uiHarness('export {ActivityScreen} from "./src/features/activity/ActivityScreen";',{stubComponents:true,resources:true});let renderer:ReturnType<typeof create>|undefined;
 try {await act(async()=>{renderer=create(React.createElement(ActivityScreen!))});await settle();const row=renderer!.root.findAllByType("Pressable").find(row=>row.findAllByType("Text").some(text=>text.props.children==="Conditions checked"))!;await act(async()=>row.props.onPress());
 const values=renderer!.root.findAllByType("DataRow");assert.deepEqual(values.filter(row=>row.props.label==="Battery charge").map(row=>row.props.value),["—","0%"]);assert.deepEqual(values.filter(row=>row.props.label==="Solar power").map(row=>row.props.value),["—","0.00 kW"]);
 }finally{await act(async()=>renderer?.unmount());delete globals.__smartUi;delete globals.IS_REACT_ACT_ENVIRONMENT;}
});
test("actual virtualized ReadingsLog keeps nullable metrics as dashes and explicitly measured zeros visible",async()=>{
 const base:ReadingDetailsRow={id:1,timestamp:"2026-10-05T12:00:00Z",inverterId:"inverter",configurationRevision:1,runtimeGeneration:1,dataSource:"Integration",solarObservedAt:null,batterySoc:null,solarProduction:null,batteryPower:null,gridConsumption:null,loadPower:null,batteryVoltage:null,batteryTemperature:null,batteryCurrent:null};
 const zero={...base,id:2,batterySoc:0,solarProduction:0,batteryPower:0,gridConsumption:0,loadPower:0,batteryVoltage:0,batteryTemperature:0,batteryCurrent:0};
 globals.IS_REACT_ACT_ENVIRONMENT=true;globals.__smartUi={auth,resources:{"readings:6:5m":{data:{start:base.timestamp,end:base.timestamp,items:[base,zero],gaps:[],nextCursor:null,partial:false}}}};
 const {ReadingsLogScreen}=await uiHarness('export {ReadingsLogScreen} from "./src/features/readings/ReadingsLogScreen";',{stubComponents:true,resources:true});let renderer:ReturnType<typeof create>|undefined;
 try{await act(async()=>{renderer=create(React.createElement(ReadingsLogScreen!))});const list=renderer!.root.findByType("FlatList");assert.equal(list.props.initialNumToRender,5);assert.equal(list.props.windowSize,5);
 let cards:ReturnType<typeof create>|undefined;await act(async()=>{cards=create(React.createElement(React.Fragment,null,...list.props.data.map((item:ReadingDetailsRow)=>React.createElement(React.Fragment,{key:item.id},list.props.renderItem({item})))))});
 const rows=cards!.root.findAllByType("Card");for(const label of ["SOC","Solar","Grid","Battery","Load","Voltage","Temperature","Current"])assert.equal(metric(rows[0]!,label),"—");assert.equal(metric(rows[1]!,"SOC"),"0%");for(const label of ["Solar","Grid","Battery","Load"])assert.equal(metric(rows[1]!,label),"0 W");assert.equal(metric(rows[1]!,"Voltage"),"0.0 V");assert.equal(metric(rows[1]!,"Temperature"),"0.0 °C");assert.equal(metric(rows[1]!,"Current"),"0.0 A");await act(async()=>cards?.unmount());
 }finally{await act(async()=>renderer?.unmount());delete globals.__smartUi;delete globals.IS_REACT_ACT_ENVIRONMENT;}
});
