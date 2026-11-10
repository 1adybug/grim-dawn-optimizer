import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { isTauri } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  ArrowDownUp,
  ArrowRight,
  Check,
  ChevronRight,
  CircleHelp,
  Download,
  LoaderCircle,
  Monitor,
  Moon,
  RefreshCw,
  RotateCcw,
  Settings2,
  Shield,
  SlidersHorizontal,
  Sun,
  UserRound,
  X,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { EquipmentComparisonTable } from "@/components/equipment-comparison-table";
import { CombatTools } from "@/components/combat-tools";
import { AppUpdates } from "@/components/app-updates";
import { PathInput } from "@/components/path-input";
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import {
  Table,
  TableHeader,
  TableBody,
  TableRow,
  TableHead,
  TableCell,
  TableCaption,
} from "@/components/ui/table";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Progress } from "@/components/ui/progress";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  ActionButton,
  Disclosure,
  EmptyState,
  Notice,
  NumericField,
  SelectField,
} from "@/components/shared";
import { request, subscribe } from "@/lib/bridge";
import { SnapshotOrder } from "@/lib/state-order";
import { useTrainer } from "@/lib/use-trainer";
import { useAppUpdater, type AppUpdater } from "@/lib/use-app-updater";
import appIcon from "../src-tauri/icons/icon.png";
import type { Run, State, Theme, Workspace } from "@/lib/types";

const routes = [
  { id: "sources", label: "角色库", icon: UserRound },
  { id: "build", label: "配装工坊", icon: SlidersHorizontal },
  { id: "results", label: "方案对比", icon: ArrowDownUp },
  { id: "combat", label: "实战工具", icon: SlidersHorizontal },
] as const;

export default function App() {
  const trainer = useTrainer();
  const {
    receive: receiveTrainer,
    refresh: refreshTrainer,
    reportTransportError,
  } = trainer;
  const [state, setState] = useState<State | null>(null);
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const updater = useAppUpdater(
    !state ||
      state.isBusy ||
      pending ||
      !!trainer.transportError ||
      !!trainer.state?.connected ||
      !!trainer.state?.connecting ||
      trainer.pending.length > 0,
  );
  const pendingCount = useRef(0);
  const inFlight = useRef(new Set<Promise<State>>());
  const configurationTail = useRef(Promise.resolve());
  const snapshotOrder = useRef(new SnapshotOrder());
  const [connection, setConnection] = useState(0);
  const [view, setView] = useState<Workspace>("sources");
  const lastWorkspace = useRef<Workspace>("sources");
  const applyState = useCallback((next: State) => {
    if (!snapshotOrder.current.accept(next)) return;
    setState(next);
    if (next.lastError) setError(next.lastError);
    if (next.workspace !== lastWorkspace.current) {
      setView((current) => (current === "combat" ? current : next.workspace));
      lastWorkspace.current = next.workspace;
    }
  }, []);
  const run: Run = useCallback(
    async (command, payload = {}) => {
      // Keep radio controls focusable while serializing their configuration writes.
      let releaseConfiguration: (() => void) | undefined;
      if (command === "configure") {
        const previousConfiguration = configurationTail.current;
        configurationTail.current = new Promise<void>((resolve) => {
          releaseConfiguration = resolve;
        });
        await previousConfiguration;
        while (inFlight.current.size > 0) {
          await Promise.allSettled([...inFlight.current]);
        }
      }
      if (
        pendingCount.current > 0 &&
        command !== "cancel" &&
        command !== "navigate"
      ) {
        releaseConfiguration?.();
        return;
      }
      pendingCount.current++;
      setPending(true);
      setError("");
      let operation: Promise<State> | undefined;
      try {
        operation = request(command, payload);
        inFlight.current.add(operation);
        const next = await operation;
        applyState(next);
        return next;
      } catch (e) {
        setError(String(e instanceof Error ? e.message : e));
      } finally {
        if (operation) inFlight.current.delete(operation);
        pendingCount.current--;
        setPending(pendingCount.current > 0);
        releaseConfiguration?.();
      }
    },
    [applyState],
  );
  useEffect(() => {
    let disposed = false;
    let cleanup: (() => void) | undefined;
    setError("");
    void (async () => {
      try {
        cleanup = await subscribe(
          (s) => {
            if (!disposed) applyState(s);
          },
          (e) => {
            if (!disposed) {
              setError(e);
            }
          },
          (next) => {
            if (!disposed) receiveTrainer(next);
          },
          (message) => {
            if (!disposed) reportTransportError(message);
          },
        );
        if (disposed) {
          cleanup();
          return;
        }
        const [next] = await Promise.all([request("state"), refreshTrainer()]);
        if (!disposed) {
          applyState(next);
        }
      } catch (e) {
        if (!disposed) setError(String(e instanceof Error ? e.message : e));
      }
    })();
    return () => {
      disposed = true;
      cleanup?.();
    };
  }, [
    connection,
    applyState,
    receiveTrainer,
    refreshTrainer,
    reportTransportError,
  ]);
  useEffect(() => {
    const preference = state?.theme ?? "system";
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const update = () => {
      const dark =
        preference === "dark" || (preference === "system" && media.matches);
      document.documentElement.classList.toggle("dark", dark);
      if (isTauri())
        void getCurrentWindow()
          .setTheme(preference === "system" ? null : preference)
          .catch(() => {});
    };
    update();
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, [state?.theme]);
  useEffect(() => {
    document.title = `${routes.find((r) => r.id === view)?.label} · Grim Dawn 配装工坊`;
  }, [view]);
  const character = state?.characters.find(
    (c) => c.id === state.selectedCharacterId,
  );
  const disabled = pending || !!state?.isBusy;
  const navigate = (target: Workspace) => {
    setView(target);
    if (
      target === "sources" ||
      (target === "build" && state?.selectedCharacterId) ||
      (target === "results" && state?.results.length)
    )
      void run("navigate", { workspace: target });
  };
  return (
    <TooltipProvider delayDuration={300}>
      <Tabs
        className="app-shell"
        value={view}
        onValueChange={(value) => navigate(value as Workspace)}
      >
        <header className="app-chrome">
          <div className="app-topbar">
            <div className="topbar-content">
              <div className="topbar-brand">
                <Button
                  variant="ghost"
                  className="brand"
                  onClick={() => navigate("sources")}
                  aria-label="Grim Dawn Optimizer，返回角色库"
                >
                  <img src={appIcon} alt="" width={30} height={30} />
                  <strong>Grim Dawn Optimizer</strong>
                </Button>
              </div>
              <TabsList
                variant="line"
                aria-label="工作区"
                className="workspace-tabs"
              >
                {routes.map((r) => (
                  <TabsTrigger key={r.id} value={r.id}>
                    {r.label}
                  </TabsTrigger>
                ))}
              </TabsList>
              <div className="header-tools">
                {character && view !== "combat" && (
                  <span className="header-character">
                    {character.name}
                    <span>Lv. {character.level}</span>
                  </span>
                )}
                <Settings state={state} run={run} updater={updater} />
              </div>
            </div>
          </div>
        </header>
        <main
          id="main-content"
          className="main-content"
          inert={updater.installing}
        >
          {error && (
            <div className="connection-error" role="alert">
              <span>{error}</span>
              <Button
                variant="outline"
                size="sm"
                onClick={() => setConnection((x) => x + 1)}
              >
                重新连接
              </Button>
              <Button
                variant="ghost"
                size="icon"
                aria-label="关闭提示"
                onClick={() => setError("")}
              >
                <X />
              </Button>
            </div>
          )}
          <TabsContent value={view} className="workspace-tab-content">
            <h1 className="sr-only">
              {routes.find((r) => r.id === view)?.label}
            </h1>
            {view === "combat" ? (
              <CombatTools controller={trainer} />
            ) : !state ? (
              <EmptyState
                icon={
                  <LoaderCircle
                    className={error ? "" : "animate-spin"}
                    size={32}
                  />
                }
                title={error ? "暂时无法连接计算服务" : "正在准备配装工坊"}
              >
                正在读取本机设置与数据源。首次启动可能需要一些时间。
              </EmptyState>
            ) : view === "sources" ? (
              <Sources
                state={state}
                run={run}
                disabled={disabled}
                navigate={navigate}
              />
            ) : view === "build" ? (
              <Build
                state={state}
                run={run}
                disabled={disabled}
                navigate={navigate}
              />
            ) : (
              <Results
                state={state}
                run={run}
                disabled={disabled}
                navigate={navigate}
              />
            )}
          </TabsContent>
        </main>
        <footer className="status-bar">
          <div role="status" aria-live="polite">
            {(view === "combat" ? trainer.state?.connecting : state?.isBusy) ? (
              <LoaderCircle size={13} className="animate-spin" />
            ) : (
              <span className="status-dot" />
            )}
            <span>
              {updater.installing
                ? `正在下载并安装更新${updater.progress === null ? "…" : ` ${updater.progress}%`}`
                : view === "combat"
                  ? trainer.transportError
                    ? "实战工具连接中断"
                    : trainer.state?.statusText || "连接实战工具"
                  : state?.status || "连接本地计算服务"}
            </span>
          </div>
          <span className="footer-note">
            {view === "combat" ? "当前游戏会话" : "只读存档"}
            <span>·</span>
            {state?.version || "1.0.27"}
          </span>
        </footer>
        {state?.isBusy && (
          <div className="global-progress">
            <Progress value={state.progress} aria-label="任务进度" />
            <Button
              variant="outline"
              size="sm"
              disabled={!state.canCancel}
              onClick={() => void run("cancel")}
            >
              <X size={14} />
              取消
            </Button>
          </div>
        )}
      </Tabs>
    </TooltipProvider>
  );
}

function Settings({
  state,
  run,
  updater,
}: {
  state: State | null;
  run: Run;
  updater: AppUpdater;
}) {
  return (
    <Dialog>
      <Tooltip>
        <TooltipTrigger asChild>
          <DialogTrigger asChild>
            <Button variant="ghost" size="icon" aria-label="设置与关于">
              <Settings2 />
            </Button>
          </DialogTrigger>
        </TooltipTrigger>
        <TooltipContent>设置与关于</TooltipContent>
      </Tooltip>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>工坊设置</DialogTitle>
          <DialogDescription>选择适合你的工作环境。</DialogDescription>
        </DialogHeader>
        <div className="settings-body">
          <Label id="appearance-label">界面外观</Label>
          <RadioGroup
            className="theme-options"
            aria-labelledby="appearance-label"
            value={state?.theme ?? "system"}
            disabled={!state || state.isBusy || updater.installing}
            onValueChange={(theme) =>
              void run("configure", { theme: theme as Theme })
            }
          >
            {(
              [
                { id: "system", label: "跟随系统", icon: Monitor },
                { id: "light", label: "浅色", icon: Sun },
                { id: "dark", label: "深色", icon: Moon },
              ] as const
            ).map((t) => (
              <Label
                key={t.id}
                className="theme-option"
                htmlFor={`theme-${t.id}`}
                data-checked={state?.theme === t.id}
              >
                <t.icon size={22} />
                <span>{t.label}</span>
                <RadioGroupItem id={`theme-${t.id}`} value={t.id} />
              </Label>
            ))}
          </RadioGroup>
          <AppUpdates updater={updater} />
          <div className="about-copy">
            <img src={appIcon} alt="" width={28} height={28} />
            <div>
              <strong>Grim Dawn 配装工坊</strong>
              <p>配装分析只读存档；实战工具修改当前游戏会话。</p>
              <p>
                候选方案来自限时搜索，不保证全局最优。
                <br />
                实际效果请结合游戏内面板确认。
              </p>
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function Sources({
  state,
  run,
  disabled,
  navigate,
}: {
  state: State;
  run: Run;
  disabled: boolean;
  navigate: (v: Workspace) => void;
}) {
  return (
    <div className="page-enter sources-page">
      <div className="sources-layout">
        <section className="source-config workspace-section">
          <div className="section-header">
            <h2 className="section-title" role="heading" aria-level={2}>
              数据源
            </h2>
            <div className="section-tools">
              <Button
                variant="ghost"
                size="sm"
                disabled={disabled}
                onClick={() => void run("discover")}
              >
                <RefreshCw />
                检测目录
              </Button>
            </div>
          </div>
          <div className="section-body source-fields">
            <SourceField
              kind="game"
              title="游戏安装目录"
              value={state.selectedGamePath ?? ""}
              run={run}
              disabled={disabled}
            />
            <SourceField
              kind="save"
              title="角色存档目录"
              value={state.selectedSavePath ?? ""}
              run={run}
              disabled={disabled}
            />
          </div>
          <div className="section-actions source-actions">
            <ActionButton
              className="load-button"
              size="lg"
              disabled={disabled || !state.canLoad}
              busy={state.isBusy}
              onClick={() => void run("load")}
            >
              <Download size={16} />
              读取角色
              <ArrowRight size={16} />
            </ActionButton>
            <Button
              variant="ghost"
              disabled={disabled}
              onClick={() => void run("demo")}
            >
              使用演示角色
            </Button>
          </div>
          <div className="read-note">
            <CircleHelp size={16} />
            <p>换装后请先在游戏中保存，再刷新角色。</p>
          </div>
          {state.importWarnings.length > 0 && (
            <Disclosure
              title="读取说明"
              description={`${state.importWarnings.length} 项`}
            >
              <ul className="detail-list">
                {state.importWarnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            </Disclosure>
          )}
        </section>
        <section className="character-library workspace-section">
          <div className="section-header library-heading">
            <h2 className="section-title" role="heading" aria-level={2}>
              我的角色{" "}
              <Badge variant="secondary">{state.characters.length}</Badge>
            </h2>
            {state.characters.length > 0 && (
              <div className="section-meta">
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={disabled || !state.canRefresh}
                  onClick={() => void run("refresh")}
                >
                  <RefreshCw size={14} />
                  刷新角色
                </Button>
              </div>
            )}
          </div>
          <div className="section-body library-content">
            {!state.characters.length ? (
              <div className="library-empty">
                <h3>暂无角色</h3>
                <p>选择目录后，点击“读取角色”。</p>
              </div>
            ) : (
              <div className="character-list">
                {state.characters.map((c) => (
                  <Button
                    variant="ghost"
                    key={c.id}
                    className="character-row"
                    aria-pressed={c.id === state.selectedCharacterId}
                    disabled={disabled}
                    onClick={() => void run("selectCharacter", { id: c.id })}
                  >
                    <div className="character-name">
                      <strong>{c.name}</strong>
                      <span>
                        {c.hardcore ? "专家角色" : "普通角色"}
                        <span>·</span>
                        {c.itemCount} 件装备<span>·</span>
                        {c.attachmentCount} 个附件
                      </span>
                    </div>
                    <span className="character-level">Lv. {c.level}</span>
                    <span className="selection-check">
                      {c.id === state.selectedCharacterId ? (
                        <Check size={16} />
                      ) : (
                        <ChevronRight size={16} />
                      )}
                    </span>
                  </Button>
                ))}
              </div>
            )}
          </div>
          {!!state.selectedCharacterId && (
            <div className="section-actions selected-character-summary">
              <Button disabled={disabled} onClick={() => navigate("build")}>
                分析此角色
                <ArrowRight />
              </Button>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}

function SourceField({
  kind,
  title,
  value,
  run,
  disabled,
}: {
  kind: "game" | "save";
  title: string;
  value: string;
  run: Run;
  disabled: boolean;
}) {
  const command =
    kind === "game" ? "selectGameDirectory" : "selectSaveDirectory";
  return (
    <PathInput
      className="source-field"
      label={title}
      value={value}
      kind={kind === "game" ? "gameDirectory" : "saveDirectory"}
      disabled={disabled}
      onCommit={async (path) => Boolean(await run(command, { path }))}
      commitError="目录未能应用，请检查路径；详细原因见上方提示。"
      placeholder="输入完整目录路径"
    />
  );
}

function Build({
  state,
  run,
  disabled,
  navigate,
}: {
  state: State;
  run: Run;
  disabled: boolean;
  navigate: (v: Workspace) => void;
}) {
  if (!state.selectedCharacterId)
    return (
      <EmptyState
        icon={<img src={appIcon} alt="" width={40} height={40} />}
        title="先选择一个角色"
        action={
          <Button onClick={() => navigate("sources")}>
            前往角色库
            <ArrowRight />
          </Button>
        }
      >
        读取角色后，在这里设定目标并寻找更合适的装备组合。
      </EmptyState>
    );
  return (
    <div className="page-enter">
      <div className="build-layout">
        <section className="build-config workspace-section">
          <div className="section-header">
            <h2 className="section-title" role="heading" aria-level={2}>
              分析条件
            </h2>
            <div className="section-tools">
              <Button
                variant="ghost"
                size="sm"
                disabled={disabled || !state.canSaveProfile}
                onClick={() => void run("saveProfile")}
              >
                <Download />
                保存配置
              </Button>
            </div>
          </div>
          <div className="section-body config-section">
            <SelectField
              label="主攻技能"
              value={state.selectedSkillRecord ?? ""}
              options={state.skills.map((s) => ({
                value: s.record,
                label: s.name,
              }))}
              onChange={(skillRecord) => void run("configure", { skillRecord })}
              disabled={disabled}
            />
            <SelectField
              label="战斗难度"
              value={String(state.difficultyIndex)}
              options={state.difficulties.map((d, i) => ({
                value: String(i),
                label: d,
              }))}
              onChange={(i) =>
                void run("configure", { difficultyIndex: Number(i) })
              }
              disabled={disabled}
            />
            <div className="field">
              <Label id="analysis-goal-label">分析目标</Label>
              <RadioGroup
                className="goal-options"
                aria-labelledby="analysis-goal-label"
                value={String(state.goalIndex)}
                disabled={state.isBusy}
                onValueChange={(value) =>
                  void run("configure", { goalIndex: Number(value) })
                }
              >
                {state.goals.map((g, i) => (
                  <Label
                    key={g}
                    className="goal-option"
                    htmlFor={`goal-${i}`}
                    data-checked={state.goalIndex === i}
                  >
                    <RadioGroupItem value={String(i)} id={`goal-${i}`} />
                    <span>{g}</span>
                  </Label>
                ))}
              </RadioGroup>
            </div>
          </div>
          <div className="section-actions analyze-action">
            <ActionButton
              size="lg"
              disabled={disabled || !state.canOptimize}
              busy={state.isBusy}
              onClick={() => void run("optimize")}
            >
              {state.isBusy ? "正在分析" : "开始分析"}
            </ActionButton>
            {!state.canOptimize && !state.isBusy && (
              <p>{state.panel.requiredStatus}</p>
            )}
          </div>
        </section>
        <div className="build-main">
          <section className="current-overview workspace-section">
            <div className="section-body metrics-row">
              <div className="primary-metric">
                <label>主攻面板 DPS</label>
                <strong>{state.panel.dps}</strong>
              </div>
              <div>
                <label>默认攻击 DPS</label>
                <strong>{state.panel.defaultDps}</strong>
              </div>
              <div>
                <label>生命</label>
                <strong>{state.panel.health}</strong>
              </div>
            </div>
          </section>
          <section className="equipment-section workspace-section">
            <div className="section-header equipment-heading">
              <h2 className="section-title" role="heading" aria-level={2}>
                已装备物品
              </h2>
              <div className="section-tools">
                <span className="section-meta">
                  {state.equipment.length} 个槽位
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={disabled || !state.canRefresh}
                  onClick={() => void run("refresh")}
                >
                  <RefreshCw />
                  刷新角色
                </Button>
              </div>
            </div>
            <div className="section-body equipment-grid">
              {state.equipment.map((item) => (
                <div className="equipment-item" key={item.slotKey}>
                  <div>
                    <small>{item.slot}</small>
                    <strong>{item.name || "未装备"}</strong>
                  </div>
                </div>
              ))}
            </div>
          </section>
          <Disclosure
            title="计算范围"
            description={
              state.panel.hasWarning ? "部分效果未完整计算" : undefined
            }
          >
            <p className="muted">{state.panel.summary}</p>
            <p className="pre-line muted">{state.panel.coverage}</p>
          </Disclosure>
          {state.buffs.length > 0 && (
            <Disclosure
              title="临时增益"
              description={`${state.buffs.filter((b) => b.isActive).length} 项已启用`}
            >
              <p className="muted">
                仅在对照增益生效时的面板时启用。刷新角色后自动清空。
              </p>
              <div className="buff-list">
                {state.buffs.map((b) => (
                  <div className="buff-row" key={b.record}>
                    <div>
                      <label htmlFor={`buff-${b.record}`}>{b.name}</label>
                      <p>{b.description}</p>
                    </div>
                    <Switch
                      id={`buff-${b.record}`}
                      checked={b.isActive}
                      disabled={disabled}
                      onCheckedChange={(isActive) =>
                        void run("configureBuff", {
                          record: b.record,
                          isActive,
                        })
                      }
                    />
                  </div>
                ))}
              </div>
            </Disclosure>
          )}
          <Disclosure title="特殊战斗场景" description="时长、目标与额外降抗">
            <div className="scenario-grid">
              <NumericField
                label="战斗时长（秒）"
                value={state.scenario.durationSeconds}
                min={1}
                max={300}
                disabled={disabled}
                onCommit={(durationSeconds) =>
                  void run("configure", { durationSeconds })
                }
              />
              <NumericField
                label="目标数量"
                integer
                value={state.scenario.targetCount}
                min={1}
                max={20}
                disabled={disabled}
                onCommit={(targetCount) =>
                  void run("configure", { targetCount })
                }
              />
              <NumericField
                label="敌人额外增伤（%）"
                value={state.scenario.enemyDamageIncreasePercent}
                min={0}
                max={1000}
                disabled={disabled}
                onCommit={(enemyDamageIncreasePercent) =>
                  void run("configure", { enemyDamageIncreasePercent })
                }
              />
            </div>
            <p className="muted pre-line">{state.scenario.enemySummary}</p>
            <h3 className="subheading">玩家额外降抗（百分点）</h3>
            <div className="resistance-grid">
              {state.scenario.resistanceDebuffs.map((r) => (
                <NumericField
                  key={r.type}
                  label={r.label}
                  value={r.reduction}
                  min={0}
                  max={100}
                  disabled={disabled}
                  onCommit={(value) =>
                    void run("configure", {
                      resistanceDebuffs: { [r.type]: value },
                    })
                  }
                />
              ))}
            </div>
            <p className="muted pre-line">{state.scenario.enemyResistances}</p>
          </Disclosure>
        </div>
      </div>
    </div>
  );
}

function Results({
  state,
  run,
  disabled,
  navigate,
}: {
  state: State;
  run: Run;
  disabled: boolean;
  navigate: (v: Workspace) => void;
}) {
  const [onlyChanged, setOnlyChanged] = useState(false);
  const sizingRows = useMemo(
    () => state.results.flatMap((result) => result.slotRows),
    [state.results],
  );
  const selected =
    state.results.find((r) => r.rank === state.selectedResultRank) ??
    state.results[0];
  if (!selected)
    return (
      <EmptyState
        icon={<ArrowDownUp size={32} />}
        title={state.isBusy ? "正在寻找更合适的方案" : "方案，还在等待发现"}
        action={
          <Button
            onClick={() =>
              navigate(state.selectedCharacterId ? "build" : "sources")
            }
          >
            {state.selectedCharacterId ? "前往配装工坊" : "前往角色库"}
            <ArrowRight />
          </Button>
        }
      >
        {state.isBusy
          ? "分析完成后，可以在这里查看逐槽替换和预期变化。"
          : "选择角色并开始分析，候选方案会出现在这里。修改分析条件后，需要重新分析。"}
      </EmptyState>
    );
  const rows = selected.slotRows.filter((r) => !onlyChanged || r.changed);
  return (
    <div className="page-enter">
      <div className="results-layout">
        <section className="candidate-panel workspace-section">
          <div className="section-header candidate-heading">
            <h2 className="section-title" role="heading" aria-level={2}>
              候选方案
            </h2>
            <div className="section-meta candidate-column-label">面板 DPS</div>
          </div>
          <div className="section-body candidate-list">
            {state.results.map((r) => (
              <Button
                variant="ghost"
                className="candidate-button"
                key={r.rank}
                disabled={disabled}
                aria-pressed={r.rank === selected.rank}
                aria-label={`${r.title}，面板 DPS ${r.panelDps}，${r.changeSummary}`}
                onClick={() => void run("selectResult", { rank: r.rank })}
              >
                <strong>{r.title}</strong>
                <span className="candidate-dps">{r.panelDps}</span>
              </Button>
            ))}
          </div>
        </section>
        <section className="result-summary workspace-section">
          <div className="result-identity">
            <h2>
              {selected.title}
              <Badge variant="outline">{selected.validity}</Badge>
            </h2>
            <div className="result-context">
              <span>{selected.changeSummary}</span>
              <span className="result-confidence">
                <Shield size={14} />
                {selected.confidence}
              </span>
            </div>
          </div>
          <div className="section-body metrics-row result-metrics">
            <div className="primary-metric">
              <label>主攻面板 DPS</label>
              <strong>{selected.panelDps}</strong>
              <small>仅装备本体 {selected.equipmentOnlyDps}</small>
            </div>
            <div>
              <label>预计实战 DPS</label>
              <strong>{selected.dps}</strong>
              <small>{selected.monteCarloRange}</small>
            </div>
            <div>
              <label>有效生命</label>
              <strong>{selected.effectiveHealth}</strong>
              <small>预计存活 {selected.timeToDeath}</small>
            </div>
          </div>
          <div className="section-tools result-actions">
            <Button variant="ghost" size="sm" onClick={() => navigate("build")}>
              <SlidersHorizontal />
              调整条件
            </Button>
          </div>
        </section>
        <section className="result-detail">
          <section className="equipment-comparison workspace-section">
            <EquipmentComparisonTable
              rows={rows}
              sizingRows={sizingRows}
              caption={`${selected.title}逐槽装备与附件对比`}
              renderToolbar={({ resetLayout, rowCount }) => (
                <div className="section-header comparison-toolbar">
                  <h2 className="section-title">
                    装备替换清单
                    <span className="section-meta">{rowCount} 个槽位</span>
                  </h2>
                  <div className="section-tools">
                    <div className="inline-switch">
                      <label htmlFor="changed-only">只看变化</label>
                      <Switch
                        id="changed-only"
                        checked={onlyChanged}
                        onCheckedChange={setOnlyChanged}
                      />
                    </div>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={resetLayout}
                      aria-label="重置布局"
                      title="重置布局"
                    >
                      <RotateCcw />
                      <span className="comparison-reset-label">重置布局</span>
                    </Button>
                  </div>
                </div>
              )}
            />
          </section>
          <div className="result-extra">
            <Notice tone={selected.warningRows.length ? "warning" : "neutral"}>
              {selected.diagnosticSummary}
            </Notice>
            <Disclosure
              title="实战与计算范围"
              description={`${selected.warningRows.length} 项待确认`}
            >
              <p>{selected.scoreSummary}</p>
              <p className="muted">{selected.scoreBreakdown}</p>
              <p className="muted pre-line">{selected.combatResistances}</p>
              <ul className="detail-list">
                {[...selected.warningRows, ...selected.informationRows].map(
                  (w, i) => (
                    <li key={i}>{w}</li>
                  ),
                )}
              </ul>
            </Disclosure>
            {selected.singleSwaps.length > 0 && (
              <SwapComparison state={state} run={run} disabled={disabled} />
            )}
          </div>
        </section>
      </div>
    </div>
  );
}

function SwapComparison({
  state,
  run,
  disabled,
}: {
  state: State;
  run: Run;
  disabled: boolean;
}) {
  const result =
    state.results.find((r) => r.rank === state.selectedResultRank) ??
    state.results[0];
  const index = result?.selectedSwapIndex ?? 0;
  const swap = result?.singleSwaps[index];
  if (!swap) return null;
  return (
    <Disclosure title="单件换装对照" description="固定其他装备，查看单件差异">
      <p className="muted">
        使用该物品已有的组件与增幅，与整套方案的附件可能不同。
      </p>
      <SelectField
        label="选择换装物品"
        value={String(index)}
        options={result.singleSwaps.map((s, i) => ({
          value: String(i),
          label: s.title,
        }))}
        disabled={disabled}
        onChange={(i) => void run("compareItem", { index: Number(i) })}
      />
      <p className="muted swap-details">{swap.explanation}</p>
      <div className="equipment-table-wrap">
        <Table className="equipment-table measurement-table">
          <TableCaption className="sr-only">
            单件换装指标与实测对照
          </TableCaption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">指标</TableHead>
              <TableHead scope="col">当前</TableHead>
              <TableHead scope="col">预测</TableHead>
              <TableHead scope="col">变化</TableHead>
              <TableHead scope="col">游戏实测（可选）</TableHead>
              <TableHead scope="col">误差</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {swap.rows.map((row) => (
              <TableRow key={row.key}>
                <TableHead scope="row">{row.label}</TableHead>
                <TableCell>{row.before}</TableCell>
                <TableCell>{row.predicted}</TableCell>
                <TableCell>{row.change}</TableCell>
                <TableCell>
                  <MeasuredField
                    key={`${result.rank}-${index}-${row.key}`}
                    label={`${row.label}实测值`}
                    value={row.actualInput}
                    disabled={disabled}
                    onCommit={(actualInput) =>
                      void run("compareItem", {
                        index,
                        metricKey: row.key,
                        actualInput,
                      })
                    }
                  />
                </TableCell>
                <TableCell className="muted">{row.deviation || "—"}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </Disclosure>
  );
}
function MeasuredField({
  label,
  value,
  disabled,
  onCommit,
}: {
  label: string;
  value: string;
  disabled: boolean;
  onCommit: (value: string) => void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return (
    <div className="field">
      <label htmlFor={label} className="sr-only">
        {label}
      </label>
      <Input
        id={label}
        inputMode="decimal"
        value={draft}
        disabled={disabled}
        placeholder="输入实测值"
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => {
          if (draft !== value) onCommit(draft);
        }}
      />
    </div>
  );
}
