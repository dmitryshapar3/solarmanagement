import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import { createHash, randomBytes } from "node:crypto";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { sessionKeys } from "../src/application/sessionStorage";

for (const [language, loginMode] of [["en", "restored"], ["pl", "restored"], ["en", "password"]] as const) test(`the new sign-in security screen links Google in ${language} after ${loginMode} sign-in without signing out its current owner`, async () => {
  const stored = JSON.stringify({ baseUrl: "https://solar.dshapar.com", token: "original-owner-token", username: "owner" });
  const preferences = new Map([[sessionKeys.baseUrl, "https://solar.dshapar.com"], ["solar.language.v1", language]]);
  const secure = new Map(loginMode === "restored" ? [[sessionKeys.secureSession, stored]] : []);
  const storage = (values: Map<string, string>) => ({
    getItem: async (key: string) => values.get(key) ?? null,
    setItem: async (key: string, value: string) => { values.set(key, value); },
    multiSet: async (entries: [string, string][]) => { for (const [key, value] of entries) values.set(key, value); },
    removeItem: async (key: string) => { values.delete(key); }
  });
  const calls: { path: string; bearer?: string }[] = [];
  let startedFlow: { codeChallenge: string; state: string } | undefined;
  let browserCalls = 0;
  let googleLinked = false;
  let identityReads = 0;
  let finishInitialIdentity!: () => void;
  const initialIdentity = new Promise<void>(resolve => { finishInitialIdentity = resolve; });
  const ticket = "C".repeat(43);
  const native = {
    preferences: storage(preferences), secure: storage(secure),
    fetch: async (url: string, init: { headers: Record<string, string>; body?: string }) => {
      const pathname = new URL(url).pathname;
      calls.push({ path: pathname, bearer: init.headers.Authorization });
      if (pathname === "/api/auth/identities" && ++identityReads === 1) await initialIdentity;
      if (pathname === "/api/auth/login") {
        assert.equal(init.headers.Authorization, undefined);
        assert.deepEqual(JSON.parse(init.body!), { username: "owner", password: "local-test-password" });
      }
      if (pathname === "/api/auth/google/link/start") {
        startedFlow = JSON.parse(init.body!);
        assert.deepEqual(JSON.parse(init.body!).proof, { currentPassword: "verified-owner-password" });
        assert.match(startedFlow!.codeChallenge, /^[A-Za-z0-9_-]{43}$/);
        assert.match(startedFlow!.state, /^[A-Za-z0-9_-]{16,128}$/);
      }
      const linking = pathname === "/api/auth/google/exchange";
      const proof = linking ? JSON.parse(init.body!) : null;
      const validProof = !linking || proof.code === ticket && startedFlow !== undefined
        && createHash("sha256").update(proof.codeVerifier, "ascii").digest("base64url") === startedFlow.codeChallenge;
      const status = linking && (!validProof || init.headers.Authorization !== "Bearer original-owner-token") ? 401 : 200;
      if (linking && status === 200) googleLinked = true;
      const body = pathname === "/api/auth/options" ? { googleEnabled: true, emailEnabled: false, phoneEnabled: false, registrationEnabled: true }
        : pathname === "/api/auth/identities" ? { email: "owner@example.test", phone: "+48123456789", googleLinked }
        : pathname === "/api/auth/google/link/start" ? { authorizationUrl: "https://solar.dshapar.com/auth/google?linkTicket=test", expiresAt: "2026-10-04T12:02:00Z" }
        : pathname === "/api/auth/login" ? { token: "original-owner-token", username: "owner", expiresAt: "2026-11-04T12:00:00Z" }
        : linking ? status === 200 ? { token: "unused-replacement-token", username: "owner", expiresAt: "2026-11-04T12:00:00Z" } : { message: "Google sign-in expired. Please start again." }
        : { authenticated: true, username: "owner" };
      return { status, ok: status === 200, text: async () => JSON.stringify(body) };
    },
    randomBytes: async (length: number) => new Uint8Array(randomBytes(length)),
    digest: async (algorithm: string, text: string, options: { encoding: string }) => {
      assert.equal(algorithm, "SHA-256");
      assert.equal(options.encoding, "base64");
      return createHash("sha256").update(text, "utf8").digest("base64");
    },
    openAuthSession: async (url: string, callback: string, options: { preferEphemeralSession: boolean }) => {
      browserCalls++;
      assert.equal(url, "https://solar.dshapar.com/auth/google?linkTicket=test");
      assert.equal(callback, "deyesolar://auth/callback");
      assert.equal(options.preferEphemeralSession, true);
      assert.ok(startedFlow);
      return { type: "success", url: `${callback}?code=${ticket}&state=${startedFlow.state}` };
    }
  };
  const globals = globalThis as typeof globalThis & { __solarAccountNative?: typeof native; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarAccountNative = native;
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    // Auth state, secure storage, API client, PKCE and new screen handlers remain real.
    // The fresh proof form has its own component regression coverage.
    const bundle = await build({
      stdin: { contents: 'export { AuthProvider, useAuth } from "./src/application/AuthContext"; export { LanguageProvider } from "./src/application/LanguageContext"; export { SignInSecurityScreen } from "./src/features/settings/SettingsPages";', resolveDir: process.cwd(), loader: "ts" },
      bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
      plugins: [{ name: "native-boundaries", setup(builder) {
        builder.onResolve({ filter: /^(react-native|expo\/fetch|expo-secure-store|expo-crypto|expo-web-browser|expo-apple-authentication|@react-navigation\/native|expo-location|expo-file-system|expo-sharing|lucide-react-native|@react-native-async-storage\/async-storage)$/ }, args => ({ path: args.path, namespace: "native-test" }));
        builder.onResolve({ filter: /(?:^|\/)(core\/components|ThemeProvider|AccountProofForm|LanguageDropdown|CompassBearing)$/ }, args => ({ path: args.path, namespace: "native-test" }));
        builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => {
          const state = "globalThis.__solarAccountNative";
          const contents = args.path === "react-native" ? 'export const Platform = {OS:"ios"}; export const Text = "Text"; export const View = "View"; export const Alert={alert(){}};'
            : args.path === "expo/fetch" ? `export const fetch = (...args) => ${state}.fetch(...args);`
            : args.path === "expo-crypto" ? `export const randomUUID = () => "11111111-1111-4111-8111-111111111111"; export const getRandomBytesAsync = length => ${state}.randomBytes(length); export const CryptoDigestAlgorithm = {SHA256:"SHA-256"}; export const CryptoEncoding = {BASE64:"base64"}; export const digestStringAsync = (...args) => ${state}.digest(...args);`
            : args.path === "expo-secure-store" ? `export const WHEN_UNLOCKED_THIS_DEVICE_ONLY = 1; export const getItemAsync = key => ${state}.secure.getItem(key); export const setItemAsync = (key,value) => ${state}.secure.setItem(key,value); export const deleteItemAsync = key => ${state}.secure.removeItem(key);`
            : args.path === "@react-native-async-storage/async-storage" ? `export default ${state}.preferences;`
            : args.path === "expo-apple-authentication" ? 'export const isAvailableAsync=async()=>false; export const AppleAuthenticationScope={FULL_NAME:0,EMAIL:1}; export const signInAsync=async()=>{throw new Error("unavailable")};'
            : args.path === "@react-navigation/native" ? 'export const useNavigation=()=>({navigate(){},dispatch(){}});export const usePreventRemove=()=>{};'
            : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:{ink:"#111",ink3:"#888"}});'
            : args.path.endsWith("AccountProofForm") ? 'import React from "react";export const AccountProofForm=props=>React.createElement("Proof",props);'
            : args.path.endsWith("LanguageDropdown") ? 'export const LanguageDropdown=()=>null;'
            : args.path.endsWith("CompassBearing") ? 'export const CompassBearing=()=>null;'
            : args.path === "expo-file-system" ? 'export const Paths={cache:"cache"};export class File{}'
            : args.path === "expo-sharing" ? 'export const isAvailableAsync=async()=>false;export const shareAsync=async()=>{};'
            : args.path === "expo-location" ? 'export const Accuracy={Balanced:1};export const requestForegroundPermissionsAsync=async()=>({status:"denied"});export const getCurrentPositionAsync=async()=>{};'
            : args.path === "lucide-react-native" ? 'export const Check="Check",LocateFixed="LocateFixed",Plug="Plug",User="User";'
            : args.path === "expo-web-browser" ? `export const openAuthSessionAsync = (...args) => ${state}.openAuthSession(...args);`
            : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card="Card",SectionTitle="SectionTitle",ErrorBanner="ErrorBanner",TextField="TextField",StatusPill="StatusPill",ThemedText="Text",Screen="Screen",DataRow="DataRow",EmptyState="EmptyState",Group="Group",Header="Header",LoadingState="LoadingState",NavigationRow="NavigationRow",NativeSwitch="NativeSwitch",SegmentedControl="SegmentedControl",SwitchRow="SwitchRow";';
          return { contents, loader: "js" };
        });
      } }]
    });
    const module = { exports: {} as any };
    new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
    const { AuthProvider, useAuth, LanguageProvider, SignInSecurityScreen } = module.exports;
    let current: { isAuthenticated: boolean; username: string | null; login(input: { baseUrl: string; username: string; password: string }): Promise<void> } | undefined;
    function Probe() { current = useAuth(); return current!.isAuthenticated ? React.createElement(SignInSecurityScreen) : null; }
    await act(async () => {
      renderer = create(React.createElement(AuthProvider, null,
        React.createElement(LanguageProvider, null, React.createElement(Probe))));
    });
    if (loginMode === "password") {
      assert.equal(current?.isAuthenticated, false);
      await act(async () => { await current!.login({ baseUrl: "https://solar.dshapar.com", username: "owner", password: "local-test-password" }); });
      assert.equal(secure.get(sessionKeys.secureSession), stored);
    }
    assert.equal(current?.isAuthenticated, true);
    assert.equal(current?.username, "owner");
    assert.equal(identityReads, 1);
    assert.equal(renderer!.root.findByType("Screen").props.refreshing, true, "Initial identity reads must block screen pulls");
    await act(async () => { await renderer!.root.findByType("Screen").props.onRefresh(); });
    assert.equal(identityReads, 1, "A pull cannot overlap the initial identity read or allow its old status to win later");
    await act(async () => { finishInitialIdentity(); await initialIdentity; });
    assert.equal(renderer!.root.findByType("Screen").props.refreshing, false);
    await act(async () => { await renderer!.root.findByType("Screen").props.onRefresh(); });
    assert.equal(identityReads, 2, "Pull refresh becomes available after the initial request finishes");
    const before = renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Google")!.props.value;
    const linkButton = () => renderer!.root.findAllByType("button").find(item => item.props.label === "Link sign-in method")!;
    assert.equal(linkButton().props.disabled, true, "Linking must require a fresh account proof");
    await act(async () => renderer!.root.findByType("Proof").props.onProof({ currentPassword: "verified-owner-password" }));
    assert.equal(linkButton().props.disabled, false);
    await act(async () => { linkButton().props.onPress(); });
    assert.equal(browserCalls, 1);
    assert.equal(renderer!.root.findByType("ErrorBanner").props.message, null);
    assert.deepEqual(calls.filter(call => call.path.includes("google")), [
      { path: "/api/auth/google/link/start", bearer: "Bearer original-owner-token" },
      { path: "/api/auth/google/exchange", bearer: "Bearer original-owner-token" }
    ]);
    assert.equal(current?.isAuthenticated, true);
    assert.equal(current?.username, "owner");
    assert.equal(secure.get(sessionKeys.secureSession), stored);
    assert.equal(preferences.get(sessionKeys.disabled), undefined);
    assert.ok(JSON.stringify(renderer!.toJSON()).includes("Sign-in method linked"));
    const linked = renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Google")!.props.value;
    assert.notEqual(linked, before, "The server linked status is shown in the selected language");
    assert.ok(JSON.stringify(renderer!.toJSON()).includes("owner@example.test"));
    assert.ok(JSON.stringify(renderer!.toJSON()).includes("+48123456789"));
    const linkInstruction = language === "pl" ? "Każda metoda kontaktu wymaga weryfikacji." : "Each contact must be verified.";
    assert.equal(JSON.stringify(renderer!.toJSON()).includes(linkInstruction), false, "A fully linked account should not be asked to link its identities again");
    assert.equal(renderer!.root.findAllByType("button").some((item: any) => item.props.label === "Link sign-in method"), false);
    assert.ok(calls.filter(call => call.path === "/api/auth/identities").every(call => call.bearer === "Bearer original-owner-token"));
    // The provider binding is read again on opening Settings; its state is not just a transient success message.
    await act(async () => renderer!.unmount());
    await act(async () => {
      renderer = create(React.createElement(AuthProvider, null,
        React.createElement(LanguageProvider, null, React.createElement(Probe))));
    });
    assert.equal(renderer!.root.findAllByType("button").some((item: any) => item.props.label === "Link sign-in method"), false);
    assert.equal(renderer!.root.findAllByType("NavigationRow").find(item => item.props.title === "Google")!.props.value, linked);
    assert.equal(browserCalls, 1);
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__solarAccountNative;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
