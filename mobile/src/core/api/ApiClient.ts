export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

export type ApiTransport = (url: string, options: {
  method: string;
  headers: Record<string, string>;
  body?: string;
  signal: AbortSignal;
  redirect: "error";
  credentials: "omit";
}) => Promise<{ status: number; ok: boolean; text(): Promise<string> }>;

export type ApiClientOptions = {
  baseUrl: string;
  token?: string | null;
  onUnauthorized?: () => void;
  transport?: ApiTransport;
};

export type RequestOptions = {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined | null>;
  signal?: AbortSignal;
  timeoutMs?: number;
  // Wrong login credentials must not expire an unrelated session.
  skipUnauthorizedHandler?: boolean;
};

export class ApiClient {
  private baseUrl: string;
  private token?: string | null;
  private revision = 0;
  private readonly requests = new Set<AbortController>();
  private readonly onUnauthorized?: () => void;
  private readonly transport: ApiTransport;

  constructor(options: ApiClientOptions) {
    this.baseUrl = normalizeBaseUrl(options.baseUrl);
    this.token = options.token;
    this.onUnauthorized = options.onUnauthorized;
    this.transport = options.transport ?? ((url, init) => fetch(url, init));
  }

  setBaseUrl(baseUrl: string) {
    const normalized = normalizeBaseUrl(baseUrl);
    if (normalized === this.baseUrl) return;
    // Bearer tokens belong to the configured endpoint, including its path.
    this.baseUrl = normalized;
    this.setToken(null);
  }

  setToken(token?: string | null) {
    this.revision++;
    this.token = token;
    for (const request of this.requests) request.abort();
  }

  async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    if (!path.startsWith("/") || path.startsWith("//") || path.includes("\\")) {
      throw new Error("API requests must use a relative server path.");
    }
    const timeoutMs = options.timeoutMs ?? 15000;
    if (!Number.isFinite(timeoutMs) || timeoutMs < 1 || timeoutMs > 60000) {
      throw new Error("The request timeout must be between 1 and 60000 milliseconds.");
    }
    const revision = this.revision;
    const token = this.token;
    const url = `${this.baseUrl}${path}${buildQuery(options.query)}`;
    const controller = new AbortController();
    this.requests.add(controller);
    let timedOut = false;
    const cancel = () => controller.abort();
    options.signal?.addEventListener("abort", cancel, { once: true });
    if (options.signal?.aborted) controller.abort();
    const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
    const headers: Record<string, string> = { Accept: "application/json" };
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    if (token) headers.Authorization = `Bearer ${token}`;
    let rejectAborted: () => void = () => {};
    const aborted = new Promise<never>((_, reject) => {
      rejectAborted = () => {
        const error = new Error(timedOut ? "The server took too long to respond. Please try again." : "The request was canceled.");
        error.name = timedOut ? "TimeoutError" : "AbortError";
        reject(error);
      };
      controller.signal.addEventListener("abort", rejectAborted, { once: true });
      if (controller.signal.aborted) rejectAborted();
    });
    const send = async (): Promise<T> => {
      if (controller.signal.aborted) throw new Error("The request was canceled.");
      const response = await this.transport(url, {
        method: options.method ?? "GET",
        headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
        signal: controller.signal,
        redirect: "error",
        credentials: "omit"
      });
      const text = await response.text();
      // A late response or 401 must never affect a replacement session.
      if (controller.signal.aborted || revision !== this.revision) throw new Error("The request was canceled.");
      let payload: unknown;
      if (text) {
        try { payload = JSON.parse(text); }
        catch {
          if (response.ok) {
            throw new ApiError(response.status, "The server returned an invalid API response. Check the server URL and try again.");
          }
        }
      }
      if (response.status === 401) {
        if (options.skipUnauthorizedHandler) throw new ApiError(401, extractErrorMessage(payload, 401));
        if (token) this.onUnauthorized?.();
        throw new ApiError(401, "Session expired. Sign in again.");
      }
      if (!response.ok) throw new ApiError(response.status, extractErrorMessage(payload, response.status));
      return payload as T;
    };
    try {
      return await Promise.race([aborted, send()]);
    } finally {
      clearTimeout(timer);
      options.signal?.removeEventListener("abort", cancel);
      controller.signal.removeEventListener("abort", rejectAborted);
      this.requests.delete(controller);
    }
  }
}

export function normalizeBaseUrl(value: string): string {
  let url: URL;
  try { url = new URL(value.trim()); }
  catch { throw new Error("Enter a valid server URL, for example https://solar.dshapar.com."); }
  if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
    throw new Error("Use an HTTP or HTTPS server URL without credentials, a query, or a fragment.");
  }
  return `${url.origin}${url.pathname.replace(/\/+$/, "")}`;
}

function buildQuery(query?: RequestOptions["query"]): string {
  const params = new URLSearchParams();
  Object.entries(query ?? {}).forEach(([key, value]) => {
    if (value !== undefined && value !== null) params.append(key, String(value));
  });
  const text = params.toString();
  return text ? `?${text}` : "";
}

function extractErrorMessage(payload: unknown, status: number): string {
  if (typeof payload === "object" && payload !== null && "message" in payload) {
    const message = (payload as { message?: unknown }).message;
    if (typeof message === "string" && message.trim()) return message;
  }
  // Do not expose proxy HTML, stack traces, or echoed credentials in a sign-in error.
  return `Request failed with HTTP ${status}.`;
}
