import { useCallback, useRef, useState } from "react";
import { AppState } from "react-native";
import { useFocusEffect } from "@react-navigation/native";

const refreshInterval = 5 * 60 * 1000;

// Keep the last result only for the same selected window, and fence responses after blur or replacement.
export function useFocusedResource<T>(key: string, fetch: (signal: AbortSignal, force: boolean) => Promise<T>) {
  const [stored, setStored] = useState<{ key: string; value: T } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const fetchRef = useRef(fetch);
  fetchRef.current = fetch;
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
    setLoading(true);
    try {
      const value = await fetchRef.current(controller.signal, force);
      if (generation === epoch.current && active.current && !controller.signal.aborted) {
        setStored({ key, value });
        setError(null);
      }
    } catch (exception) {
      if (generation === epoch.current && active.current && !controller.signal.aborted)
        setError(exception instanceof Error ? exception.message : "The latest data could not be loaded.");
    } finally {
      if (generation === epoch.current) {
        pending.current = null;
        setLoading(false);
      }
    }
  }, [key]);

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

  const data = stored?.key === key ? stored.value : null;
  return { data, loading, error: error ? `${data ? "Refresh failed. Previous data is retained. " : ""}${error}` : null, refresh: run, invalidate };
}
