import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";
type Native = { auth: { api: DeyeSolarApi; apiBaseUrl: string; isDemo: boolean; logout(): Promise<void> }; alerts: { buttons: { text: string; onPress?(): void }[] }[] };
const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __accountPages?: Native };
async function screens() {
  const bundle = await build({ stdin: { contents: 'export { EditProfileScreen, PasswordSessionsScreen, ChangePasswordScreen, SignInSecurityScreen } from "./src/features/settings/SettingsPages";', resolveDir: process.cwd(), loader: "ts" }, bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"], plugins: [{ name: "account-boundaries", setup(builder) {
    builder.onResolve({ filter: /^(react-native|@react-navigation\/native|expo-location|expo-file-system|expo-sharing|lucide-react-native)$|(?:^|\/)(AuthContext|LanguageContext|core\/components|ThemeProvider|AccountProofForm|appleSignIn|googleSignIn|LanguageDropdown|CompassBearing|RoofSunDiagram)$/ }, args => ({ path: args.path, namespace: "account" }));
    builder.onLoad({ filter: /.*/, namespace: "account" }, args => ({ loader: "js", contents:
      args.path === "react-native" ? 'export const View="View"; export const Alert={alert:(title,message,buttons)=>globalThis.__accountPages.alerts.push({buttons})};'
      : args.path === "@react-navigation/native" ? 'export const useNavigation=()=>({navigate(){},dispatch(){}}),usePreventRemove=()=>{};'
      : args.path.endsWith("AuthContext") ? 'export const useAuth=()=>globalThis.__accountPages.auth;'
      : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({t:(key,...args)=>key.replace(/\{(\d+)\}/g,(_,i)=>args[+i]??""),languages:[],language:"en"});'
      : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:{ink:"#111",ink3:"#888"}});'
      : args.path.endsWith("AccountProofForm") ? 'import React from "react"; export const AccountProofForm=props=>React.createElement("Proof",props);'
      : args.path.endsWith("appleSignIn") ? 'export const appleIdentity=async()=>null;'
      : args.path.endsWith("googleSignIn") ? 'export const googleSignIn=async()=>null;'
      : args.path.endsWith("LanguageDropdown") ? 'export const LanguageDropdown=()=>null;'
      : args.path.endsWith("CompassBearing") ? 'export const CompassBearing=()=>null;'
            : args.path.endsWith("RoofSunDiagram") ? 'export const RoofSunDiagram=()=>null;'
      : args.path === "lucide-react-native" ? 'export const Check="Check",LocateFixed="LocateFixed",Plug="Plug",User="User";'
      : args.path === "expo-file-system" ? 'export const Paths={cache:"cache"};export class File{}'
      : args.path === "expo-sharing" ? 'export const isAvailableAsync=async()=>false;export const shareAsync=async()=>{};'
      : args.path === "expo-location" ? 'export const Accuracy={Balanced:1};export const requestForegroundPermissionsAsync=async()=>({status:"denied"});export const getCurrentPositionAsync=async()=>{};'
      : 'import React from "react";export const AppButton=props=>React.createElement("button",props,props.label);export const TextField=props=>React.createElement("field",props);export const ThemedText="Text",Card="Card",Screen="Screen",DataRow="DataRow",EmptyState="EmptyState",ErrorBanner="ErrorBanner",Group="Group",Header="Header",LoadingState="LoadingState",NavigationRow="NavigationRow",NativeSwitch="NativeSwitch",SectionTitle="SectionTitle",SegmentedControl="SegmentedControl",StatusPill="StatusPill",SwitchRow="SwitchRow";'
    }));
  } }] }); const module = { exports: {} as any }; new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports); return module.exports;
}
async function harness(component: string, handler: (route: string, body: any) => Promise<unknown>, replacement?: typeof handler) {
  globals.IS_REACT_ACT_ENVIRONMENT = true; const calls: { route: string; body: any }[] = []; let signouts = 0;
  const apiFor = (handle: typeof handler) => { const client = new ApiClient({ baseUrl: "https://solar.example", token: "session", transport: async (url, init) => { const route = new URL(url).pathname; const body = init.body ? JSON.parse(init.body) : null; calls.push({ route, body }); const value = await handle(route, body); return value instanceof Response ? value : new Response(JSON.stringify(value)); } }); return { api: new DeyeSolarApi(client), client }; };
  const first = apiFor(handler); globals.__accountPages = { auth: { api: first.api, apiBaseUrl: "https://solar.example", isDemo: false, logout: async () => { signouts++; } }, alerts: [] };
  const Components = await screens(); const Component = Components[component]; let renderer!: ReturnType<typeof create>; await act(async () => { renderer = create(React.createElement(Component)); });
  return { calls, get signouts() { return signouts; }, first, renderer,
    field: async (label: string, value: string) => { await act(async () => renderer.root.findAllByType("field").find(item => item.props.label === label)!.props.onChangeText(value)); },
    proof: async () => { await act(async () => renderer.root.findAllByType("Proof")[0]!.props.onProof({ currentPassword: "correct-test-password" })); },
    press: async (label: string) => { await act(async () => { const button = renderer.root.findAllByType("button").find(item => item.props.label === label)!; assert.ok(button); assert.equal(button.props.disabled, false); button.props.onPress(); }); },
    confirm: async () => { await act(async () => globals.__accountPages!.alerts.at(-1)!.buttons.at(-1)!.onPress!()); },
    replace: async () => { const next = apiFor(replacement!); globals.__accountPages!.auth = { ...globals.__accountPages!.auth, api: next.api }; await act(async () => renderer.update(React.createElement(Component))); },
    close: async () => { await act(async () => renderer.unmount()); delete globals.__accountPages; delete globals.IS_REACT_ACT_ENVIRONMENT; }
  };
}
test("contact replacement preserves the verified contact until completion and rejects a previous account's late reply", async () => {
  let finish!: (value: unknown) => void; const pending = new Promise(resolve => { finish = resolve; });
  const first = { displayName: "Owner", verifiedEmail: "old@example.com", verifiedPhone: null }; const second = { displayName: "Second", verifiedEmail: "second@example.com", verifiedPhone: null };
  const h = await harness("EditProfileScreen", async route => route.endsWith("/profile") ? first : route.endsWith("/start") ? { challengeId: "challenge", expiresAt: new Date(Date.now() + 60000).toISOString(), retryAfterSeconds: 30 } : pending, async () => second);
  try { await h.field("New email", "new@example.com"); await h.proof(); await h.press("Verify new contact"); assert.ok(JSON.stringify(h.renderer.toJSON()).includes("old@example.com")); await h.field("Verification code", "123456"); await h.press("Confirm new contact"); await h.replace(); await act(async () => finish({ ...first, verifiedEmail: "new@example.com" })); const tree = JSON.stringify(h.renderer.toJSON()); assert.ok(tree.includes("second@example.com")); assert.ok(!tree.includes("new@example.com")); } finally { await h.close(); }
});
test("revoking other sessions uses stable server identifiers and keeps the current session", async () => {
  let revoked = false; const sessions = [{ id: "11111111-1111-4111-8111-111111111111", client: "iPhone", platform: "ios", createdAt: "2026-10-06T12:00:00Z", lastSeenAt: null, isCurrent: true }, { id: "22222222-2222-4222-8222-222222222222", client: null, platform: null, createdAt: "2026-10-06T11:00:00Z", lastSeenAt: null, isCurrent: false }];
  const h = await harness("PasswordSessionsScreen", async route => { if (route.endsWith("/revoke-others")) { revoked = true; return { revoked: true }; } return { sessions: revoked ? [sessions[0]] : sessions }; });
  try { await h.proof(); await h.press("Sign out other devices"); await h.confirm(); const mutation = h.calls.find(item => item.route.endsWith("/revoke-others"))!; assert.deepEqual(mutation.body, { proof: { currentPassword: "correct-test-password" } }); assert.equal(h.signouts, 0); assert.equal(h.renderer.root.findAllByType("SectionTitle")[0]!.props.title, "Signed-in devices"); assert.equal(h.renderer.root.findAllByType("Card").length, 2); } finally { await h.close(); }
});
test("password change stays disabled until both new passwords meet the requirement", async () => {
  const h = await harness("ChangePasswordScreen", async () => ({ signedOut: true }));
  try { await h.proof(); await h.field("New password", "short"); await h.field("Confirm new password", "short"); assert.equal(h.renderer.root.findByType("button").props.disabled, true); await h.field("New password", "long-new-password"); await h.field("Confirm new password", "different-password"); assert.equal(h.renderer.root.findByType("button").props.disabled, true); await h.field("Confirm new password", "long-new-password"); await h.press("Change password"); assert.equal(h.signouts, 1); assert.deepEqual(h.calls.at(-1)!.body, { proof: { currentPassword: "correct-test-password" }, newPassword: "long-new-password" }); } finally { await h.close(); }
});
