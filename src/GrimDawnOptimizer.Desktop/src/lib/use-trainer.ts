import { useCallback, useRef, useState } from "react";
import { request } from "./bridge";
import { SnapshotOrder } from "./state-order";
import type { TrainerCommand, TrainerState } from "./trainer-types";

/** Combat commands have their own busy/error state and never invalidate a build. */
export function useTrainer() {
  const [state, setState] = useState<TrainerState | null>(null);
  const [error, setError] = useState("");
  const [transportError, setTransportError] = useState("");
  const [pending, setPending] = useState<TrainerCommand[]>([]);
  const inFlight = useRef(new Set<TrainerCommand>());
  const order = useRef(new SnapshotOrder());
  const receive = useCallback((next: TrainerState) => {
    if (!order.current.accept(next)) return;
    setState(next);
    setTransportError("");
    setError(next.error ?? "");
  }, []);
  const reportTransportError = useCallback((message: string) => {
    setTransportError(message);
  }, []);
  const run = useCallback(
    async (command: TrainerCommand, payload: Record<string, unknown> = {}) => {
      const emergency =
        command === "trainerDisableAll" || command === "trainerDisconnect";
      if (
        inFlight.current.has(command) ||
        (inFlight.current.size > 0 && !emergency)
      )
        return false;
      inFlight.current.add(command);
      setPending([...inFlight.current]);
      setError("");
      try {
        receive(await request<TrainerState>(command, payload));
        return true;
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : String(cause));
        return false;
      } finally {
        inFlight.current.delete(command);
        setPending([...inFlight.current]);
      }
    },
    [receive],
  );
  const refresh = useCallback(async () => {
    try {
      receive(await request<TrainerState>("trainerState"));
    } catch (cause) {
      reportTransportError(
        cause instanceof Error ? cause.message : String(cause),
      );
    }
  }, [receive, reportTransportError]);
  return {
    state,
    error,
    setError,
    transportError,
    pending,
    receive,
    reportTransportError,
    refresh,
    run,
  };
}

export type TrainerController = ReturnType<typeof useTrainer>;
