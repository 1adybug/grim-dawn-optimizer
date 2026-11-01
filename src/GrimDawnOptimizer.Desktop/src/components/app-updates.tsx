import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Progress } from "@/components/ui/progress";
import { ActionButton, Notice } from "@/components/shared";
import type { AppUpdater } from "@/lib/use-app-updater";

export function AppUpdates({ updater }: { updater: AppUpdater }) {
  const { info, release, phase, saving, installing, blocked } = updater;
  const busy = phase === "checking" || installing;
  const status =
    phase === "checking"
      ? "正在检查 GitHub 更新…"
      : installing
        ? `正在下载并安装更新${updater.progress === null ? "…" : ` ${updater.progress}%`}`
        : release
          ? `发现新版本 ${release.version}${blocked ? "，完成分析并断开实战工具后可更新。" : ""}`
          : phase === "current"
            ? "已是最新版本。"
            : "从 GitHub Releases 获取更新。";
  return (
    <section className="app-updates" aria-label="应用更新">
      <strong>应用更新{info && ` · ${info.version}`}</strong>
      {info?.supported ? (
        <>
          <div className="update-option">
            <Label htmlFor="update-auto-check">自动检查更新</Label>
            <Switch
              id="update-auto-check"
              checked={info.preferences.autoCheck}
              disabled={saving || installing}
              onCheckedChange={(enabled) =>
                void updater.configure(enabled, info.preferences.autoInstall)
              }
            />
          </div>
          <div className="update-option">
            <Label htmlFor="update-auto-install">自动下载并安装</Label>
            <Switch
              id="update-auto-install"
              checked={info.preferences.autoInstall}
              disabled={saving || installing || !info.preferences.autoCheck}
              onCheckedChange={(enabled) =>
                void updater.configure(info.preferences.autoCheck, enabled)
              }
            />
          </div>
          <p className="update-hint">
            自动更新会在分析结束且实战工具断开后安装并重启。关闭自动检查后，仍可手动检查。
          </p>
          <p role="status" aria-live="polite">
            {status}
          </p>
          {installing && updater.progress !== null && (
            <Progress value={updater.progress} aria-label="更新下载进度" />
          )}
          <div className="update-actions">
            <ActionButton
              variant="outline"
              size="sm"
              busy={phase === "checking"}
              disabled={busy || saving}
              onClick={() => void updater.check()}
            >
              检查更新
            </ActionButton>
            {release && (
              <ActionButton
                size="sm"
                busy={installing}
                disabled={busy || blocked || saving}
                onClick={() => void updater.install()}
              >
                安装并重启
              </ActionButton>
            )}
          </div>
        </>
      ) : (
        <p className="update-hint">
          {info
            ? "便携版与开发预览通过 GitHub 下载新版本；安装版支持自动更新。"
            : "正在读取更新设置…"}
        </p>
      )}
      {(updater.error || info?.warning) && (
        <Notice tone="error">{updater.error || info?.warning}</Notice>
      )}
      {!info && updater.error && (
        <Button
          size="sm"
          variant="outline"
          onClick={() => void updater.refresh()}
        >
          重试
        </Button>
      )}
    </section>
  );
}
