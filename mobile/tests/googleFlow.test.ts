import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { googleCallbackCode, googleStartUrl } from "../src/features/auth/googleFlow";
import { languages, setLocale, translate } from "../src/core/i18n";

beforeEach(() => setLocale("en"));

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

const callbackFailures = [
  ["registration_disabled", "This Google account is not linked, and new account registration is disabled. Sign in to your existing account with your password or a sign-in code, then open Settings > Link Google account."],
  ["link_required", "Sign in to your existing account with your password or a sign-in code, then open Settings > Link Google account."],
  ["link_conflict", "This Google account is already linked to another account. Sign in to that account or choose a different Google account."],
  ["link_failed", "Google could not be linked to this account. Sign in again, then open Settings > Link Google account to retry."],
  ["account_unavailable", "This account is currently unavailable. Contact the administrator for help."],
  ["google_failed", "Google sign-in could not be completed. Please try again or use another sign-in method."],
  ["google_unavailable", "Google sign-in is not configured on this server. Use another sign-in method or contact the administrator."]
] as const;

for (const [code, message] of callbackFailures) {
  test(`Google callback ${code} explains the available next step without returning a ticket`, () => {
    assert.throws(() => googleCallbackCode(`deyesolar://auth/callback?error=${code}&state=nonce`, "nonce"), { message });
  });
}

test("Unknown Google callback errors use fixed text without reflecting arbitrary server data", () => {
  for (const error of ["new_provider_error", "untrusted private value\n<script>", "__proto__", "toString"]) {
    assert.throws(() => googleCallbackCode(`deyesolar://auth/callback?error=${encodeURIComponent(error)}&state=nonce`, "nonce"), {
      message: "Google sign-in could not be completed. Please try again or use another sign-in method."
    });
  }
});

test("Malformed Google callback errors cannot select a message or return a success ticket", () => {
  for (const query of [
    "error=link_required&error=registration_disabled", "error=google_failed&error=google_failed", "error=",
    "code=ABC_def-123&error=registration_disabled", "error=link_required&code=", "error=link_failed&code=x&code=y"
  ]) {
    assert.throws(() => googleCallbackCode(`deyesolar://auth/callback?${query}&state=nonce`, "nonce"), {
      message: "The sign-in response was invalid. Please start again."
    });
  }
});

test("Google callback origin and state validation take precedence over provider errors", () => {
  for (const callback of [
    "deyesolar://auth/callback?error=registration_disabled&state=other",
    "deyesolar://auth/callback?error=link_required&state=nonce&state=nonce",
    "deyesolar://auth/callback?error=google_failed&error=google_failed&state=other",
    "deyesolar://auth/callback?code=valid&error=google_failed&state=other",
    "https://auth/callback?error=registration_disabled&state=nonce",
    "deyesolar://other/callback?error=registration_disabled&state=nonce"
  ]) {
    assert.throws(() => googleCallbackCode(callback, "nonce"), {
      message: "The sign-in response could not be verified. Please start again."
    });
  }
});

test("Google callback guidance uses the selected language without changing callback validation", () => {
  try {
    for (const language of languages) {
      setLocale(language.code);
      for (const [code, english] of callbackFailures) {
        assert.throws(() => googleCallbackCode(`deyesolar://auth/callback?error=${code}&state=nonce`, "nonce"), error => {
          assert.ok(error instanceof Error);
          assert.equal(error.message, translate(english));
          if (language.code !== "en") assert.notEqual(error.message, english);
          return true;
        });
      }
      assert.equal(googleCallbackCode("deyesolar://auth/callback?code=ABC_def-123&state=nonce", "nonce"), "ABC_def-123");
      assert.throws(() => googleCallbackCode("deyesolar://auth/callback?error=registration_disabled&state=other", "nonce"), {
        message: translate("The sign-in response could not be verified. Please start again.")
      });
    }
    setLocale("pl");
    assert.throws(() => googleCallbackCode("deyesolar://auth/callback?error=link_required&state=nonce", "nonce"), {
      message: "Zaloguj się na istniejące konto hasłem lub kodem logowania, a następnie otwórz Ustawienia > Połącz konto Google."
    });
  } finally { setLocale("en"); }
});

test("Google starts only on the configured HTTPS server", () => {
  assert.equal(googleStartUrl("https://solar.example/base/", "a_b-c", "nonce"), "https://solar.example/base/auth/google?mobile=true&codeChallenge=a_b-c&state=nonce");
  assert.throws(() => googleStartUrl("http://solar.example", "challenge", "nonce"));
});
