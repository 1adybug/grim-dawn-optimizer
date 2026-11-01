// Browser-only fixture: all updater IPC is mocked. Never downloads or installs.
import React, { useState } from "react";
import { createRoot } from "react-dom/client";
import { mockIPC } from "@tauri-apps/api/mocks";
import { AppUpdates } from "../src/components/app-updates";
import { useAppUpdater } from "../src/lib/use-app-updater";
import "../src/styles.css";

const scenario =
  new URLSearchParams(location.search).get("scenario") || "manual";
let preferences = { autoCheck: scenario !== "manual", autoInstall: true };
const calls: string[] = [];
const info = () => ({
  version: "1.0.23",
  supported: scenario !== "portable",
  preferences,
});
Object.assign(window, { isTauri: true });
mockIPC(
  async (command, payload) => {
    calls.push(command);
    document.getElementById("calls")!.textContent = calls.join(" · ");
    if (command === "update_state") return info();
    if (command === "update_preferences") {
      const next = payload as typeof preferences;
      preferences = {
        autoCheck: next.autoCheck,
        autoInstall: next.autoInstall,
      };
      return info();
    }
    if (command === "check_update") {
      await new Promise((resolve) => setTimeout(resolve, 1000));
      if (scenario === "network")
        throw new Error("无法检查 GitHub 更新，请检查网络后重试。");
      return scenario === "current"
        ? null
        : { version: "1.0.24", notes: "交互测试" };
    }
    if (command === "install_update")
      throw new Error("更新下载或签名校验失败，请重试。");
    throw new Error(`Unexpected mock IPC: ${command}`);
  },
  { shouldMockEvents: true },
);

function Harness() {
  const [blocked, setBlocked] = useState(true);
  const updater = useAppUpdater(blocked);
  return (
    <div style={{ maxWidth: 460, padding: 24, margin: "auto" }}>
      <h1>安装版更新交互测试</h1>
      <p>仅模拟 IPC，不执行更新。</p>
      <button onClick={() => setBlocked(!blocked)}>
        {blocked ? "模拟断开实战工具" : "模拟连接实战工具"}
      </button>
      <AppUpdates updater={updater} />
      <output id="calls" aria-label="命令记录" />
    </div>
  );
}
const root = createRoot(document.getElementById("root")!);
root.render(<Harness />);
import.meta.hot?.dispose(() => root.unmount());
