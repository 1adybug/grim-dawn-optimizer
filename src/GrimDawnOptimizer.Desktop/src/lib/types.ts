export type Workspace = "sources" | "build" | "results" | "combat";
export type Theme = "system" | "light" | "dark";
export interface Character {
  id: string;
  name: string;
  level: number;
  hardcore: boolean;
  itemCount: number;
  attachmentCount: number;
}
export interface Equipment {
  slot: string;
  slotKey: string;
  name: string;
  source: string;
}
export interface SlotRow {
  slot: string;
  currentItem: string;
  candidateItem: string;
  component: string;
  augment: string;
  source: string;
  changed: boolean;
}
export interface Swap {
  index: number;
  title: string;
  explanation: string;
  rows: {
    key: string;
    label: string;
    before: string;
    predicted: string;
    delta: number | null;
    change: string;
    actualInput: string;
    deviation: string;
  }[];
}
export interface Result {
  rank: number;
  title: string;
  changeSummary: string;
  diagnosticSummary: string;
  dps: string;
  panelDps: string;
  equipmentOnlyDps: string;
  effectiveHealth: string;
  timeToKill: string;
  timeToDeath: string;
  confidence: string;
  validity: string;
  monteCarloRange: string;
  scoreSummary: string;
  scoreBreakdown: string;
  combatResistances: string;
  retaliationDps: string;
  coverageExplanation: string;
  hasWarnings: boolean;
  selectedSwapIndex: number | null;
  slotRows: SlotRow[];
  warningRows: string[];
  informationRows: string[];
  singleSwaps: Swap[];
}
export interface State {
  protocolVersion: number;
  sessionId: string;
  revision: number;
  version: string;
  ready: boolean;
  isBusy: boolean;
  status: string;
  progress: number;
  lastError: string | null;
  workspace: Workspace;
  canLoad: boolean;
  canRefresh: boolean;
  canOptimize: boolean;
  canCancel: boolean;
  canSaveProfile: boolean;
  theme: Theme;
  installations: {
    path: string;
    source: string;
    version: string;
    fileCount: number;
  }[];
  saveLocations: { path: string; source: string; characterCount: number }[];
  selectedGamePath: string | null;
  selectedSavePath: string | null;
  characters: Character[];
  selectedCharacterId: string | null;
  characterSummary: string;
  skills: { record: string; name: string; level: number }[];
  selectedSkillRecord: string | null;
  goals: string[];
  goalIndex: number;
  difficulties: string[];
  difficultyIndex: number;
  panel: {
    dps: string;
    defaultDps: string;
    health: string;
    coverage: string;
    summary: string;
    requiredStatus: string;
    hasWarning: boolean;
    foundationSummary: string;
  };
  scenario: {
    durationSeconds: number;
    targetCount: number;
    enemyDamageIncreasePercent: number;
    enemySummary: string;
    enemyResistances: string;
    scoreSummary: string;
    resistanceDebuffs: {
      type: string;
      label: string;
      reduction: number;
      penalty: string;
    }[];
  };
  buffs: {
    record: string;
    name: string;
    description: string;
    isActive: boolean;
  }[];
  equipment: Equipment[];
  importWarnings: string[];
  searchStats: string;
  results: Result[];
  selectedResultRank: number | null;
}
export type Run = (
  command: string,
  payload?: Record<string, unknown>,
) => Promise<State | undefined>;
