import type { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import { translate as t } from "../../core/i18n";
import type { VerificationChannel, VerificationPurpose, VerificationResponse } from "../../core/api/types";

type GoogleProof = { code: string; codeVerifier: string };

function ensureActive(signal: AbortSignal): void {
  if (!signal.aborted) return;
  const error = new Error(t("The identity operation was canceled."));
  error.name = "AbortError";
  throw error;
}

export async function linkGoogleIdentity(api: DeyeSolarApi, signal: AbortSignal,
  authorize: () => Promise<GoogleProof | null>): Promise<boolean> {
  ensureActive(signal);
  const proof = await authorize();
  ensureActive(signal);
  if (!proof) return false;
  // Linking requires the initiating bearer. Keep its session on success or failure;
  // the extra session returned by the exchange is not needed to retain this account.
  await api.exchangeGoogleCode(proof.code, proof.codeVerifier, signal);
  ensureActive(signal);
  return true;
}

export class VerificationRequests {
  private api: DeyeSolarApi | null = null;
  private controller = new AbortController();

  replace(api: DeyeSolarApi | null): void {
    this.controller.abort();
    this.controller = new AbortController();
    this.api = api;
  }

  cancel(): void { this.controller.abort(); }

  async start(channel: VerificationChannel, destination: string, purpose: VerificationPurpose): Promise<VerificationResponse> {
    const api = this.api;
    const signal = this.controller.signal;
    ensureActive(signal);
    if (!api) throw new Error(t("Enter a valid server URL before requesting a code."));
    const response = await api.startVerification(channel, destination, purpose, signal);
    ensureActive(signal);
    return response;
  }
}
