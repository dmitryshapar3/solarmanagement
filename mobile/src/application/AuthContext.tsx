import AsyncStorage from "@react-native-async-storage/async-storage";
import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { Platform } from "react-native";
import { ApiClient, normalizeBaseUrl } from "../core/api/ApiClient";
import { DeyeSolarApi } from "../core/api/DeyeSolarApi";

type LoginInput = {
  baseUrl: string;
  username: string;
  password: string;
};

type AuthContextValue = {
  api: DeyeSolarApi;
  apiBaseUrl: string;
  username: string | null;
  isAuthenticated: boolean;
  isBootstrapping: boolean;
  login: (input: LoginInput) => Promise<void>;
  logout: () => Promise<void>;
  updateApiBaseUrl: (baseUrl: string) => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

const tokenKey = "deyeSolar.mobile.token";
const usernameKey = "deyeSolar.mobile.username";
const apiBaseUrlKey = "deyeSolar.mobile.apiBaseUrl";

export function AuthProvider({ children }: { children: ReactNode }) {
  const [apiBaseUrl, setApiBaseUrl] = useState(defaultApiBaseUrl);
  const [token, setToken] = useState<string | null>(null);
  const [username, setUsername] = useState<string | null>(null);
  const [isBootstrapping, setIsBootstrapping] = useState(true);

  const clearSession = useCallback(async () => {
    setToken(null);
    setUsername(null);
    clientRef.current.setToken(null);
    await Promise.all([
      AsyncStorage.removeItem(tokenKey),
      AsyncStorage.removeItem(usernameKey)
    ]);
  }, []);

  const clientRef = useRef(
    new ApiClient({
      baseUrl: apiBaseUrl,
      onUnauthorized: () => {
        void clearSession();
      }
    })
  );

  const api = useMemo(() => new DeyeSolarApi(clientRef.current), []);

  useEffect(() => {
    let mounted = true;

    async function loadSession() {
      const [storedBaseUrl, storedToken, storedUsername] = await Promise.all([
        AsyncStorage.getItem(apiBaseUrlKey),
        AsyncStorage.getItem(tokenKey),
        AsyncStorage.getItem(usernameKey)
      ]);

      if (!mounted) {
        return;
      }

      const nextBaseUrl = normalizeBaseUrl(storedBaseUrl || defaultApiBaseUrl);
      setApiBaseUrl(nextBaseUrl);
      setToken(storedToken ?? null);
      setUsername(storedUsername ?? null);
      clientRef.current.setBaseUrl(nextBaseUrl);
      clientRef.current.setToken(storedToken);
      setIsBootstrapping(false);
    }

    void loadSession();

    return () => {
      mounted = false;
    };
  }, []);

  const login = useCallback(async (input: LoginInput) => {
    const nextBaseUrl = normalizeBaseUrl(input.baseUrl);
    clientRef.current.setBaseUrl(nextBaseUrl);
    clientRef.current.setToken(null);

    const session = await api.login(input.username.trim(), input.password);

    setApiBaseUrl(nextBaseUrl);
    setToken(session.token);
    setUsername(session.username);
    clientRef.current.setToken(session.token);
    await Promise.all([
      AsyncStorage.setItem(apiBaseUrlKey, nextBaseUrl),
      AsyncStorage.setItem(tokenKey, session.token),
      AsyncStorage.setItem(usernameKey, session.username)
    ]);
  }, [api]);

  const logout = useCallback(async () => {
    try {
      if (token) {
        await api.logout();
      }
    } finally {
      await clearSession();
    }
  }, [api, clearSession, token]);

  const updateApiBaseUrl = useCallback(async (baseUrl: string) => {
    const nextBaseUrl = normalizeBaseUrl(baseUrl);
    setApiBaseUrl(nextBaseUrl);
    clientRef.current.setBaseUrl(nextBaseUrl);
    await AsyncStorage.setItem(apiBaseUrlKey, nextBaseUrl);
  }, []);

  const value: AuthContextValue = {
    api,
    apiBaseUrl,
    username,
    isAuthenticated: Boolean(token),
    isBootstrapping,
    login,
    logout,
    updateApiBaseUrl
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside AuthProvider.");
  }

  return context;
}

const defaultApiBaseUrl = normalizeBaseUrl(
  Platform.OS === "android" ? "http://10.0.2.2:5000" : "http://localhost:5000"
);
