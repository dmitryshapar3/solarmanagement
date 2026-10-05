import { translate as t } from "../../core/i18n";
import { useCallback, useEffect, useRef, useState } from "react";
import type { ActionSession } from "../../application/ScopedActionScope";
import { AppState } from "react-native";
import { useFocusEffect } from "@react-navigation/native";

const refreshInterval = 5 * 60 * 1000;

// Use a memoized fetch callback: its identity owns results alongside the selected window/session.
// Keep the last result only for that owner, and fence responses after blur or replacement.
export function useFocusedResource<T>(key: string, fetch: (signal: AbortSignal, force: boolean) => Promise<T>, session?: ActionSession) {
  const [stored, setStored] = useState<{ key: string; fetch: typeof fetch; session?: ActionSession; sessionEpoch?: number; value: T } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const owner = useRef({ key, fetch, session }); owner.current = { key, fetch, session };
  const epoch = useRef(0);
  const active = useRef(false);
  const pending = useRef<AbortController | null>(null);
  const invalidate = useCallback(() => {
    ++epoch.current;
    pending.current?.abort();
    pending.current = null;
    setLoading(false);
  }, []);
  const run = useCallback(async (force = false) => {
    if (!active.current || AppState.currentState !== "active" || pending.current) return;
    const controller = new AbortController();
    pending.current = controller;
    const generation = ++epoch.current;
    const sessionEpoch = session?.sessionEpoch;
    const isCurrent = () => generation === epoch.current && active.current && !controller.signal.aborted
      && owner.current.key === key && owner.current.fetch === fetch && owner.current.session === session
      && sessionEpoch === session?.sessionEpoch;
    setLoading(true);
    try {
      const value = await fetch(controller.signal, force);
      if (isCurrent()) {
        setStored({ key, fetch, session, sessionEpoch, value });
        setError(null);
      }
    } catch (exception) {
      if (isCurrent())
        setError(exception instanceof Error ? exception.message : "The latest data could not be loaded.");
    } finally {
      if (generation === epoch.current && owner.current.key === key && owner.current.fetch === fetch && owner.current.session === session) {
        pending.current = null;
        setLoading(false);
      }
    }
  }, [key, fetch, session]);

  useEffect(() => session?.onSessionChange(() => {
    invalidate(); setStored(null); setError(null);
  }), [session, invalidate]);

  useFocusEffect(useCallback(() => {
    active.current = true;
    setError(null);
    void run();
    const timer = setInterval(() => void run(), refreshInterval);
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "active") void run();
      else invalidate();
    });
    return () => {
      active.current = false;
      clearInterval(timer);
      listener.remove();
      invalidate();
    };
  }, [run, invalidate]));

  const data = stored?.key === key && stored.fetch === fetch && stored.session === session && stored.sessionEpoch === session?.sessionEpoch ? stored.value : null;
  return { data, loading, error: error ? `${data ? t("Refresh failed. Previous data is retained. ") : ""}${t(error)}` : null, refresh: run, invalidate };
}
