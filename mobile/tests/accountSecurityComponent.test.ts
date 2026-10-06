import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { ApiClient } from "../src/core/api/ApiClient";
import { DeyeSolarApi } from "../src/core/api/DeyeSolarApi";

type AlertButton = { text: string; onPress?(): void };
type State = { auth: { api: DeyeSolarApi; apiBaseUrl: string; isDemo: boolean; logout(): Promise<void> }; alerts: AlertButton[][] };
const globals = globalThis as typeof globalThis & { __securityPages?: State; IS_REACT_ACT_ENVIRONMENT?: boolean };
async function screens() {
  const bundle = await build({
    stdin: { contents: 'export { PasswordSessionsScreen, DeleteAccountScreen } from "./src/features/settings/SettingsPages";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "native-boundaries", setup(builder) {
      builder.onResolve({ filter: /^(react-native|@react-navigation\/native|expo-location|expo-file-system|expo-sharing|expo-crypto|expo-web-browser|lucide-react-native)$|(?:^|\/)(AuthContext|LanguageContext|core\/components|ThemeProvider|appleSignIn|googleSignIn|LanguageDropdown|CompassBearing)$/ }, args => ({ path: args.path, namespace: "security" }));
      builder.onLoad({ filter: /.*/, namespace: "security" }, args => ({ loader: "js", contents:
        args.path === "react-native" ? 'export const View="View",Platform={OS:"ios"};export const Alert={alert:(title,message,buttons)=>globalThis.__securityPages.alerts.push(buttons)};'
        : args.path === "@react-navigation/native" ? 'export const useNavigation=()=>({navigate(){},dispatch(){}}),usePreventRemove=()=>{};'
        : args.path.endsWith("AuthContext") ? 'export const useAuth=()=>globalThis.__securityPages.auth;'
        : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({t:(key,...args)=>key.replace(/\{(\d+)\}/g,(_,i)=>args[+i]??""),languages:[],language:"en"});'
        : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:{ink:"#111",ink3:"#888"}});'
        : args.path.endsWith("appleSignIn") ? 'export const appleIdentity=async()=>null;'
        : args.path.endsWith("googleSignIn") ? 'export const googleSignIn=async()=>null;'
        : args.path.endsWith("LanguageDropdown") ? 'export const LanguageDropdown=()=>null;'
        : args.path.endsWith("CompassBearing") ? 'export const CompassBearing=()=>null;'
        : args.path === "lucide-react-native" ? 'export const Check="Check",LocateFixed="LocateFixed",Plug="Plug",User="User";'
        : args.path === "expo-file-system" ? 'export const Paths={cache:"cache"};export class File{}'
        : args.path === "expo-sharing" ? 'export const isAvailableAsync=async()=>false;export const shareAsync=async()=>{};'
        : args.path === "expo-location" ? 'export const Accuracy={Balanced:1};export const requestForegroundPermissionsAsync=async()=>({status:"denied"});export const getCurrentPositionAsync=async()=>{};'
        : args.path === "expo-crypto" ? 'export const getRandomBytesAsync=async()=>new Uint8Array(32);export const CryptoDigestAlgorithm={SHA256:"SHA-256"},CryptoEncoding={BASE64:"base64"};export const digestStringAsync=async()=>"";'
        : args.path === "expo-web-browser" ? 'export const openAuthSessionAsync=async()=>({type:"cancel"});'
        : 'import React from "react";export const AppButton=props=>React.createElement("button",props,props.label);export const TextField=props=>React.createElement("field",props);export const ThemedText="Text",Card="Card",Screen="Screen",DataRow="DataRow",EmptyState="EmptyState",ErrorBanner="ErrorBanner",Group="Group",Header="Header",LoadingState="LoadingState",NavigationRow="NavigationRow",NativeSwitch="NativeSwitch",SectionTitle="SectionTitle",SegmentedControl="SegmentedControl",StatusPill="StatusPill",SwitchRow="SwitchRow";'
      }));
    } }]
  });
  const module = { exports: {} as any }; new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports); return module.exports;
}
async function harness(component: string) {
  const calls: { path: string; bearer?: string; body: unknown }[] = [];
  let finishRevoke!: () => void; const pending = new Promise<void>(resolve => { finishRevoke = resolve; }); let signouts = 0;
  const sessionId = "11111111-1111-4111-8111-111111111111";
  const apiFor = (token: string) => new DeyeSolarApi(new ApiClient({ baseUrl: "https://solar.example", token, transport: async (url, init) => {
    const pathname = new URL(url).pathname; const body = init.body ? JSON.parse(init.body) : null;
    calls.push({ path: pathname, body, bearer: init.headers.Authorization });
    if (pathname.endsWith("/revoke") && token === "original-account") await pending;
    const value = pathname === "/api/auth/identities" ? { email: token + "@example.test", phone: null, googleLinked: false, appleLinked: false, hasPassword: true }
      : pathname === "/api/account/sessions" ? { sessions: [{ id: sessionId, client: "iPhone", platform: "ios", createdAt: "2026-10-06T10:00:00Z", lastSeenAt: null, isCurrent: true }] }
      : pathname === "/api/auth/security/verification/start" ? { verificationId: "fresh-proof", expiresAt: new Date(Date.now() + 60000).toISOString(), retryAfterSeconds: 60 }
      : { revoked: true };
    return new Response(JSON.stringify(value));
  } }));
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  globals.__securityPages = { auth: { api: apiFor("original-account"), apiBaseUrl: "https://solar.example", isDemo: false, logout: async () => { signouts++; } }, alerts: [] };
  const Components = await screens(); const Component = Components[component]; let renderer!: ReturnType<typeof create>;
  await act(async () => { renderer = create(React.createElement(Component)); });
  const field = (label: string) => renderer.root.findAllByType("field").find(item => item.props.label === label)!;
  const button = (label: string) => renderer.root.findAllByType("button").find(item => item.props.label === label)!;
  return { calls, renderer, sessionId, field, button, get signouts() { return signouts; },
    otp: async () => {
      await act(async () => { button("Send verification code").props.onPress(); });
      assert.deepEqual(calls.find(call => call.path === "/api/auth/security/verification/start")!.body, { channel: "email", destination: "original-account@example.test" });
      assert.equal(field("Verification code").props.autoComplete, "one-time-code");
      await act(async () => field("Verification code").props.onChangeText("12-3456"));
      assert.equal(field("Verification code").props.value, "123456", "Fresh OTP is retained after issuance and normalized to six digits");
    },
    replace: async () => { globals.__securityPages!.auth = { ...globals.__securityPages!.auth, api: apiFor("replacement-account") }; await act(async () => renderer.update(React.createElement(Component))); },
    finish: async () => { await act(async () => { finishRevoke(); await pending; }); },
    close: async () => { await act(async () => renderer.unmount()); delete globals.__securityPages; delete globals.IS_REACT_ACT_ENVIRONMENT; }
  };
}

test("the real sessions screen uses fresh OTP proof and ignores a late current-session revocation after account replacement", async () => {
  const h = await harness("PasswordSessionsScreen");
  try {
    assert.equal(h.button("Sign out this device").props.disabled, true);
    await h.otp(); assert.equal(h.button("Sign out this device").props.disabled, false);
    await act(async () => h.button("Sign out this device").props.onPress());
    await act(async () => globals.__securityPages!.alerts.at(-1)!.at(-1)!.onPress!());
    const revoke = h.calls.find(call => call.path.endsWith("/revoke"))!;
    assert.equal(revoke.path, `/api/account/sessions/${h.sessionId}/revoke`);
    assert.deepEqual(revoke.body, { proof: { verificationId: "fresh-proof", code: "123456" } });
    assert.equal(revoke.bearer, "Bearer original-account");
    await h.replace();
    assert.equal(h.field("Current password").props.value, "");
    assert.equal(h.renderer.root.findAllByType("field").some(item => item.props.label === "Verification code"), false);
    assert.equal(h.button("Sign out this device").props.disabled, true);
    await h.finish(); assert.equal(h.signouts, 0, "The old response cannot sign out the replacement account");
    assert.ok(h.renderer.root.findAllByType("ErrorBanner").every(item => item.props.message === null));
  } finally { await h.close(); }
});

test("an old destructive account confirmation cannot delete a replacement account", async () => {
  const h = await harness("DeleteAccountScreen");
  try {
    await h.otp();
    await act(async () => h.field("Type DELETE to confirm").props.onChangeText("DELETE"));
    assert.equal(h.button("Delete account").props.disabled, false);
    await act(async () => h.button("Delete account").props.onPress());
    const oldConfirmation = globals.__securityPages!.alerts.at(-1)!.at(-1)!.onPress!;
    assert.ok(JSON.stringify(h.renderer.toJSON()).includes("An App Store subscription must be canceled separately"));
    await h.replace(); assert.equal(h.field("Type DELETE to confirm").props.value, "");
    assert.equal(h.button("Delete account").props.disabled, true);
    await act(async () => oldConfirmation());
    assert.equal(h.calls.filter(call => call.path === "/api/auth/security/delete").length, 0);
    assert.equal(h.signouts, 0);
  } finally { await h.close(); }
});
