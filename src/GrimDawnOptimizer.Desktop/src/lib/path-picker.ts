import { isTauri } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { request } from "./bridge";

export type PathKind = "gameDirectory" | "saveDirectory" | "gameExecutable";

/** Windows "Copy as path" quotes the whole path; this is not shell parsing. */
export function normalizePath(value: string): string {
  const trimmed = value.trim();
  return trimmed.length >= 2 && trimmed.startsWith('"') && trimmed.endsWith('"')
    ? trimmed.slice(1, -1)
    : trimmed;
}

const titles: Record<PathKind, string> = {
  gameDirectory: "选择游戏安装目录",
  saveDirectory: "选择角色存档目录",
  gameExecutable: "选择 Grim Dawn.exe",
};

/** Both desktop and local development open the system picker; neither uploads a file. */
export async function pickPath(
  kind: PathKind,
  defaultPath?: string,
): Promise<string | null> {
  const initialPath = defaultPath ? normalizePath(defaultPath) : undefined;
  if (isTauri()) {
    const selected = await open({
      title: titles[kind],
      directory: kind !== "gameExecutable",
      multiple: false,
      defaultPath: initialPath || undefined,
      ...(kind === "gameExecutable"
        ? { filters: [{ name: "Grim Dawn", extensions: ["exe"] }] }
        : {}),
    });
    return typeof selected === "string" ? selected : null;
  }
  if (!import.meta.env.DEV) throw new Error("请通过桌面程序选择本机路径。");
  const result = await request<{ path: string | null }>("pickPath", {
    kind,
    defaultPath: initialPath || null,
  });
  if (!result || (result.path !== null && typeof result.path !== "string"))
    throw new Error("系统选择器未返回有效路径，请重试。");
  return result.path;
}
