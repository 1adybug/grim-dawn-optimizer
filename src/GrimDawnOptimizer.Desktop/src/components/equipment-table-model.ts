import type { ColumnDef, ColumnSizingState } from "@tanstack/react-table";
import type { SlotRow } from "@/lib/types";

export interface EquipmentComparisonRow extends SlotRow {
  componentName: string;
  componentSource: string;
  augmentName: string;
  augmentSource: string;
}

export const equipmentColumnPinning = {
  left: ["slot", "candidateItem"],
  right: [],
};

export const equipmentLayoutStorageKey =
  "grim-dawn-optimizer.equipment-table-layout.v1";

const columnRules = [
  { id: "slot", header: "槽位", min: 64, max: 96, defaultMax: 96 },
  {
    id: "candidateItem",
    header: "候选装备",
    min: 112,
    max: 288,
    defaultMax: 288,
  },
  {
    id: "currentItem",
    header: "当前装备",
    min: 112,
    max: 420,
    defaultMax: 320,
  },
  { id: "componentName", header: "组件", min: 88, max: 360, defaultMax: 240 },
  {
    id: "componentSource",
    header: "组件来源",
    min: 128,
    max: 560,
    defaultMax: 400,
  },
  { id: "augmentName", header: "增幅", min: 88, max: 360, defaultMax: 240 },
  {
    id: "augmentSource",
    header: "增幅来源",
    min: 128,
    max: 560,
    defaultMax: 400,
  },
  // The final column also fills wide windows; its authored upper bound must
  // allow a drag to continue from that expanded, visible starting boundary.
  { id: "source", header: "装备位置", min: 128, max: 4000, defaultMax: 400 },
] as const;

type LayoutStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;

function browserStorage(): LayoutStorage | null {
  try {
    return typeof window === "undefined" ? null : window.localStorage;
  } catch {
    return null;
  }
}

/** Only explicit, finite widths for maintained columns belong in preferences. */
export function sanitizeEquipmentColumnSizing(
  value: unknown,
): ColumnSizingState {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  const source = value as Record<string, unknown>;
  const result: ColumnSizingState = {};
  for (const rule of columnRules) {
    if (!Object.prototype.hasOwnProperty.call(source, rule.id)) continue;
    const width = source[rule.id];
    if (typeof width !== "number" || !Number.isFinite(width) || width <= 0)
      continue;
    result[rule.id] = Math.max(rule.min, Math.min(rule.max, Math.round(width)));
  }
  return result;
}

export function parseEquipmentColumnSizing(
  serialized: string | null,
): ColumnSizingState {
  if (!serialized || serialized.length > 8192) return {};
  try {
    const value: unknown = JSON.parse(serialized);
    if (!value || typeof value !== "object" || Array.isArray(value)) return {};
    const saved = value as Record<string, unknown>;
    return saved.schemaVersion === 1
      ? sanitizeEquipmentColumnSizing(saved.columnSizing)
      : {};
  } catch {
    return {};
  }
}

export function readEquipmentColumnSizing(
  storage: LayoutStorage | null = browserStorage(),
): ColumnSizingState {
  try {
    return parseEquipmentColumnSizing(
      storage?.getItem(equipmentLayoutStorageKey) ?? null,
    );
  } catch {
    return {};
  }
}

export function writeEquipmentColumnSizing(
  sizing: ColumnSizingState,
  storage: LayoutStorage | null = browserStorage(),
): boolean {
  if (!storage) return false;
  try {
    const safe = sanitizeEquipmentColumnSizing(sizing);
    if (Object.keys(safe).length === 0)
      storage.removeItem(equipmentLayoutStorageKey);
    else
      storage.setItem(
        equipmentLayoutStorageKey,
        JSON.stringify({ schemaVersion: 1, columnSizing: safe }),
      );
    return true;
  } catch {
    return false;
  }
}

export function splitAttachment(value: string) {
  const [first = "", ...remaining] = value.split(/\r?\n/);
  return {
    name: first.trim() || "—",
    source: remaining.join("\n").trim() || "—",
  };
}

export function toEquipmentComparisonRows(
  rows: SlotRow[],
): EquipmentComparisonRow[] {
  return rows.map((row) => {
    const component = splitAttachment(row.component);
    const augment = splitAttachment(row.augment);
    return {
      ...row,
      componentName: component.name,
      componentSource: component.source,
      augmentName: augment.name,
      augmentSource: augment.source,
    };
  });
}

/** Deterministic approximation of the local Inter/Noto Sans SC text at 12px.
 * CJK/full-width characters occupy an em; proportional Latin glyphs do not.
 * Newlines use the same separator shown by the single-line cells. */
export function estimateEquipmentTextWidth(
  value: string,
  fontSize = 12,
): number {
  let ems = 0;
  for (const character of value.replace(/\r?\n/g, " · ")) {
    const code = character.codePointAt(0)!;
    if (/\p{Mark}/u.test(character) || code === 0x200d || code === 0xfe0f)
      continue;
    if (/\s/u.test(character)) ems += 0.32;
    else if (
      code > 0xffff ||
      /[\u2e80-\ua4cf\uac00-\ud7af\uf900-\ufaff\uff01-\uff60\uffe0-\uffe6]/u.test(
        character,
      )
    )
      ems += 1;
    else if (/[ilIjtfr.,:;!'|]/.test(character)) ems += 0.3;
    else if (/[MWmw@%&]/.test(character)) ems += 0.86;
    else if (/[A-Z]/.test(character)) ems += 0.68;
    else if (/[0-9]/.test(character)) ems += 0.58;
    else if (/[a-z]/.test(character)) ems += 0.54;
    else ems += 0.48;
  }
  return Math.ceil(ems * fontSize);
}

export function predictEquipmentColumnWidths(
  rows: SlotRow[],
): ColumnSizingState {
  const data = toEquipmentComparisonRows(rows);
  return Object.fromEntries(
    columnRules.map((rule) => {
      let widest = estimateEquipmentTextWidth(rule.header, 11) + 30;
      for (const row of data) {
        const text = row[rule.id];
        const weightAllowance = rule.id === "candidateItem" ? 1.04 : 1;
        const changeMarker = rule.id === "slot" && row.changed ? 17 : 0;
        widest = Math.max(
          widest,
          estimateEquipmentTextWidth(text) * weightAllowance +
            24 +
            changeMarker,
        );
      }
      // Round up to a small spacing unit. Long source descriptions remain
      // scrollable/clickable instead of forcing an unreasonable initial width.
      return [
        rule.id,
        Math.max(
          rule.min,
          Math.min(rule.defaultMax, Math.ceil(widest / 4) * 4),
        ),
      ];
    }),
  );
}

/** Bound the fixed area to 60% of the container (184–384px). Width preferences
 * stay in sizing state, so expanding the window restores the authored widths. */
export function equipmentColumns(
  containerWidth: number,
  sizing: ColumnSizingState,
  defaults: ColumnSizingState = predictEquipmentColumnWidths([]),
): ColumnDef<EquipmentComparisonRow, string>[] {
  const pinnedBudget = Math.min(
    384,
    Math.max(184, Math.floor(containerWidth * 0.6)),
  );
  const slotMax = Math.min(96, pinnedBudget - 112);
  const slotWidth = Math.max(
    64,
    Math.min(slotMax, sizing.slot ?? defaults.slot ?? 64),
  );
  const visibleSizing = equipmentVisibleColumnSizing(
    containerWidth,
    sizing,
    defaults,
  );
  return columnRules.map((rule) => ({
    accessorKey: rule.id,
    header: rule.header,
    size: defaults[rule.id] ?? rule.min,
    minSize: rule.min,
    maxSize:
      rule.id === "slot"
        ? slotMax
        : rule.id === "candidateItem"
          ? Math.min(rule.max, pinnedBudget - slotWidth)
          : rule.id === "source"
            ? Math.max(rule.max, visibleSizing.source)
            : rule.max,
  }));
}

/** Keep authored widths intact; only the final, unpinned column absorbs unused
 * viewport space. The returned map is presentation state, never preferences. */
export function equipmentVisibleColumnSizing(
  containerWidth: number,
  sizing: ColumnSizingState,
  defaults: ColumnSizingState,
): ColumnSizingState {
  const pinnedBudget = Math.min(
    384,
    Math.max(184, Math.floor(containerWidth * 0.6)),
  );
  const visible: ColumnSizingState = {};
  for (const rule of columnRules) {
    const maximum =
      rule.id === "slot"
        ? Math.min(rule.max, pinnedBudget - 112)
        : rule.id === "candidateItem"
          ? Math.min(rule.max, pinnedBudget - visible.slot)
          : rule.max;
    visible[rule.id] = Math.max(
      rule.min,
      Math.min(maximum, sizing[rule.id] ?? defaults[rule.id] ?? rule.min),
    );
  }
  const total = Object.values(visible).reduce((sum, value) => sum + value, 0);
  visible.source += Math.max(0, containerWidth - total);
  return visible;
}

/** TanStack updaters operate on visible widths so dragging starts at the actual
 * boundary. Persist only edited columns, excluding untouched viewport fill. */
export function equipmentSizingPreferencesAfterChange(
  preferences: ColumnSizingState,
  previousVisible: ColumnSizingState,
  nextVisible: ColumnSizingState,
): ColumnSizingState {
  const next = { ...preferences };
  for (const rule of columnRules) {
    if (!Object.prototype.hasOwnProperty.call(nextVisible, rule.id))
      delete next[rule.id];
    else if (nextVisible[rule.id] !== previousVisible[rule.id])
      next[rule.id] = nextVisible[rule.id];
  }
  return sanitizeEquipmentColumnSizing(next);
}

export function keyboardColumnSize(
  key: string,
  current: number,
  minimum: number,
  maximum: number,
  initial: number,
  accelerated = false,
): number | undefined {
  const step = accelerated ? 24 : 8;
  const next =
    key === "ArrowLeft"
      ? current - step
      : key === "ArrowRight"
        ? current + step
        : key === "Home"
          ? minimum
          : key === "End"
            ? maximum
            : key === "Enter"
              ? initial
              : undefined;
  return next === undefined
    ? undefined
    : Math.max(minimum, Math.min(maximum, next));
}
