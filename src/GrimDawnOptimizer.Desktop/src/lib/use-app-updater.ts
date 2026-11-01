import { useCallback, useEffect, useRef, useState } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

interface UpdateInfo {
  version: string;
  supported: boolean;
  warning?: string | null;
  preferences: { autoCheck: boolean; autoInstall: boolean };
}
interface Release {
  version: string;
  notes: string | null;
}
type Phase =
  "idle" | "checking" | "current" | "available" | "installing" | "failed";
const interval = 6 * 60 * 60 * 1000;
const message = (cause: unknown) =>
  cause instanceof Error ? cause.message : String(cause);

export function useAppUpdater(blocked: boolean) {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [release, setRelease] = useState<Release | null>(null);
  const [phase, setPhase] = useState<Phase>("idle");
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);
  const [progress, setProgress] = useState<number | null>(null);
  const current = useRef(info);
  const active = useRef(false);
  const savingRef = useRef(false);
  const blockedRef = useRef(blocked);
  current.current = info;
  blockedRef.current = blocked;

  const refresh = useCallback(async () => {
    try {
      setInfo(
        isTauri()
          ? await invoke<UpdateInfo>("update_state")
          : {
              version: "开发预览",
              supported: false,
              preferences: { autoCheck: true, autoInstall: true },
            },
      );
      setError("");
    } catch (cause) {
      setError(message(cause));
    }
  }, []);
  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    if (!isTauri()) return;
    let disposed = false;
    let remove: (() => void) | undefined;
    void listen<{ downloaded: number; total: number | null }>(
      "update-progress",
      ({ payload }) => {
        setProgress(
          payload.total
            ? Math.min(
                100,
                Math.round((payload.downloaded / payload.total) * 100),
              )
            : null,
        );
      },
    )
      .then((unlisten) => {
        if (disposed) unlisten();
        else remove = unlisten;
      })
      .catch(() => {});
    return () => {
      disposed = true;
      remove?.();
    };
  }, []);

  const configure = useCallback(
    async (autoCheck: boolean, autoInstall: boolean) => {
      if (savingRef.current || !current.current?.supported) return;
      savingRef.current = true;
      setSaving(true);
      const previous = current.current;
      // Turning either switch off fences an already-running check immediately.
      current.current = {
        ...previous,
        preferences: { autoCheck, autoInstall },
      };
      try {
        const next = await invoke<UpdateInfo>("update_preferences", {
          autoCheck,
          autoInstall,
        });
        current.current = next;
        setInfo(next);
        setError("");
      } catch (cause) {
        current.current = previous;
        setError(message(cause));
      } finally {
        savingRef.current = false;
        setSaving(false);
      }
    },
    [],
  );

  const check = useCallback(async () => {
    if (active.current || !current.current?.supported) return;
    active.current = true;
    setPhase("checking");
    setError("");
    setRelease(null);
    try {
      const next = await invoke<Release | null>("check_update");
      setRelease(next);
      setPhase(next ? "available" : "current");
    } catch (cause) {
      setError(message(cause));
      setPhase("failed");
    } finally {
      active.current = false;
    }
  }, []);

  const install = useCallback(async (automatic = false) => {
    const preferences = current.current?.preferences;
    if (
      active.current ||
      savingRef.current ||
      blockedRef.current ||
      !current.current?.supported ||
      (automatic && (!preferences?.autoCheck || !preferences.autoInstall))
    )
      return;
    active.current = true;
    setPhase("installing");
    setError("");
    setProgress(null);
    try {
      await invoke("install_update");
      // Windows normally exits here. A returned success must never trigger a
      // second automatic installation while the installer is taking over.
    } catch (cause) {
      setError(message(cause));
      setPhase("failed");
    } finally {
      active.current = false;
    }
  }, []);

  useEffect(() => {
    if (!info?.supported || !info.preferences.autoCheck) return;
    const initial = window.setTimeout(() => void check(), 4000);
    const periodic = window.setInterval(() => void check(), interval);
    return () => {
      clearTimeout(initial);
      clearInterval(periodic);
    };
  }, [info?.supported, info?.preferences.autoCheck, check]);

  useEffect(() => {
    if (
      phase !== "available" ||
      !release ||
      blocked ||
      saving ||
      !info?.preferences.autoCheck ||
      !info.preferences.autoInstall
    )
      return;
    const timer = window.setTimeout(() => void install(true), 3000);
    return () => clearTimeout(timer);
  }, [phase, release, blocked, saving, info, install]);

  return {
    info,
    release,
    phase,
    error,
    saving,
    progress,
    blocked,
    configure,
    check,
    install,
    refresh,
    installing: phase === "installing",
  };
}
export type AppUpdater = ReturnType<typeof useAppUpdater>;
