import type { ApiClient } from "./ApiClient";
import type { VerificationChannel, VerificationResponse } from "./types";
export type AccountSecurityProof = { currentPassword?: string; verificationId?: string; code?: string };
export type AccountPermissions = { role: string | null; permissions: ("Read" | "ManageRules" | "ControlDevices" | "ManageSettings" | "ManageIntegrations")[] };
export type AccountExport = { exportedAt: string; account: { id: string; userName: string; email: string | null; phoneNumber: string | null }; memberships: { installationId: string; role: string }[] } & Record<string, unknown>;
// Fresh-proof failures can return 401 without invalidating an otherwise valid session.
export class AccountSecurityApi {
  constructor(private readonly client: Pick<ApiClient, "request">) {}
  getPermissions(signal?: AbortSignal): Promise<AccountPermissions> {
    return this.client.request("/api/auth/security/permissions", { signal });
  }
  startProof(channel: VerificationChannel, destination: string, signal?: AbortSignal): Promise<VerificationResponse> {
    return this.client.request("/api/auth/security/verification/start", { method: "POST", body: { channel, destination }, signal });
  }
  changePassword(proof: AccountSecurityProof, newPassword: string): Promise<{ signedOut: true }> {
    return this.client.request("/api/auth/security/password", { method: "POST", body: { proof, newPassword }, skipUnauthorizedHandler: true });
  }
  revokeAll(proof: AccountSecurityProof): Promise<{ signedOut: true }> {
    return this.client.request("/api/auth/security/revoke-all", { method: "POST", body: proof, skipUnauthorizedHandler: true });
  }
  exportData(proof: AccountSecurityProof): Promise<AccountExport> {
    return this.client.request("/api/auth/security/export", { method: "POST", body: proof, skipUnauthorizedHandler: true });
  }
  deleteAccount(proof: AccountSecurityProof): Promise<{ deleted: true }> {
    return this.client.request("/api/auth/security/delete", { method: "POST", body: proof, skipUnauthorizedHandler: true });
  }
}
