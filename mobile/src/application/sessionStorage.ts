import { translate as t } from "../core/i18n";
import { normalizeBaseUrl } from "../core/api/ApiClient";

export type StoredSession = { baseUrl: string; token: string; username: string };
export type StringStorage = {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
};

export const sessionKeys = {
  baseUrl: "deyeSolar.mobile.apiBaseUrl",
  legacyToken: "deyeSolar.mobile.token",
  legacyUsername: "deyeSolar.mobile.username",
  secureSession: "deyeSolar.mobile.session.v2",
  disabled: "deyeSolar.mobile.sessionDisabled"
};

export class SessionStorage {
  private queue: Promise<unknown> = Promise.resolve();
  private memory: StoredSession | null = null;

  constructor(private readonly preferences: StringStorage, private readonly secure: StringStorage | null) {}

  load(defaultBaseUrl: string): Promise<{ baseUrl: string; session: StoredSession | null }> {
    return this.enqueue(async () => {
      const storedBase = await this.preferences.getItem(sessionKeys.baseUrl);
      let baseUrl = defaultBaseUrl;
      let hasStoredBinding = false;
      try {
        baseUrl = normalizeBaseUrl(storedBase || defaultBaseUrl);
        hasStoredBinding = Boolean(storedBase);
      } catch { /* Invalid legacy settings cannot bind a token. */ }
      // The old app could change servers without changing its plaintext token, so its endpoint binding is untrustworthy.
      await this.removeLegacy();
      if (await this.preferences.getItem(sessionKeys.disabled)) {
        await this.clearCore();
        return { baseUrl, session: null };
      }
      if (!this.secure) return { baseUrl, session: this.memory?.baseUrl === baseUrl ? this.memory : null };
      const raw = await this.secure.getItem(sessionKeys.secureSession);
      let session: StoredSession | null = null;
      try {
        const parsed: unknown = raw ? JSON.parse(raw) : null;
        if (isSession(parsed) && hasStoredBinding && parsed.baseUrl === baseUrl) session = parsed;
      } catch { /* Corrupt records require a new sign-in. */ }
      if (raw && !session) await this.secure.removeItem(sessionKeys.secureSession);
      return { baseUrl, session };
    });
  }

  save(session: StoredSession): Promise<void> {
    if (!isSession(session)) return Promise.reject(new Error(t("The server returned an invalid session.")));
    const stored = { baseUrl: session.baseUrl, token: session.token, username: session.username };
    return this.enqueue(async () => {
      this.memory = null;
      try {
        // A failed write cannot revive a previous Keychain session on the next launch.
        await this.preferences.setItem(sessionKeys.disabled, "1");
        await this.removeLegacy();
        if (this.secure) await this.secure.setItem(sessionKeys.secureSession, JSON.stringify(stored));
        await this.preferences.setItem(sessionKeys.baseUrl, stored.baseUrl);
        await this.preferences.removeItem(sessionKeys.disabled);
        this.memory = stored;
      } catch {
        if (this.secure) await this.secure.removeItem(sessionKeys.secureSession).catch(() => {});
        throw new Error(t("Unable to save this session securely. Please try again."));
      }
    });
  }

  clear(): Promise<void> { return this.enqueue(() => this.clearCore()); }

  changeBaseUrl(baseUrl: string): Promise<void> {
    const normalized = normalizeBaseUrl(baseUrl);
    return this.enqueue(async () => {
      await this.clearCore();
      await this.preferences.setItem(sessionKeys.baseUrl, normalized);
    });
  }

  private async clearCore(): Promise<void> {
    this.memory = null;
    const outcomes = await Promise.allSettled([
      this.preferences.setItem(sessionKeys.disabled, "1"),
      this.removeLegacy(),
      this.secure?.removeItem(sessionKeys.secureSession) ?? Promise.resolve()
    ]);
    if (outcomes.some(result => result.status === "rejected")) {
      throw new Error(t("The session is signed out, but local session storage could not be cleared. Please try again."));
    }
  }

  private async removeLegacy(): Promise<void> {
    const outcomes = await Promise.allSettled([
      this.preferences.removeItem(sessionKeys.legacyToken),
      this.preferences.removeItem(sessionKeys.legacyUsername)
    ]);
    if (outcomes.some(result => result.status === "rejected")) throw new Error(t("Unable to clear old session storage."));
  }

  private enqueue<T>(action: () => Promise<T>): Promise<T> {
    const work = this.queue.then(action);
    this.queue = work.catch(() => {});
    return work;
  }
}

function isSession(value: unknown): value is StoredSession {
  if (typeof value !== "object" || value === null) return false;
  const session = value as Partial<StoredSession>;
  return typeof session.baseUrl === "string" && normalizeBaseUrl(session.baseUrl) === session.baseUrl
    && typeof session.token === "string" && session.token.trim().length > 0
    && typeof session.username === "string" && session.username.trim().length > 0;
}

export class SessionOperations {
  private current = new AbortController();

  capture(): AbortSignal { return this.current.signal; }

  begin(): AbortSignal {
    this.current.abort();
    this.current = new AbortController();
    return this.current.signal;
  }

  isCurrent(signal: AbortSignal): boolean { return signal === this.current.signal && !signal.aborted; }
  cancel(): void { this.current.abort(); }
}
