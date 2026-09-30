import assert from "node:assert/strict";
import { test } from "node:test";
import { SessionOperations, SessionStorage, sessionKeys, type StringStorage } from "../src/application/sessionStorage";

const baseUrl = "https://solar.example";
const session = { baseUrl, token: "private-test-token", username: "test-user" };

class MemoryStorage implements StringStorage {
  values = new Map<string, string>();
  failSet = false;
  failRemove = false;
  async getItem(key: string) { return this.values.get(key) ?? null; }
  async setItem(key: string, value: string) { if (this.failSet) throw new Error("Storage failed"); this.values.set(key, value); }
  async removeItem(key: string) { if (this.failRemove) throw new Error("Storage failed"); this.values.delete(key); }
}

function setup() {
  const preferences = new MemoryStorage();
  const secure = new MemoryStorage();
  return { preferences, secure, store: new SessionStorage(preferences, secure) };
}

test("native sessions round-trip through secure storage without plaintext token or password", async () => {
  const { store, secure, preferences } = setup();
  await store.save({ ...session, password: "never-persist-this" } as typeof session);
  assert.deepEqual((await store.load(baseUrl)).session, session);
  assert.deepEqual(JSON.parse(secure.values.get(sessionKeys.secureSession)!), session);
  assert.deepEqual([...preferences.values], [[sessionKeys.baseUrl, baseUrl]]);
  assert.ok(!JSON.stringify([...secure.values]).includes("never-persist-this"));
});

test("legacy unbound plaintext credentials are removed while preserving the chosen LAN endpoint", async () => {
  const { store, secure, preferences } = setup();
  preferences.values.set(sessionKeys.baseUrl, "http://192.168.31.190:5000");
  preferences.values.set(sessionKeys.legacyToken, "unbound-old-token");
  preferences.values.set(sessionKeys.legacyUsername, "old-user");
  assert.deepEqual(await store.load(baseUrl), { baseUrl: "http://192.168.31.190:5000", session: null });
  assert.deepEqual([...preferences.values], [[sessionKeys.baseUrl, "http://192.168.31.190:5000"]]);
  assert.equal(secure.values.size, 0);
});

for (const savedBase of [undefined, "https://foreign.example", "invalid stored address"]) {
  test(`a Keychain session cannot be restored with ${savedBase ?? "missing installation settings"}`, async () => {
    const { store, secure, preferences } = setup();
    secure.values.set(sessionKeys.secureSession, JSON.stringify(session));
    if (savedBase) preferences.values.set(sessionKeys.baseUrl, savedBase);
    assert.equal((await store.load(baseUrl)).session, null);
    assert.equal(secure.values.size, 0);
  });
}

test("corrupt secure records fail closed and are removed", async () => {
  const { store, secure, preferences } = setup();
  preferences.values.set(sessionKeys.baseUrl, baseUrl);
  secure.values.set(sessionKeys.secureSession, "{invalid");
  assert.equal((await store.load(baseUrl)).session, null);
  assert.equal(secure.values.size, 0);
});

test("web sessions live only in memory and never survive a new app instance", async () => {
  const preferences = new MemoryStorage();
  const store = new SessionStorage(preferences, null);
  await store.save(session);
  assert.deepEqual((await store.load(baseUrl)).session, session);
  assert.deepEqual([...preferences.values], [[sessionKeys.baseUrl, baseUrl]]);
  assert.equal((await new SessionStorage(preferences, null).load(baseUrl)).session, null);
  await store.clear();
  assert.equal((await store.load(baseUrl)).session, null);
});

test("a failed secure write cannot authenticate or retain legacy plaintext", async () => {
  const { store, secure, preferences } = setup();
  preferences.values.set(sessionKeys.legacyToken, "old-token");
  secure.failSet = true;
  await assert.rejects(store.save(session), /save this session securely/);
  assert.ok(!preferences.values.has(sessionKeys.legacyToken));
  assert.equal((await store.load(baseUrl)).session, null);
  secure.failSet = false;
  await store.save(session);
  assert.deepEqual((await store.load(baseUrl)).session, session);
});

test("a deletion failure leaves a persisted sign-out fence instead of reviving a Keychain token", async () => {
  const { store, secure, preferences } = setup();
  await store.save(session);
  secure.failRemove = true;
  await assert.rejects(store.clear(), /storage could not be cleared/);
  assert.equal(preferences.values.get(sessionKeys.disabled), "1");
  await assert.rejects(new SessionStorage(preferences, secure).load(baseUrl));
  secure.failRemove = false;
  assert.equal((await store.load(baseUrl)).session, null);
});

test("endpoint changes remove the old session before persisting a replacement URL", async () => {
  const { store, secure, preferences } = setup();
  await store.save(session);
  const writes: string[] = [];
  const set = preferences.setItem.bind(preferences);
  preferences.setItem = async (key, value) => {
    if (key === sessionKeys.baseUrl) assert.equal(secure.values.size, 0);
    writes.push(key);
    await set(key, value);
  };
  await store.changeBaseUrl("https://new.example/");
  assert.deepEqual(await store.load(baseUrl), { baseUrl: "https://new.example", session: null });
  assert.ok(writes.includes(sessionKeys.baseUrl));
});

test("sign-out queued during a secure write wins over that late write", async () => {
  const { store, secure } = setup();
  let release!: () => void;
  let started!: () => void;
  const hold = new Promise<void>(done => { release = done; });
  const writing = new Promise<void>(done => { started = done; });
  const set = secure.setItem.bind(secure);
  secure.setItem = async (key, value) => { started(); await hold; await set(key, value); };
  const login = store.save(session);
  await writing;
  const logout = store.clear();
  release();
  await Promise.all([login, logout]);
  assert.equal(secure.values.size, 0);
  assert.equal((await store.load(baseUrl)).session, null);
});

test("bootstrap, login and logout generations cannot publish over a newer session", () => {
  const operations = new SessionOperations();
  const bootstrap = operations.begin();
  const firstLogin = operations.begin();
  const logout = operations.begin();
  const secondLogin = operations.begin();
  const published: string[] = [];
  for (const [name, signal] of [["bootstrap", bootstrap], ["first login", firstLogin], ["logout", logout], ["second login", secondLogin]] as const) {
    if (operations.isCurrent(signal)) published.push(name);
  }
  assert.deepEqual(published, ["second login"]);
  assert.ok(bootstrap.aborted && firstLogin.aborted && logout.aborted);
  operations.cancel();
  assert.equal(operations.isCurrent(secondLogin), false);
});
