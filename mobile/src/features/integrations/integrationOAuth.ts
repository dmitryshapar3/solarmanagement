import { translate as t } from "../../core/i18n";
import type { IntegrationApi, IntegrationConfigurationChange, IntegrationOAuthStart, IntegrationOAuthStatus } from "../../core/api/IntegrationApi";

export type IntegrationOAuthBrowser = {
  open(url: string, returnUri: string, signal: AbortSignal): Promise<{ type: string; url?: string }>;
  dismiss(): void;
};

let nativeBrowserModule: typeof import("expo-web-browser") | undefined;
const nativeBrowser: IntegrationOAuthBrowser = {
  async open(url, returnUri, signal) {
    const browser = await import("expo-web-browser");
    if (signal.aborted) throw canceled();
    nativeBrowserModule = browser;
    return browser.openAuthSessionAsync(url, returnUri, { preferEphemeralSession: true });
  },
  dismiss() {
    try { nativeBrowserModule?.dismissAuthSession(); } catch { /* Android cancels locally through the session fence. */ }
  }
};

let browserOwner: symbol | null = null;
const mobileReturnUri = "deyesolar://integration-oauth";

export function validIntegrationOAuthCallback(url: string, start: IntegrationOAuthStart): boolean {
  try {
    const callback = new URL(url);
    const expected = new URL(mobileReturnUri);
    return start.returnUri === mobileReturnUri && callback.protocol === expected.protocol && callback.hostname === expected.hostname
      && callback.pathname === expected.pathname && !callback.port && !callback.username && !callback.password && !url.includes("#")
      && callback.searchParams.getAll("flowId").length === 1 && callback.searchParams.get("flowId") === start.flowId
      && callback.searchParams.getAll("returnNonce").length === 1 && callback.searchParams.get("returnNonce") === start.returnNonce
      && [...callback.searchParams.keys()].every(key => key === "flowId" || key === "returnNonce");
  } catch { return false; }
}

/** A callback only identifies the flow; the authenticated server remains the authority for its result. */
export async function authorizeIntegration(
  api: IntegrationApi, instanceId: string, draft: IntegrationConfigurationChange, signal: AbortSignal,
  browser: IntegrationOAuthBrowser = nativeBrowser
): Promise<IntegrationOAuthStatus | null> {
  if (browserOwner) throw new Error(t("Another authorization window is already open."));
  const owner = Symbol("integration authorization");
  browserOwner = owner;
  const epoch = api.sessionEpoch;
  const controller = new AbortController();
  let start: IntegrationOAuthStart | undefined;
  let opened = false;
  let dismissed = false;
  let accepted = false;
  let expired = false;
  let expirationTimer: ReturnType<typeof setTimeout> | undefined;
  let pollingTimer: ReturnType<typeof setTimeout> | undefined;
  let pollingTimedOut = false;
  const cancel = () => {
    controller.abort();
    if (opened && !dismissed && browserOwner === owner) { dismissed = true; browser.dismiss(); }
  };
  signal.addEventListener("abort", cancel, { once: true });
  const unsubscribe = api.onSessionChange(cancel);
  if (signal.aborted || api.sessionEpoch !== epoch) cancel();
  try {
    if (controller.signal.aborted) throw canceled();
    start = await api.startOAuth(instanceId, draft, controller.signal);
    if (controller.signal.aborted || api.sessionEpoch !== epoch) throw canceled();
    const authorization = new URL(start.authorizationUrl);
    if (authorization.protocol !== "https:" || authorization.username || authorization.password || start.authorizationUrl.includes("#")
      || start.returnUri !== mobileReturnUri || !start.flowId || !start.returnNonce
      || !Number.isFinite(Date.parse(start.expiresAt)) || Date.parse(start.expiresAt) <= Date.now()) {
      throw new Error(t("The server returned an unsafe or expired authorization request."));
    }
    expirationTimer = setTimeout(() => { expired = true; cancel(); }, Math.min(2147483647, Date.parse(start.expiresAt) - Date.now()));
    opened = true;
    let result: { type: string; url?: string };
    try { result = await abortable(browser.open(start.authorizationUrl, start.returnUri, controller.signal), controller.signal); }
    catch (error) {
      if (controller.signal.aborted) throw error;
      throw new Error(t("The authorization browser could not open. Close any existing authorization window and try again."));
    }
    opened = false;
    if (controller.signal.aborted || api.sessionEpoch !== epoch) throw canceled();
    if (result.type !== "success") return null;
    if (!result.url || !validIntegrationOAuthCallback(result.url, start)) throw new Error(t("Authorization returned an invalid callback. No settings were changed."));
    pollingTimer = setTimeout(() => { pollingTimedOut = true; cancel(); }, 10000);
    // Poll only the authenticated result, never repeat the provider authorization or save settings.
    for (let attempt = 0; attempt < 20; ++attempt) {
      const status = await api.getOAuth(instanceId, start.flowId, controller.signal);
      if (controller.signal.aborted || api.sessionEpoch !== epoch) throw canceled();
      if (status.flowId !== start.flowId) throw new Error(t("Authorization returned a different flow."));
      if (status.status === "ready") { accepted = true; return status; }
      if (status.status !== "pending" && status.status !== "exchanging") throw new Error(t("Authorization failed or expired. Authorize again."));
      await pause(500, controller.signal);
    }
    throw new Error(t("Authorization is still pending. Authorize again after the current request expires."));
  } catch (error) {
    if (expired) throw new Error(t("The authorization request expired. Authorize again."));
    if (pollingTimedOut) throw new Error(t("The authorization result took too long to respond. Authorize again."));
    throw error;
  } finally {
    clearTimeout(expirationTimer);
    clearTimeout(pollingTimer);
    signal.removeEventListener("abort", cancel);
    unsubscribe();
    if (browserOwner === owner) browserOwner = null;
    // An account switch must never send an old flow's cancellation under the new account's credentials.
    if (start && !accepted && api.sessionEpoch === epoch) {
      void api.cancelOAuth(instanceId, start.flowId).catch(() => {});
    }
  }
}

async function pause(delay: number, signal: AbortSignal): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try { await abortable(new Promise<void>(resolve => { timer = setTimeout(resolve, delay); }), signal); }
  finally { clearTimeout(timer); }
}

function canceled(): Error { const error = new Error(t("Authorization was canceled.")); error.name = "AbortError"; return error; }

async function abortable<T>(pending: Promise<T>, signal: AbortSignal): Promise<T> {
  let abort: () => void = () => {};
  const cancellation = new Promise<never>((_, reject) => {
    abort = () => reject(canceled());
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
  });
  try { return await Promise.race([pending, cancellation]); }
  finally { signal.removeEventListener("abort", abort); }
}
