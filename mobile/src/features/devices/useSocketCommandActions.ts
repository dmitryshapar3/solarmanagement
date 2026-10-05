import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useFocusEffect } from "@react-navigation/native";
import type { SocketCommandCoordinator } from "../../core/api/SocketCommandCoordinator";
import { SocketCommandActions } from "./SocketCommandActions";

export function useSocketCommandActions(commands: SocketCommandCoordinator, acknowledged: () => Promise<void>, error: (message: string | null) => void) {
  const [, setRevision] = useState(0);
  const callbacks = useRef({ acknowledged, error }); callbacks.current = { acknowledged, error };
  const epoch = commands.sessionEpoch;
  const current = useRef({ commands, epoch }); current.current = { commands, epoch };
  const actions = useMemo(() => new SocketCommandActions(commands, {
    changed: () => { if (current.current.commands === commands && current.current.epoch === epoch) setRevision(value => value + 1); },
    started: () => { if (current.current.commands === commands && current.current.epoch === epoch) callbacks.current.error(null); },
    acknowledged: async () => { if (current.current.commands === commands && current.current.epoch === epoch) await callbacks.current.acknowledged(); },
    failed: (exception, fallback) => { if (current.current.commands === commands && current.current.epoch === epoch) callbacks.current.error(exception instanceof Error ? exception.message : fallback); }
  }), [commands, epoch]);
  useEffect(() => commands.subscribe(() => setRevision(value => value + 1)), [commands]);
  useFocusEffect(useCallback(() => {
    actions.activate();
    setRevision(value => value + 1);
    return () => actions.deactivate();
  }, [actions]));
  return actions;
}
