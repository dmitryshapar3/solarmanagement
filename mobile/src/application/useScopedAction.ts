import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ScopedActionScope, type ActionSession, type ScopedActionContext } from "./ScopedActionScope";
type Callbacks = { started?: () => void; failed?: (error: unknown) => void; finished?: () => void };

export function useScopedAction(session: ActionSession, identity: string = "", reset?: () => void) {
  const owner = useRef({ session, identity }); owner.current = { session, identity };
  const scope = useMemo(() => new ScopedActionScope(() => session.sessionEpoch,
    () => owner.current.session === session && owner.current.identity === identity), [session, identity]);
  const [pending, setPending] = useState<{ scope: ScopedActionScope; label: string } | null>(null);
  const [revision, setRevision] = useState(0);
  const resetRef = useRef(reset); resetRef.current = reset;
  useEffect(() => {
    scope.activate();
    resetRef.current?.();
    const unsubscribe = session.onSessionChange(() => {
      scope.invalidate(); setPending(null); setRevision(value => value + 1); resetRef.current?.();
    });
    return () => { unsubscribe(); scope.dispose(); };
  }, [session, scope]);
  const run = useCallback((label: string, action: (context: ScopedActionContext) => Promise<void>, callbacks: Callbacks = {}) =>
    scope.run(action, {
      started: () => { setPending({ scope, label }); callbacks.started?.(); },
      failed: callbacks.failed,
      finished: () => { setPending(null); callbacks.finished?.(); }
    }), [scope]);
  const capture = useCallback(() => scope.capture(), [scope, revision]);
  const cancel = useCallback(() => scope.cancel(), [scope]);
  return { busy: pending?.scope === scope ? pending.label : null, run, capture, cancel };
}
