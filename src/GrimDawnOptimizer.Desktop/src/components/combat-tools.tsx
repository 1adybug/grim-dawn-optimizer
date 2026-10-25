import { useRef, useState } from "react";
import {
  CircleHelp,
  LoaderCircle,
  Play,
  Plug,
  Power,
  Unplug,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { PathInput } from "@/components/path-input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { ActionButton, Notice } from "@/components/shared";
import type { TrainerFeature, TrainerFeatureId } from "@/lib/trainer-types";
import type { TrainerController } from "@/lib/use-trainer";
import "./combat-tools.css";

const featureOrder: TrainerFeatureId[] = [
  "life",
  "mana",
  "kill",
  "moveSpeed",
  "attackSpeed",
  "castSpeed",
  "cooldown",
  "autoHealth",
  "autoMana",
];

export function CombatTools({ controller }: { controller: TrainerController }) {
  const { state, error, transportError, pending, run } = controller;
  const [help, setHelp] = useState<TrainerFeature | null>(null);
  const [pathBusy, setPathBusy] = useState(false);
  const helpTrigger = useRef<HTMLButtonElement | null>(null);
  const busy = pending.length > 0;
  const available = !!state?.available && !transportError;
  const connecting = !!state?.connecting || pending.includes("trainerConnect");
  const pathLocked = busy || !!state?.connected || connecting || !state;
  const status = transportError
    ? "连接中断"
    : state?.statusText || "正在读取实战工具";
  const failure = error || transportError;
  const resourceSummary =
    available && state
      ? `生命 ${formatResource(state.health)} / ${formatResource(state.maxHealth)} · 蓝量 ${formatResource(state.mana)} / ${formatResource(state.maxMana)}`
      : null;

  return (
    <div className="page-enter combat-page">
      <section className="combat-connection" aria-label="游戏连接">
        <div className="combat-toolbar">
          <div
            className="combat-connection-status"
            role="status"
            aria-live="polite"
          >
            {connecting || !state ? (
              <LoaderCircle size={14} className="animate-spin" />
            ) : (
              <span className="combat-status-dot" data-ready={available} />
            )}
            {resourceSummary ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span
                    tabIndex={0}
                    aria-label={`${status}；${resourceSummary}`}
                  >
                    <strong>{status}</strong>
                  </span>
                </TooltipTrigger>
                <TooltipContent>{resourceSummary}</TooltipContent>
              </Tooltip>
            ) : (
              <strong>{status}</strong>
            )}
            {state?.connected && state.pid && (
              <span className="combat-process">PID {state.pid}</span>
            )}
          </div>
          <div className="combat-actions">
            <ActionButton
              size="sm"
              variant={state?.connected || connecting ? "outline" : "default"}
              busy={connecting}
              disabled={
                !state ||
                pathBusy ||
                pending.includes("trainerDisconnect") ||
                (busy && !connecting && !state.connected)
              }
              onClick={() =>
                void run(
                  state?.connected || connecting
                    ? "trainerDisconnect"
                    : "trainerConnect",
                )
              }
            >
              {!connecting && (state?.connected ? <Unplug /> : <Plug />)}
              {connecting ? "取消连接" : state?.connected ? "断开" : "连接"}
            </ActionButton>
            <Button
              variant="outline"
              size="sm"
              disabled={pathLocked || pathBusy || !state?.configuredExecutable}
              onClick={() => void run("trainerLaunch")}
            >
              <Play />
              启动游戏
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={!state || pending.includes("trainerDisableAll")}
              onClick={() => void run("trainerDisableAll")}
            >
              <Power />
              全部关闭<span className="combat-off-shortcut">Ctrl+Alt+F12</span>
            </Button>
          </div>
        </div>
        <PathInput
          className="combat-path-row"
          label="游戏程序"
          kind="gameExecutable"
          value={state?.configuredExecutable || ""}
          placeholder="输入或选择 Grim Dawn.exe 的完整路径"
          disabled={pathLocked}
          onCommit={(path) => run("trainerSelectExecutable", { path })}
          onBusyChange={setPathBusy}
          commitError={
            error || "无法使用此程序，请选择完整游戏目录中的 Grim Dawn.exe。"
          }
        />
        {(failure || state?.hotkeyWarning) && (
          <div className="combat-notices">
            {failure && <Notice tone="error">{failure}</Notice>}
            {transportError && (
              <Button
                variant="outline"
                size="sm"
                onClick={() => void controller.refresh()}
              >
                重试
              </Button>
            )}
            {state?.hotkeyWarning && <Notice>{state.hotkeyWarning}</Notice>}
          </div>
        )}
      </section>
      {!state ? (
        <div className="combat-loading" role="status">
          正在准备功能开关…
        </div>
      ) : (
        <section className="combat-feature-grid" aria-label="实战选项">
          {featureOrder.map((id) => {
            const feature = state.features.find((item) => item.id === id);
            if (!feature) return null;
            return (
              <div
                key={id}
                className="combat-feature"
                data-requested={feature.requested}
              >
                <div className="combat-feature-label">
                  <Label htmlFor={`trainer-${id}`}>{feature.title}</Label>
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <Button
                        variant="ghost"
                        size="icon"
                        className="combat-help"
                        aria-label={`${feature.title}说明`}
                        onClick={(event) => {
                          helpTrigger.current = event.currentTarget;
                          setHelp(feature);
                        }}
                      >
                        <CircleHelp />
                      </Button>
                    </TooltipTrigger>
                    <TooltipContent className="combat-tooltip">
                      {feature.description}
                    </TooltipContent>
                  </Tooltip>
                </div>
                <Switch
                  id={`trainer-${id}`}
                  checked={feature.requested}
                  disabled={!available || busy}
                  aria-describedby={`trainer-${id}-description`}
                  onCheckedChange={(enabled) =>
                    void run("trainerSetFeature", { id, enabled })
                  }
                />
                <span className="sr-only" id={`trainer-${id}-description`}>
                  {feature.description}
                </span>
                <div className="combat-feature-meta">
                  <kbd>{feature.shortcut}</kbd>
                  {feature.pending && (
                    <span className="combat-pending" role="status">
                      <LoaderCircle size={12} className="animate-spin" />
                      {feature.requested ? "待确认" : "关闭中"}
                    </span>
                  )}
                  {feature.applied && !feature.pending && (
                    <span className="combat-applied">已生效</span>
                  )}
                </div>
                {id === "moveSpeed" && (
                  <RadioGroup
                    className="combat-speed-options"
                    aria-label="移动速度倍率"
                    value={String(state.movementMultiplier)}
                    disabled={busy || connecting || !!transportError}
                    onValueChange={(value) =>
                      void run("trainerSetMovementMultiplier", {
                        value: Number(value),
                      })
                    }
                  >
                    {[2, 4, 8, 16].map((value) => (
                      <Label
                        key={value}
                        className="combat-speed-option"
                        htmlFor={`trainer-speed-${value}`}
                      >
                        <RadioGroupItem
                          id={`trainer-speed-${value}`}
                          value={String(value)}
                        />
                        {value}×
                      </Label>
                    ))}
                  </RadioGroup>
                )}
              </div>
            );
          })}
        </section>
      )}
      <div className="combat-footnote">
        <span>仅单人模式 · 适配 1.3.1.1</span>
        <span>连接后使用全局快捷键，效果仅作用于当前会话。</span>
      </div>
      <Dialog
        open={!!help}
        onOpenChange={(visible) => {
          if (!visible) setHelp(null);
        }}
      >
        <DialogContent
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            helpTrigger.current?.focus();
          }}
        >
          <DialogHeader>
            <DialogTitle>{help?.title}</DialogTitle>
            <DialogDescription>{help?.description}</DialogDescription>
          </DialogHeader>
          {help && (
            <p className="combat-help-shortcut">
              快捷键 <kbd>{help.shortcut}</kbd>
            </p>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}

function formatResource(value: number | null) {
  return value === null ? "—" : Math.round(value).toLocaleString("zh-CN");
}
