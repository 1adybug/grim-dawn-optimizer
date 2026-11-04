export type TrainerFeatureId =
  | "life"
  | "mana"
  | "kill"
  | "moveSpeed"
  | "attackSpeed"
  | "castSpeed"
  | "cooldown"
  | "autoHealth"
  | "autoMana";

export interface TrainerFeature {
  id: TrainerFeatureId;
  title: string;
  description: string;
  shortcut: string;
  enabled: boolean;
  requested: boolean;
  applied: boolean;
  pending: boolean;
}

export interface TrainerState {
  sessionId: string;
  revision: number;
  configuredExecutable: string | null;
  autoConnect: boolean;
  autoConnectPaused: boolean;
  connected: boolean;
  connecting: boolean;
  available: boolean;
  status:
    | "disconnected"
    | "connecting"
    | "waiting"
    | "ready"
    | "multiplayer"
    | "stale"
    | "fault"
    | "error";
  statusText: string;
  error: string | null;
  hotkeyWarning: string | null;
  pid: number | null;
  architecture: string | null;
  movementMultiplier: number;
  lifeMode: "infinite" | "lock";
  lifeTriggers: number;
  deathCount: number | null;
  overlayRevision: number;
  overlayStatus: number;
  features: TrainerFeature[];
  health: number | null;
  maxHealth: number | null;
  mana: number | null;
  maxMana: number | null;
}

export type TrainerCommand =
  | "trainerState"
  | "trainerSelectExecutable"
  | "trainerConnect"
  | "trainerSetAutoConnect"
  | "trainerDisconnect"
  | "trainerSetFeature"
  | "trainerSetMovementMultiplier"
  | "trainerSetLifeMode"
  | "trainerToggleOverlay"
  | "trainerDisableAll"
  | "trainerLaunch";
