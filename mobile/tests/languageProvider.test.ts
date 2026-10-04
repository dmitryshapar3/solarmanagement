import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";

type Preference = { language: string; setLanguage(language: string): Promise<void> };
type Storage = { getItem(key: string): Promise<string | null>; multiSet(entries: [string, string][]): Promise<void> };
type Api = { integrations: { sessionEpoch: number }; getLanguage(signal: AbortSignal): Promise<{ language: string | null }>; setLanguage(language: string): Promise<unknown> };
type Auth = { api: Api; username: string | null; apiBaseUrl: string; isAuthenticated: boolean; isDemo: boolean; isBootstrapping: boolean };
const globals = globalThis as typeof globalThis & {
  __languageTestAuth?: Auth; __languageTestStorage?: Storage; IS_REACT_ACT_ENVIRONMENT?: boolean;
};
const globalKey = "solar.language.v1";
const accountKey = (username: string) => `${globalKey}:https://solar.example:${username}`;
const pendingKey = (username: string) => `${accountKey(username)}:pending`;
function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

async function providerModule() {
  const result = await build({
    stdin: { contents: 'export {LanguageProvider, useLanguage} from "./src/application/LanguageContext"; export {ApiError} from "./src/core/api/ApiClient";', resolveDir: process.cwd(), loader: "ts" },
    bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
    plugins: [{ name: "language-provider-boundaries", setup(builder) {
      builder.onResolve({ filter: /@react-native-async-storage\/async-storage$/ }, () => ({ path: "storage", namespace: "language-test" }));
      builder.onResolve({ filter: /(?:^|\/)AuthContext$/ }, () => ({ path: "auth", namespace: "language-test" }));
      builder.onResolve({ filter: /(?:^|\/)core\/i18n$/ }, () => ({ path: "i18n", namespace: "language-test" }));
      builder.onResolve({ filter: /(?:^|\/)api\/ApiClient$/ }, () => ({ path: "api", namespace: "language-test" }));
      builder.onLoad({ filter: /.*/, namespace: "language-test" }, args => ({ loader: "js", contents:
        args.path === "storage" ? 'export default {getItem:(...args)=>globalThis.__languageTestStorage.getItem(...args),multiSet:(...args)=>globalThis.__languageTestStorage.multiSet(...args)};'
        : args.path === "auth" ? 'export const useAuth = () => globalThis.__languageTestAuth;'
        : args.path === "api" ? 'export class ApiError extends Error {constructor(status,message){super(message);this.status=status;}}'
        : 'let locale="en";export const languages=["en","pl","ru","de"].map(code=>({code,name:code}));export const currentLocale=()=>locale;export const setLocale=next=>{locale=next};export const normalizeLanguage=value=>languages.some(item=>item.code===value)?value:null;export const translate=phrase=>phrase??"";'
      }));
    } }]
  });
  const module = { exports: {} as { LanguageProvider: React.ComponentType<{ children: React.ReactNode }>; useLanguage(): Preference; ApiError: new (status: number, message: string) => Error } };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
}

async function harness(options: { authenticated?: boolean; values?: Map<string, string>; getItem?: Storage["getItem"]; multiSet?: Storage["multiSet"]; getLanguage?: Api["getLanguage"]; setLanguage?: Api["setLanguage"]; failStatus?: number } = {}) {
  const stored = options.values ?? new Map<string, string>();
  const remoteWrites: Array<{ username: string | null; language: string }> = [];
  let PreferenceApiError: new (status: number, message: string) => Error;
  const api: Api = { integrations: { sessionEpoch: 0 },
    getLanguage: options.getLanguage ?? (async () => ({ language: null })),
    setLanguage: async language => { remoteWrites.push({ username: globals.__languageTestAuth!.username, language }); if (options.failStatus) throw new PreferenceApiError(options.failStatus, `HTTP ${options.failStatus}`); return options.setLanguage ? options.setLanguage(language) : { language }; }
  };
  const auth: Auth = { api, username: "alice", apiBaseUrl: "https://solar.example", isAuthenticated: options.authenticated ?? true, isDemo: false, isBootstrapping: false };
  globals.__languageTestAuth = auth;
  globals.__languageTestStorage = {
    getItem: options.getItem ?? (async key => stored.get(key) ?? null),
    multiSet: async entries => { if (options.multiSet) await options.multiSet(entries); for (const [key, value] of entries) stored.set(key, value); }
  };
  globals.IS_REACT_ACT_ENVIRONMENT = true;
  const { LanguageProvider, useLanguage, ApiError } = await providerModule();
  PreferenceApiError = ApiError;
  let preference!: Preference;
  function Probe() { preference = useLanguage(); return React.createElement("preference", { language: preference.language }); }
  const element = () => React.createElement(LanguageProvider, null, React.createElement(Probe));
  let renderer!: ReturnType<typeof create>;
  await act(async () => { renderer = create(element()); });
  return {
    api, stored, remoteWrites, read: () => preference,
    async switchAccount(username: string) { api.integrations.sessionEpoch++; globals.__languageTestAuth = { ...auth, username }; await act(async () => { renderer.update(element()); }); },
    async close() { await act(async () => renderer.unmount()); delete globals.__languageTestAuth; delete globals.__languageTestStorage; delete globals.IS_REACT_ACT_ENVIRONMENT; }
  };
}

test("delayed startup preference cannot overwrite an explicit language selection", async () => {
  const saved = deferred<string | null>();
  const app = await harness({ authenticated: false, getItem: () => saved.promise });
  try {
    await act(async () => { await app.read().setLanguage("pl"); });
    await act(async () => { saved.resolve("ru"); });
    assert.equal(app.read().language, "pl");
    assert.equal(app.stored.get(globalKey), "pl");
    assert.deepEqual(app.remoteWrites, []);
  } finally { await app.close(); }
});

test("a late server preference cannot replace the language selected while it loads", async () => {
  const remote = deferred<{ language: string | null }>();
  const app = await harness({ getLanguage: () => remote.promise });
  try {
    await act(async () => { await app.read().setLanguage("de"); });
    await act(async () => { remote.resolve({ language: "ru" }); });
    assert.equal(app.read().language, "de");
    assert.equal(app.stored.get(accountKey("alice")), "de");
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "de" }]);
  } finally { await app.close(); }
});

test("rapid changes persist the final selection and do not send a stale preference", async () => {
  const firstStorage = deferred<void>();
  let storageCalls = 0;
  const app = await harness({ multiSet: async () => { if (++storageCalls === 1) await firstStorage.promise; } });
  try {
    let first!: Promise<void>;
    let last!: Promise<void>;
    await act(async () => { first = app.read().setLanguage("pl"); });
    await act(async () => { last = app.read().setLanguage("de"); });
    assert.equal(app.read().language, "de");
    await act(async () => { firstStorage.resolve(); await Promise.all([first, last]); });
    assert.equal(app.stored.get(globalKey), "de");
    assert.equal(app.stored.get(accountKey("alice")), "de");
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "de" }]);
  } finally { await app.close(); }
});

test("switching account during preference storage never sends the old selection with the new session", async () => {
  const storage = deferred<void>();
  const app = await harness({ multiSet: () => storage.promise });
  try {
    let pending!: Promise<void>;
    await act(async () => { pending = app.read().setLanguage("pl"); });
    await app.switchAccount("bob");
    await act(async () => { storage.resolve(); await pending; });
    assert.deepEqual(app.remoteWrites, []);
    assert.equal(app.stored.get(accountKey("bob")), undefined);
  } finally { await app.close(); }
});

test("an account change fences cached hydration before sending a preference read", async () => {
  const alice = deferred<string | null>();
  let reads = 0;
  const app = await harness({ getItem: key => key === accountKey("alice") ? alice.promise : Promise.resolve(null), getLanguage: async () => { reads++; return { language: null }; } });
  try {
    await app.switchAccount("bob");
    await act(async () => { alice.resolve("ru"); });
    assert.equal(app.read().language, "en");
    assert.equal(reads, 1, "only the current account starts its server preference read");
  } finally { await app.close(); }
});

test("hydration cache writes finish before a newer user selection is persisted", async () => {
  const hydration = deferred<void>();
  let calls = 0;
  const app = await harness({ getLanguage: async () => ({ language: "ru" }), multiSet: async () => { if (++calls === 1) await hydration.promise; } });
  try {
    assert.equal(app.read().language, "ru");
    let selected!: Promise<void>;
    await act(async () => { selected = app.read().setLanguage("pl"); });
    await act(async () => { hydration.resolve(); await selected; });
    assert.equal(app.read().language, "pl");
    assert.equal(app.stored.get(globalKey), "pl");
    assert.equal(app.stored.get(accountKey("alice")), "pl");
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "pl" }]);
  } finally { await app.close(); }
});


test("a retained setter from the previous account cannot change the new session", async () => {
  const app = await harness();
  try {
    const aliceSetter = app.read().setLanguage;
    await app.switchAccount("bob");
    await act(async () => { await aliceSetter("pl"); });
    assert.equal(app.read().language, "en");
    assert.deepEqual(app.remoteWrites, []);
    assert.equal(app.stored.get(accountKey("alice")), undefined);
    assert.equal(app.stored.get(accountKey("bob")), undefined);
  } finally { await app.close(); }
});

test("session invalidation with the same username fences old setters and pending server reads", async () => {
  const remote = deferred<{ language: string | null }>();
  const app = await harness({ getLanguage: () => remote.promise });
  try {
    const oldSetter = app.read().setLanguage;
    app.api.integrations.sessionEpoch++;
    await act(async () => { await oldSetter("de"); remote.resolve({ language: "ru" }); });
    assert.equal(app.read().language, "en");
    assert.deepEqual(app.remoteWrites, []);
    assert.equal(app.stored.get(globalKey), undefined);
  } finally { await app.close(); }
});


test("an older backend preserves the local choice and the upgraded server later receives it", async () => {
  const values = new Map<string, string>();
  let app = await harness({ values, failStatus: 404 });
  try {
    await act(async () => { await app.read().setLanguage("pl"); });
    assert.equal(app.read().language, "pl");
    assert.equal(values.get(pendingKey("alice")), "pl");
  } finally { await app.close(); }
  let remoteReads = 0;
  app = await harness({ values, getLanguage: async () => { remoteReads++; return { language: "en" }; } });
  try {
    assert.equal(app.read().language, "pl");
    assert.equal(remoteReads, 0, "the older remote language cannot override the pending choice");
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "pl" }]);
    assert.equal(values.get(pendingKey("alice")), "");
  } finally { await app.close(); }
});

test("an offline pending preference remains selected instead of an older server value", async () => {
  const values = new Map([[globalKey, "pl"], [accountKey("alice"), "pl"], [pendingKey("alice"), "pl"]]);
  let remoteReads = 0;
  const app = await harness({ values, getLanguage: async () => { remoteReads++; return { language: "ru" }; }, setLanguage: async () => { throw new Error("Network unavailable"); } });
  try {
    assert.equal(app.read().language, "pl");
    assert.equal(values.get(pendingKey("alice")), "pl");
    assert.equal(remoteReads, 0);
  } finally { await app.close(); }
});

test("a pending sync finishing after an account switch cannot clear the new account preference", async () => {
  const remote = deferred<unknown>();
  const values = new Map([[accountKey("alice"), "pl"], [pendingKey("alice"), "pl"]]);
  const app = await harness({ values, setLanguage: () => remote.promise });
  try {
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "pl" }]);
    await app.switchAccount("bob");
    await act(async () => { remote.resolve({ language: "pl" }); });
    assert.equal(values.get(pendingKey("alice")), "pl", "a revoked sync cannot mark its old write confirmed");
    assert.equal(values.get(pendingKey("bob")), undefined);
    assert.deepEqual(app.remoteWrites, [{ username: "alice", language: "pl" }]);
  } finally { await app.close(); }
});


test("a server validation rejection is reported while retaining the local pending choice", async () => {
  const app = await harness({ failStatus: 400 });
  try {
    await act(async () => { await assert.rejects(app.read().setLanguage("pl"), /HTTP 400/); });
    assert.equal(app.read().language, "pl");
    assert.equal(app.stored.get(pendingKey("alice")), "pl");
  } finally { await app.close(); }
});
