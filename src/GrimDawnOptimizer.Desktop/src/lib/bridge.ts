import { invoke, isTauri } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { State } from "./types";
import type { TrainerState } from "./trainer-types";

export async function request<T = State>(
  command: string,
  payload: Record<string, unknown> = {},
): Promise<T> {
  if (isTauri()) return invoke<T>("bridge_request", { command, payload });
  if (!import.meta.env.DEV) throw new Error("请通过桌面程序打开配装工坊。");
  const response = await fetch("/__engine/request", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ command, payload }),
  });
  const data = await response.json();
  if (!response.ok || data.ok === false)
    throw new Error(data.error || "计算服务暂时不可用，请重试。");
  return data.state ?? data;
}

export async function subscribe(
  onState: (state: State) => void,
  onError: (error: string) => void,
  onTrainer?: (state: TrainerState) => void,
  onDisconnected?: (error: string) => void,
) {
  if (isTauri()) {
    const removers: (() => void)[] = [];
    try {
      removers.push(
        await listen<State>("bridge-state", (event) => onState(event.payload)),
      );
      removers.push(
        await listen<string>("bridge-error", (event) => onError(event.payload)),
      );
      if (onTrainer)
        removers.push(
          await listen<TrainerState>("trainer-state", (event) =>
            onTrainer(event.payload),
          ),
        );
      if (onDisconnected)
        removers.push(
          await listen<string>("bridge-disconnected", (event) =>
            onDisconnected(event.payload),
          ),
        );
      return () => removers.forEach((remove) => remove());
    } catch (error) {
      removers.forEach((remove) => remove());
      throw error;
    }
  }
  if (!import.meta.env.DEV) return () => {};
  const stream = new EventSource("/__engine/events");
  stream.addEventListener("state", (event) => {
    try {
      onState(JSON.parse(event.data));
    } catch {
      onError("读取计算状态失败，请重新连接。");
    }
  });
  stream.addEventListener("trainer", (event) => {
    try {
      onTrainer?.(JSON.parse(event.data));
    } catch {
      onError("读取实战工具状态失败，请重新连接。");
    }
  });
  stream.addEventListener("engine-error", (event) => {
    try {
      const failure = JSON.parse(event.data);
      onError(failure.error);
      if (failure.transport) onDisconnected?.(failure.error);
    } catch {
      onError("计算服务异常，请重新连接。");
    }
  });
  stream.onerror = () => {
    const message = "计算服务连接中断，请重新连接。";
    onError(message);
    onDisconnected?.(message);
  };
  return () => stream.close();
}
