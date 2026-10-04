import AsyncStorage from "@react-native-async-storage/async-storage";
import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "./AuthContext";
import { currentLocale, languages, Language, normalizeLanguage, setLocale, translate } from "../core/i18n";
import { ApiError } from "../core/api/ApiClient";

const storageKey = "solar.language.v1";
type LanguageValue = {
  language: Language;
  languages: typeof languages;
  t: typeof translate;
  setLanguage: (language: string) => Promise<void>;
};
const LanguageContext = createContext<LanguageValue | null>(null);

export function LanguageProvider({ children }: { children: ReactNode }) {
  const { api, isAuthenticated, isDemo, username, apiBaseUrl, isBootstrapping } = useAuth();
  const [language, updateLanguage] = useState<Language>(currentLocale);
  const [ready, setReady] = useState(false);
  const revision = useRef(0);
  const pendingWrites = useRef<Promise<void>>(Promise.resolve());
  const accountSessionEpoch = api.integrations.sessionEpoch;
  const accountIdentity = `${isAuthenticated}:${isDemo}:${apiBaseUrl}:${username ?? ""}`;
  const currentAccount = useRef(accountIdentity);
  currentAccount.current = accountIdentity;
  const apply = useCallback((next: Language) => { setLocale(next); updateLanguage(next); }, []);
  const syncPreference = useCallback(async (next: Language, pendingKey: string, current: () => boolean) => {
    if (!current()) return;
    try {
      await api.setLanguage(next);
    } catch (error) {
      if (!current()) return;
      // Network outages and temporary service failures retain the pending local choice.
      if (!(error instanceof ApiError) || error.status >= 500 || error.status === 408 || error.status === 429) return;
      throw error;
    }
    if (current()) await AsyncStorage.multiSet([[pendingKey, ""]]);
  }, [api]);
  useEffect(() => {
    let active = true;
    const observed = revision.current;
    void AsyncStorage.getItem(storageKey).then(saved => {
      const next = normalizeLanguage(saved);
      if (active && next && observed === revision.current) apply(next);
    }).catch(() => {}).finally(() => { if (active) setReady(true); });
    return () => { active = false; };
  }, [apply]);
  useEffect(() => {
    if (!ready || isBootstrapping || !isAuthenticated || isDemo) return;
    const controller = new AbortController();
    const observed = revision.current;
    const account = currentAccount.current;
    const sessionEpoch = api.integrations.sessionEpoch;
    const current = () => !controller.signal.aborted && observed === revision.current
      && account === currentAccount.current && sessionEpoch === api.integrations.sessionEpoch;
    // Cache separately per server/account so preferences cannot leak between users on a shared device.
    const accountKey = `${storageKey}:${apiBaseUrl}:${username ?? ""}`;
    const pendingKey = `${accountKey}:pending`;
    void (async () => {
      await pendingWrites.current.catch(() => {});
      if (!current()) return;
      const [stored, unsynced] = await Promise.all([AsyncStorage.getItem(accountKey), AsyncStorage.getItem(pendingKey)]);
      if (!current()) return;
      const pending = normalizeLanguage(unsynced);
      const saved = normalizeLanguage(stored);
      if (pending) {
        apply(pending);
        const write = pendingWrites.current.catch(() => {}).then(() => syncPreference(pending, pendingKey, current));
        pendingWrites.current = write;
        await write;
        return;
      }
      if (saved) apply(saved);
      try {
        const remote = await api.getLanguage(controller.signal);
        if (!current()) return;
        const next = normalizeLanguage(remote.language);
        if (next) {
          apply(next);
          // Share the persistence queue with user selections so a slower hydration write cannot overwrite them.
          const write = pendingWrites.current.catch(() => {}).then(async () => {
            if (current()) await AsyncStorage.multiSet([[accountKey, next], [storageKey, next]]);
          });
          pendingWrites.current = write;
          await write;
        }
      } catch {
        // Startup keeps the cached preference when its server cannot be read.
      }
    })().catch(() => {});
    return () => controller.abort();
  }, [api, apiBaseUrl, username, ready, isBootstrapping, isAuthenticated, isDemo, accountSessionEpoch, apply, syncPreference]);
  const setLanguage = useCallback(async (value: string) => {
    const next = normalizeLanguage(value);
    if (!next) throw new Error(translate("Choose a supported language."));
    const account = accountIdentity;
    const sessionEpoch = accountSessionEpoch;
    if (account !== currentAccount.current || sessionEpoch !== api.integrations.sessionEpoch) return;
    const observed = ++revision.current;
    apply(next);
    const entries: [string, string][] = [[storageKey, next]];
    const pendingKey = `${storageKey}:${apiBaseUrl}:${username ?? ""}:pending`;
    if (isAuthenticated && !isDemo) entries.push([`${storageKey}:${apiBaseUrl}:${username ?? ""}`, next], [pendingKey, next]);
    const write = pendingWrites.current.catch(() => {}).then(async () => {
      if (observed !== revision.current || account !== currentAccount.current || sessionEpoch !== api.integrations.sessionEpoch) return;
      await AsyncStorage.multiSet(entries);
      if (observed !== revision.current || account !== currentAccount.current || sessionEpoch !== api.integrations.sessionEpoch) return;
      if (isAuthenticated && !isDemo) {
        await syncPreference(next, pendingKey, () => observed === revision.current
          && account === currentAccount.current && sessionEpoch === api.integrations.sessionEpoch);
      }
    });
    pendingWrites.current = write;
    await write;
  }, [api, apiBaseUrl, username, isAuthenticated, isDemo, accountIdentity, accountSessionEpoch, apply, syncPreference]);
  // A new callback identity makes memoized resources and navigation labels react to a language change.
  const t = useCallback<typeof translate>((phrase, ...args) => translate(phrase, ...args), [language]);
  const value = useMemo(() => ({ language, languages, t, setLanguage }), [language, t, setLanguage]);
  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}
export function useLanguage(): LanguageValue {
  const value = useContext(LanguageContext);
  if (!value) throw new Error("useLanguage must be used inside LanguageProvider.");
  return value;
}
