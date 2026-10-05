import { createContext, useCallback, useContext, useEffect, useRef } from "react";

export type ScreenRefreshOperation = () => Promise<unknown> | void;
export type ScreenRefreshEntry = { refresh: ScreenRefreshOperation; loading: boolean };
export type ScreenRefreshRegistry = {
  register: (id: symbol, entry: ScreenRefreshEntry) => () => void;
};

export const ScreenRefreshContext = createContext<ScreenRefreshRegistry | null>(null);

/** Include a child panel in its screen's single pull-to-refresh gesture. */
export function useScreenRefresh(refresh: ScreenRefreshOperation, loading = false) {
  const registry = useContext(ScreenRefreshContext);
  const id = useRef(Symbol("screen-resource")).current;
  const latest = useRef(refresh);
  latest.current = refresh;
  const invoke = useCallback(() => latest.current(), []);
  useEffect(() => registry?.register(id, { refresh: invoke, loading }), [registry, id, invoke, loading]);
}
