import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import { createHash, randomBytes } from "node:crypto";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";
import { sessionKeys } from "../src/application/sessionStorage";

for (const [language, linkLabel, successMessage, loginMode] of [
  ["en", "Link Google account", "Google is linked to this account.", "restored"],
  ["pl", "Połącz konto Google", "Google jest połączony z tym kontem.", "restored"],
  ["en", "Link Google account", "Google is linked to this account.", "password"]
] as const) test(`the real account card links Google in ${language} after ${loginMode} sign-in without signing out its current owner`, async () => {
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
  const ticket = "C".repeat(43);
  const native = {
    preferences: storage(preferences), secure: storage(secure),
    fetch: async (url: string, init: { headers: Record<string, string>; body?: string }) => {
      const pathname = new URL(url).pathname;
      calls.push({ path: pathname, bearer: init.headers.Authorization });
      if (pathname === "/api/auth/login") {
        assert.equal(init.headers.Authorization, undefined);
        assert.deepEqual(JSON.parse(init.body!), { username: "owner", password: "local-test-password" });
      }
      if (pathname === "/api/auth/google/link/start") {
        startedFlow = JSON.parse(init.body!);
        assert.match(startedFlow!.codeChallenge, /^[A-Za-z0-9_-]{43}$/);
        assert.match(startedFlow!.state, /^[A-Za-z0-9_-]{16,128}$/);
      }
      const linking = pathname === "/api/auth/google/exchange";
      const proof = linking ? JSON.parse(init.body!) : null;
      const validProof = !linking || proof.code === ticket && startedFlow !== undefined
        && createHash("sha256").update(proof.codeVerifier, "ascii").digest("base64url") === startedFlow.codeChallenge;
      const status = linking && (!validProof || init.headers.Authorization !== "Bearer original-owner-token") ? 401 : 200;
      const body = pathname === "/api/auth/options" ? { googleEnabled: true, emailEnabled: false, phoneEnabled: false, registrationEnabled: true }
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
    // Bundle the actual provider and card. Only native platform boundaries are replaced;
    // React, auth state transitions, API client and the card's event handler remain real.
    const bundle = await build({
      stdin: { contents: 'export { AuthProvider, useAuth } from "./src/application/AuthContext"; export { LanguageProvider } from "./src/application/LanguageContext"; export { AccountIdentityCard } from "./src/features/auth/AccountIdentityCard";', resolveDir: process.cwd(), loader: "ts" },
      bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
      plugins: [{ name: "native-boundaries", setup(builder) {
        builder.onResolve({ filter: /^(react-native|expo\/fetch|expo-secure-store|expo-crypto|expo-web-browser|@react-native-async-storage\/async-storage)$/ }, args => ({ path: args.path, namespace: "native-test" }));
        builder.onResolve({ filter: /(?:^|\/)core\/components$/ }, () => ({ path: "components", namespace: "native-test" }));
        builder.onLoad({ filter: /.*/, namespace: "native-test" }, args => {
          const state = "globalThis.__solarAccountNative";
          const contents = args.path === "react-native" ? 'export const Platform = {OS:"ios"}; export const Text = "Text"; export const View = "View";'
            : args.path === "expo/fetch" ? `export const fetch = (...args) => ${state}.fetch(...args);`
            : args.path === "expo-crypto" ? `export const randomUUID = () => "11111111-1111-4111-8111-111111111111"; export const getRandomBytesAsync = length => ${state}.randomBytes(length); export const CryptoDigestAlgorithm = {SHA256:"SHA-256"}; export const CryptoEncoding = {BASE64:"base64"}; export const digestStringAsync = (...args) => ${state}.digest(...args);`
            : args.path === "expo-secure-store" ? `export const WHEN_UNLOCKED_THIS_DEVICE_ONLY = 1; export const getItemAsync = key => ${state}.secure.getItem(key); export const setItemAsync = (key,value) => ${state}.secure.setItem(key,value); export const deleteItemAsync = key => ${state}.secure.removeItem(key);`
            : args.path === "@react-native-async-storage/async-storage" ? `export default ${state}.preferences;`
            : args.path === "expo-web-browser" ? `export const openAuthSessionAsync = (...args) => ${state}.openAuthSession(...args);`
            : 'import React from "react"; export const AppButton = props => React.createElement("button", props, props.label); export const Card = "Card"; export const SectionTitle = "SectionTitle"; export const ErrorBanner = "ErrorBanner"; export const TextField = "TextField";';
          return { contents, loader: "js" };
        });
      } }]
    });
    const module = { exports: {} as any };
    new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
    const { AuthProvider, useAuth, LanguageProvider, AccountIdentityCard } = module.exports;
    let current: { isAuthenticated: boolean; username: string | null; login(input: { baseUrl: string; username: string; password: string }): Promise<void> } | undefined;
    function Probe() { current = useAuth(); return null; }
    await act(async () => {
      renderer = create(React.createElement(AuthProvider, null,
        React.createElement(LanguageProvider, null, React.createElement(Probe), React.createElement(AccountIdentityCard))));
    });
    if (loginMode === "password") {
      assert.equal(current?.isAuthenticated, false);
      await act(async () => { await current!.login({ baseUrl: "https://solar.dshapar.com", username: "owner", password: "local-test-password" }); });
      assert.equal(secure.get(sessionKeys.secureSession), stored);
    }
    assert.equal(current?.isAuthenticated, true);
    assert.equal(current?.username, "owner");
    const button = renderer!.root.findAllByType("button").find((item: any) => item.props.label === linkLabel)!;
    assert.equal(button.props.disabled, false);
    await act(async () => { button.props.onPress(); });
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
    assert.ok(JSON.stringify(renderer!.toJSON()).includes(successMessage));
  } finally {
    if (renderer) await act(async () => renderer!.unmount());
    delete globals.__solarAccountNative;
    delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
