import assert from "node:assert/strict";
import { test } from "node:test";
import { googleCallbackCode, googleStartUrl } from "../src/features/auth/googleFlow";

test("Google callback requires the exact app origin and a single matching state", () => {
  assert.equal(googleCallbackCode("deyesolar://auth/callback?code=ABC_def-123&state=nonce", "nonce"), "ABC_def-123");
  for (const url of [
    "https://auth/callback?code=x&state=nonce", "deyesolar://other/callback?code=x&state=nonce",
    "deyesolar://auth/other?code=x&state=nonce", "deyesolar://auth/callback?code=x&state=other",
    "deyesolar://auth/callback?code=x&state=nonce&state=nonce", "deyesolar://auth/callback?code=x&code=y&state=nonce",
    "deyesolar://auth/callback?code=x&state=nonce#fragment", "deyesolar://user@auth/callback?code=x&state=nonce"
  ]) assert.throws(() => googleCallbackCode(url, "nonce"));
});

test("Google tickets and provider failures never become an unchecked session", () => {
  assert.throws(() => googleCallbackCode("deyesolar://auth/callback?error=link_required&state=nonce", "nonce"));
  assert.throws(() => googleCallbackCode("deyesolar://auth/callback?code=%20secret%0A&state=nonce", "nonce"));
  assert.throws(() => googleCallbackCode("deyesolar://auth/callback?state=nonce", "nonce"));
});

test("Google starts only on the configured HTTPS server", () => {
  assert.equal(googleStartUrl("https://solar.example/base/", "a_b-c", "nonce"), "https://solar.example/base/auth/google?mobile=true&codeChallenge=a_b-c&state=nonce");
  assert.throws(() => googleStartUrl("http://solar.example", "challenge", "nonce"));
});
