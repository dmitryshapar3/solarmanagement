import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import path from "node:path";
import React from "react";
import { act, create } from "react-test-renderer";
import { build } from "esbuild";

test("account security keeps a fresh OTP proof and ignores a late credential response after account replacement", async () => {
  let completeRevoke!: () => void;
  const revocation = new Promise<void>(resolve => { completeRevoke = resolve; });
  const calls: unknown[] = [];
  let logoutCount = 0;
  let confirmDelete: (() => void) | undefined;
  let deleteCount = 0;
  const state = {
    auth: { username: "owner", apiBaseUrl: "https://solar.example", isDemo: false, logout: async () => { logoutCount++; },
      api: { accountSecurity: {
        startProof: async (channel: string, destination: string) => { calls.push([channel, destination]); return { verificationId: "one-time-proof", expiresAt: "2026-10-05T12:10:00Z", retryAfterSeconds: 60 }; },
        revokeAll: async (proof: unknown) => { calls.push(proof); await revocation; },
        deleteAccount: async () => { deleteCount++; }
      } } },
    share: async () => {}, alert: (_title: string, _message: string, actions: { text: string; onPress?: () => void }[]) => { confirmDelete = actions.find(action => action.text === "Delete")?.onPress; }
  };
  const globals = globalThis as typeof globalThis & { __solarSecurityCard?: typeof state; IS_REACT_ACT_ENVIRONMENT?: boolean };
  globals.__solarSecurityCard = state; globals.IS_REACT_ACT_ENVIRONMENT = true;
  let renderer: ReturnType<typeof create> | undefined;
  try {
    const bundle = await build({
      stdin: { contents: 'export { AccountSecurityCard } from "./src/features/auth/AccountSecurityCard";', resolveDir: process.cwd(), loader: "ts" },
      bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"],
      plugins: [{ name: "card-boundaries", setup(builder) {
        builder.onResolve({ filter: /^(react-native)$|(?:^|\/)application\/(AuthContext|LanguageContext)$|(?:^|\/)core\/components$/ }, args => ({ path: args.path, namespace: "card-test" }));
        builder.onLoad({ filter: /.*/, namespace: "card-test" }, args => {
          const state = "globalThis.__solarSecurityCard";
          return { loader: "js", contents: args.path === "react-native" ? `export const Text="Text", View="View", Alert={alert: (...args)=>${state}.alert(...args)}, Share={share:(...args)=>${state}.share(...args)};`
            : args.path.endsWith("AuthContext") ? `export const useAuth=()=>${state}.auth;`
            : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({t:text=>text});'
            : 'import React from "react"; export const AppButton=props=>React.createElement("button",props,props.label); export const Card="Card", SectionTitle="SectionTitle", ErrorBanner="ErrorBanner", TextField="TextField";' };
        });
      } }]
    });
    const module = { exports: {} as any };
    new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
    const { AccountSecurityCard } = module.exports;
    await act(async () => { renderer = create(React.createElement(AccountSecurityCard)); });
    const field = (label: string) => renderer!.root.findAllByType("TextField").find(item => item.props.label === label)!;
    const button = (label: string) => renderer!.root.findAllByType("button").find(item => item.props.label === label)!;
    await act(async () => { field("Verified email or phone").props.onChangeText("owner@example.com"); });
    await act(async () => { button("Send verification code").props.onPress(); });
    assert.deepEqual(calls, [["email", "owner@example.com"]]);
    assert.ok(field("Verification code"), "issued proof remains visible after request completion");
    await act(async () => { field("Verification code").props.onChangeText("123456"); });
    assert.equal(button("Sign out all devices").props.disabled, false);
    await act(async () => { button("Delete account and owned installations").props.onPress(); });
    assert.ok(confirmDelete);
    await act(async () => { button("Sign out all devices").props.onPress(); });
    assert.deepEqual(calls[1], { verificationId: "one-time-proof", code: "123456" });
    await act(async () => { state.auth.username = "replacement-owner"; renderer!.update(React.createElement(AccountSecurityCard)); });
    assert.equal(field("Current password").props.value, "");
    assert.equal(button("Sign out all devices").props.disabled, true);
    await act(async () => { completeRevoke(); await revocation; });
    assert.equal(logoutCount, 0, "late completion cannot sign out the replacement account");
    await act(async () => { confirmDelete!(); });
    assert.equal(deleteCount, 0, "old destructive confirmation cannot act on the replacement account");
    assert.equal(renderer!.root.findByType("ErrorBanner").props.message, null);
  } finally {
    await act(async () => { renderer?.unmount(); });
    delete globals.__solarSecurityCard; delete globals.IS_REACT_ACT_ENVIRONMENT;
  }
});
