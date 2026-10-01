import { normalizeBaseUrl } from "../../core/api/ApiClient";

export const GOOGLE_CALLBACK = "deyesolar://auth/callback";

export function googleStartUrl(baseUrl: string, codeChallenge: string, state: string): string {
  const server = normalizeBaseUrl(baseUrl);
  if (!server.startsWith("https://")) throw new Error("Google sign-in requires a secure HTTPS server.");
  return `${server}/auth/google?mobile=true&codeChallenge=${encodeURIComponent(codeChallenge)}&state=${encodeURIComponent(state)}`;
}

export function googleCallbackCode(callback: string, expectedState: string): string {
  const url = new URL(callback);
  if (url.protocol !== "deyesolar:" || url.hostname !== "auth" || url.pathname !== "/callback"
    || url.username || url.password || url.port || url.hash
    || url.searchParams.getAll("state").length !== 1 || url.searchParams.get("state") !== expectedState) {
    throw new Error("The sign-in response could not be verified. Please start again.");
  }
  if (url.searchParams.has("error")) throw new Error("Google sign-in could not be completed. To link an existing account, sign in first and use Settings.");
  const code = url.searchParams.get("code");
  if (url.searchParams.getAll("code").length !== 1 || !code || code.length > 1024 || !/^[A-Za-z0-9_-]+$/.test(code)) {
    throw new Error("The sign-in response was invalid. Please start again.");
  }
  return code;
}
