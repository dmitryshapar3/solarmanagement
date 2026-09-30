import AsyncStorage from "@react-native-async-storage/async-storage";
import * as SecureStore from "expo-secure-store";
import { fetch as expoFetch } from "expo/fetch";
import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { Platform } from "react-native";
import { ApiClient, normalizeBaseUrl } from "../core/api/ApiClient";
import { DEFAULT_API_BASE_URL } from "../core/api/config";
import { DeyeSolarApi } from "../core/api/DeyeSolarApi";
import { DemoApiClient } from "../features/demo/DemoApiClient";
import { DEMO_API_BASE_URL, DEMO_USERNAME } from "../features/demo/fixtures";
import { SessionOperations, SessionStorage } from "./sessionStorage";

type LoginInput = { baseUrl: string; username: string; password: string };

type AuthContextValue = {
  api: DeyeSolarApi;
  apiBaseUrl: string;
  username: string | null;
  isAuthenticated: boolean;
  isDemo: boolean;
  isBootstrapping: boolean;
  authError: string | null;
  login: (input: LoginInput) => Promise<void>;
  enterDemo: () => Promise<void>;
  logout: () => Promise<void>;
  updateApiBaseUrl: (baseUrl: string) => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [apiBaseUrl, setApiBaseUrl] = useState(DEFAULT_API_BASE_URL);
  const [token, setToken] = useState<string | null>(null);
  const [username, setUsername] = useState<string | null>(null);
  const [isBootstrapping, setIsBootstrapping] = useState(true);
  const [authError, setAuthError] = useState<string | null>(null);
  const [demoApi, setDemoApi] = useState<DeyeSolarApi | null>(null);
  const demoApiRef = useRef<DeyeSolarApi | null>(null);
  const realSession = useRef<{ baseUrl: string; token: string } | null>(null);
  const unauthorized = useRef<() => void>(() => {});
  const mounted = useRef(true);
  const [operations] = useState(() => new SessionOperations());
  const [storage] = useState(() => new SessionStorage(AsyncStorage, Platform.OS === "web" ? null : {
    getItem: key => SecureStore.getItemAsync(key),
    setItem: (key, value) => SecureStore.setItemAsync(key, value, {
      keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY
    }),
    removeItem: key => SecureStore.deleteItemAsync(key)
  }));
  const [client] = useState(() => new ApiClient({
    baseUrl: DEFAULT_API_BASE_URL,
    // Expo fetch enforces redirect:error on iOS/Android; React Native's XHR fetch does not.
    transport: expoFetch,
    onUnauthorized: () => unauthorized.current()
  }));
  const realApi = useMemo(() => new DeyeSolarApi(client), [client]);

  const discardDemo = useCallback(() => {
    const previous = demoApiRef.current;
    demoApiRef.current = null;
    // Invalidate the old local client too, so retained screen callbacks cannot use it.
    if (previous) void previous.logout().catch(() => {});
  }, []);

  const beginSessionChange = useCallback(() => {
    const signal = operations.begin();
    discardDemo();
    setDemoApi(null);
    realSession.current = null;
    client.setToken(null);
    setToken(null);
    setUsername(null);
    setIsBootstrapping(false);
    setAuthError(null);
    return signal;
  }, [client, discardDemo, operations]);

  unauthorized.current = () => {
    if (!mounted.current) return;
    const signal = beginSessionChange();
    setAuthError("Session expired. Sign in again.");
    void storage.clear().catch(() => {
      if (mounted.current && operations.isCurrent(signal)) {
        setAuthError("Session expired. Local session storage could not be cleared; please sign in again.");
      }
    });
  };

  useEffect(() => {
    mounted.current = true;
    const signal = operations.begin();
    const current = () => mounted.current && operations.isCurrent(signal);
    async function loadSession() {
      try {
        const restored = await storage.load(DEFAULT_API_BASE_URL);
        if (!current()) return;
        client.setBaseUrl(restored.baseUrl);
        client.setToken(restored.session?.token);
        realSession.current = restored.session ? { baseUrl: restored.baseUrl, token: restored.session.token } : null;
        setApiBaseUrl(restored.baseUrl);
        setToken(restored.session?.token ?? null);
        setUsername(restored.session?.username ?? null);
        if (restored.session) {
          try {
            await client.request("/api/auth/session", { signal, timeoutMs: 3000 });
          } catch {
            // The matching 401 handler signs out. Offline startup keeps an already bound secure session.
          }
        }
      } catch {
        if (current()) {
          client.setToken(null);
          realSession.current = null;
          setToken(null);
          setUsername(null);
          setAuthError("Stored sign-in could not be loaded safely. Please sign in again.");
        }
      } finally {
        if (current()) setIsBootstrapping(false);
      }
    }
    void loadSession();
    return () => {
      mounted.current = false;
      operations.cancel();
      discardDemo();
      realSession.current = null;
      client.setToken(null);
    };
  }, [client, discardDemo, operations, storage]);

  const login = useCallback(async (input: LoginInput) => {
    const nextBaseUrl = normalizeBaseUrl(input.baseUrl);
    if (!input.username.trim() || !input.password) throw new Error("Enter your username and password.");
    const signal = beginSessionChange();
    client.setBaseUrl(nextBaseUrl);
    setApiBaseUrl(nextBaseUrl);
    const ensureCurrent = () => {
      if (!mounted.current || !operations.isCurrent(signal)) {
        const error = new Error("Sign-in was canceled.");
        error.name = "AbortError";
        throw error;
      }
    };
    try {
      await storage.changeBaseUrl(nextBaseUrl);
      ensureCurrent();
      const session = await realApi.login(input.username.trim(), input.password, signal);
      ensureCurrent();
      await storage.save({ baseUrl: nextBaseUrl, token: session.token, username: session.username });
      ensureCurrent();
      client.setToken(session.token);
      realSession.current = { baseUrl: nextBaseUrl, token: session.token };
      setToken(session.token);
      setUsername(session.username);
    } catch (error) {
      if (mounted.current && operations.isCurrent(signal)) {
        setAuthError(error instanceof Error ? error.message : "Unable to sign in.");
      }
      throw error;
    }
  }, [realApi, beginSessionChange, client, operations, storage]);

  const enterDemo = useCallback(async () => {
    const signal = beginSessionChange();
    try {
      // Clear any queued real-session writes before activating an in-memory demo.
      await storage.clear();
      if (!mounted.current || !operations.isCurrent(signal)) {
        const error = new Error("Opening the demo was canceled.");
        error.name = "AbortError";
        throw error;
      }
      const nextDemoApi = new DeyeSolarApi(new DemoApiClient());
      demoApiRef.current = nextDemoApi;
      setDemoApi(nextDemoApi);
    } catch (error) {
      if (mounted.current && operations.isCurrent(signal)) {
        setAuthError(error instanceof Error ? error.message : "Unable to open the demo.");
      }
      throw error;
    }
  }, [beginSessionChange, operations, storage]);

  const logout = useCallback(async () => {
    // Revocation uses a captured client so its eventual result cannot expire a later sign-in.
    const capturedSession = demoApiRef.current ? null : realSession.current;
    const revocation = capturedSession ? new ApiClient({ ...capturedSession, transport: expoFetch })
      .request("/api/auth/logout", { method: "POST", timeoutMs: 5000 }).catch(() => {}) : Promise.resolve();
    const signal = beginSessionChange();
    try {
      await storage.clear();
    } catch (error) {
      if (mounted.current && operations.isCurrent(signal)) {
        setAuthError(error instanceof Error ? error.message : "Unable to clear local session storage.");
      }
    }
    await revocation;
  }, [beginSessionChange, operations, storage]);

  const updateApiBaseUrl = useCallback(async (baseUrl: string) => {
    if (demoApiRef.current) throw new Error("Exit demo before changing the server address.");
    const nextBaseUrl = normalizeBaseUrl(baseUrl);
    if (nextBaseUrl === apiBaseUrl) return;
    const signal = beginSessionChange();
    client.setBaseUrl(nextBaseUrl);
    setApiBaseUrl(nextBaseUrl);
    try {
      await storage.changeBaseUrl(nextBaseUrl);
    } catch (error) {
      if (mounted.current && operations.isCurrent(signal)) {
        setAuthError(error instanceof Error ? error.message : "Unable to save the server address.");
      }
      throw error;
    }
  }, [apiBaseUrl, beginSessionChange, client, operations, storage]);

  return <AuthContext.Provider value={{
    api: demoApi ?? realApi,
    apiBaseUrl: demoApi ? DEMO_API_BASE_URL : apiBaseUrl,
    username: demoApi ? DEMO_USERNAME : username,
    isAuthenticated: Boolean(token) || Boolean(demoApi), isDemo: Boolean(demoApi), isBootstrapping, authError,
    login, enterDemo, logout, updateApiBaseUrl
  }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth must be used inside AuthProvider.");
  return context;
}
