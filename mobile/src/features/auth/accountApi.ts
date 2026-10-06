import type { DeyeSolarApi } from "../../core/api/DeyeSolarApi";
import type { AccountSecurityProof } from "../../core/api/AccountSecurityApi";
import type { VerificationChannel } from "../../core/api/types";

export type AccountProfile = { displayName: string | null; verifiedEmail: string | null; verifiedPhone: string | null };
export type AccountPreferences = { displayTimeZoneId: string | null };
export type AccountMethods = { email: string | null; phone: string | null; googleLinked: boolean; appleLinked?: boolean; hasPassword?: boolean };
export type AccountSession = { id: string; platform: string | null; client: string | null; lastSeenAt: string | null; createdAt: string; isCurrent: boolean };
export type ExternalProofStart = { flowId: string; authorizationUrl: string | null; rawNonce: string | null; expiresAt: string };
export const accountApi = (api: DeyeSolarApi) => ({
  profile: (signal?: AbortSignal) => api.request<AccountProfile>("/api/account/profile", { signal }),
  saveProfile: (displayName: string, signal?: AbortSignal) => api.request<AccountProfile>("/api/account/profile", { method: "PATCH", body: { displayName }, signal }),
  preferences: (signal?: AbortSignal) => api.request<AccountPreferences>("/api/account/preferences", { signal }),
  savePreferences: (displayTimeZoneId: string | null, signal?: AbortSignal) => api.request<AccountPreferences>("/api/account/preferences", { method: "PUT", body: { displayTimeZoneId }, signal }),
  methods: (signal?: AbortSignal) => api.request<AccountMethods>("/api/auth/identities", { signal }),
  sessions: (signal?: AbortSignal) => api.request<{ sessions: AccountSession[] }>("/api/account/sessions", { signal }),
  revokeSession: (id: string, proof: AccountSecurityProof, signal?: AbortSignal) => api.request("/api/account/sessions/" + encodeURIComponent(id) + "/revoke", { method: "POST", body: { proof }, signal, skipUnauthorizedHandler: true }),
  revokeOthers: (proof: AccountSecurityProof, signal?: AbortSignal) => api.request("/api/account/sessions/revoke-others", { method: "POST", body: { proof }, signal, skipUnauthorizedHandler: true }),
  unlink: (provider: "Apple" | "Google", proof: AccountSecurityProof, signal?: AbortSignal) => api.request<AccountMethods>("/api/account/identities/" + provider.toLowerCase() + "/unlink", { method: "POST", body: { proof }, signal, skipUnauthorizedHandler: true }),
  startContact: (channel: VerificationChannel, destination: string, proof: AccountSecurityProof, signal?: AbortSignal) => api.request<{ challengeId: string; expiresAt: string; retryAfterSeconds: number }>("/api/account/contacts/change/start", { method: "POST", body: { channel, destination, proof }, signal, skipUnauthorizedHandler: true }),
  completeContact: (challengeId: string, code: string, signal?: AbortSignal) => api.request<AccountProfile>("/api/account/contacts/change/complete", { method: "POST", body: { challengeId, code }, signal, skipUnauthorizedHandler: true }),
  startExternal: (provider: "Apple" | "Google", operation: string, codeChallenge?: string, state?: string, signal?: AbortSignal) => api.request<ExternalProofStart>("/api/account/proof/external/start", { method: "POST", body: { provider, operation, codeChallenge, state }, signal, skipUnauthorizedHandler: true }),
  completeExternal: (flowId: string, identity?: { identityToken: string; rawNonce: string; authorizationCode: string }, signal?: AbortSignal) => api.request<{ externalProofId: string; expiresAt: string }>("/api/account/proof/external/complete", { method: "POST", body: { flowId, ...identity }, signal, skipUnauthorizedHandler: true }),
  startAppleLink: (proof: AccountSecurityProof, signal?: AbortSignal) => api.request<{ flowId: string; rawNonce: string; expiresAt: string }>("/api/auth/apple/link/start", { method: "POST", body: { proof }, signal, skipUnauthorizedHandler: true }),
  finishAppleLink: (flowId: string, identity: { identityToken: string; rawNonce: string; authorizationCode: string }, signal?: AbortSignal) => api.request<AccountMethods>("/api/auth/apple/link/complete", { method: "POST", body: { flowId, ...identity }, signal, skipUnauthorizedHandler: true })
});
