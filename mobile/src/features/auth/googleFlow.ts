import { translate as t } from "../../core/i18n";
import { normalizeBaseUrl } from "../../core/api/ApiClient";

export const GOOGLE_CALLBACK = "deyesolar://auth/callback";

export function googleStartUrl(baseUrl: string, codeChallenge: string, state: string): string {
  const server = normalizeBaseUrl(baseUrl);
  if (!server.startsWith("https://")) throw new Error(t("Google sign-in requires a secure HTTPS server."));
  return `${server}/auth/google?mobile=true&codeChallenge=${encodeURIComponent(codeChallenge)}&state=${encodeURIComponent(state)}`;
}

export function googleCallbackCode(callback: string, expectedState: string): string {
  const url = new URL(callback);
  if (url.protocol !== "deyesolar:" || url.hostname !== "auth" || url.pathname !== "/callback"
    || url.username || url.password || url.port || url.hash
    || url.searchParams.getAll("state").length !== 1 || url.searchParams.get("state") !== expectedState) {
    throw new Error(t("The sign-in response could not be verified. Please start again."));
  }
  const errors = url.searchParams.getAll("error");
  if (errors.length > 0) {
    if (errors.length !== 1 || !errors[0] || url.searchParams.has("code")) {
      throw new Error(t("The sign-in response was invalid. Please start again."));
    }
    throw new Error(googleFailureMessage(errors[0]));
  }
  const code = url.searchParams.get("code");
  if (url.searchParams.getAll("code").length !== 1 || !code || code.length > 1024 || !/^[A-Za-z0-9_-]+$/.test(code)) {
    throw new Error(t("The sign-in response was invalid. Please start again."));
  }
  return code;
}

function googleFailureMessage(code: string): string {
  switch (code) {
    case "registration_disabled":
      return t("This Google account is not linked, and new account registration is disabled. Sign in to your existing account with your password or a sign-in code, then open Settings > Link Google account.");
    case "link_required":
      return t("Sign in to your existing account with your password or a sign-in code, then open Settings > Link Google account.");
    case "link_conflict":
      return t("This Google account is already linked to another account. Sign in to that account or choose a different Google account.");
    case "link_failed":
      return t("Google could not be linked to this account. Sign in again, then open Settings > Link Google account to retry.");
    case "account_unavailable":
      return t("This account is currently unavailable. Contact the administrator for help.");
    case "google_unavailable":
      return t("Google sign-in is not configured on this server. Use another sign-in method or contact the administrator.");
    case "google_failed":
    default:
      return t("Google sign-in could not be completed. Please try again or use another sign-in method.");
  }
}
