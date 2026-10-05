import { useRef } from "react";
import { RefreshGroup } from "./RefreshGroup";

export function useGroupedRefresh(loading: boolean, operations: ReadonlyArray<() => Promise<unknown> | void>) {
  const group = useRef(new RefreshGroup()).current;
  return async () => { await group.run(loading, operations); };
}
