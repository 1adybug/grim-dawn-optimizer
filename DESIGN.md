---
version: alpha
name: Grim Dawn 配装工坊
description: A compact macOS-style equipment and single-player combat workbench on Windows, using Tauri 2, React, and shadcn preset b6HGdE1wo.
colors:
  primary: "oklch(0.527 0.154 150.069)"
  canvas: "oklch(1 0 0)"
  surface: "oklch(1 0 0)"
  text: "oklch(0.145 0 0)"
  secondary: "oklch(0.556 0 0)"
  border: "oklch(0.922 0 0)"
  dark-canvas: "oklch(0.145 0 0)"
  dark-surface: "oklch(0.205 0 0)"
  dark-primary: "oklch(0.448 0.119 151.328)"
  dark-highlight: "oklch(0.723 0.219 149.579)"
typography:
  body:
    fontFamily: "Inter Variable, Noto Sans SC Variable, sans-serif"
    fontSize: "13px"
    lineHeight: "1.65"
spacing:
  content-inset: "24px"
  content-inset-narrow: "18px"
  content-inset-small: "16px"
  partition-gap: "0px"
  field-gap: "20px"
  field-gap-narrow: "16px"
rounded:
  base: "0.45rem"
omitted:
  - section: components
    reason: Preset-generated shared primitives and semantic CSS variables are canonical; ownership is documented below.
---

# Grim Dawn Optimizer Design System

## Overview

The current design adds a fourth 实战工具 workspace for single-player session effects with optional automatic connection while preserving the established layout. All section separators reach their partition edges, with no whitespace at either endpoint. Main content now spans the full available width and height without the former 1440 px cap or outer page padding; reading padding moves inside each partition. Keep the earlier [analysis-compact.png](artifacts/tauri-ui/screenshots/analysis-compact.png) top-bar reference, canvas background, text underline tabs, PNG brand and section-local actions without a duplicate page toolbar. That screenshot was found, but a directly check-outable Tauri commit was not; the Desktop directory remains untracked, so this is visual adaptation rather than source rollback. Preserve shadcn preset `b6HGdE1wo`, locally bundled Inter/Noto Sans SC, RadioGroup and the table improvements. The application still targets Windows. Earlier WPF history remains in [docs/legacy-wpf-design.md](docs/legacy-wpf-design.md) and [docs/legacy-wpf-ux.md](docs/legacy-wpf-ux.md).

The product is a compact, practical equipment and single-player combat workbench for Chinese-reading Grim Dawn players. Its visual thesis is a quiet canvas-colored header, green underline for the active text tab, direct section actions and clear equipment rows. The main job is to show the saved character's calculated panel DPS and make candidate replacements easy to inspect. The exact DPS value and replacement list carry the identity; ornamental graphics and promotional copy do not.

The content plan is four text tabs immediately following the brand, character/settings controls at the opposite end of that bar, and direct access to content sections below. Each active TabsContent retains an `sr-only` h1 for page semantics; there is no duplicate visible page-title toolbar. Keep labels and actions short, and put actions beside their relevant section headings. Put optional explanations behind disclosures, while leaving errors, invalid input and material calculation limitations discoverable. English eyebrows, subtitles and decorative avatars stay removed. Whitespace, aligned text and separators identify functional groups; rounded containers, shadows and background blocks do not group page content. The interaction thesis is clear hover/selection feedback, restrained disclosure/menu transitions and reduced-motion support.

## Colors

Runtime ownership is model B: [src/GrimDawnOptimizer.Desktop/src/styles.css](src/GrimDawnOptimizer.Desktop/src/styles.css) is canonical. Its `:root` and `.dark` variables feed Tailwind v4's `@theme inline` aliases, shared shadcn primitives and application classes. DESIGN.md mirrors values and intent; it does not generate a second palette. WPF `DesktopPalette.cs` is a legacy adapter only.

Preset identity is recorded in [components.json](src/GrimDawnOptimizer.Desktop/components.json): `radix-rhea`, neutral base, Lucide icons, `default-translucent` menu color and `subtle` menu accent. It was applied through the shadcn CLI with `--preset b6HGdE1wo --force --reinstall`.

| Document role | Runtime token | Light | Dark |
|---|---|---|---|
| canvas | `--background` | `oklch(1 0 0)` | `oklch(0.145 0 0)` |
| surface | `--card`, `--popover` | `oklch(1 0 0)` | `oklch(0.205 0 0)` |
| text | `--foreground` | `oklch(0.145 0 0)` | `oklch(0.985 0 0)` |
| secondary text | `--muted-foreground` | `oklch(0.556 0 0)` | `oklch(0.708 0 0)` |
| primary action | `--primary` | `oklch(0.527 0.154 150.069)` | `oklch(0.448 0.119 151.328)` |
| primary action text | `--primary-foreground` | `oklch(0.982 0.018 155.826)` | `oklch(0.982 0.018 155.826)` |
| text highlight | `--highlight` | `var(--primary)` | `var(--sidebar-primary)` = `oklch(0.723 0.219 149.579)` |
| border | `--border` | `oklch(0.922 0 0)` | `oklch(1 0 0 / 10%)` |

The darker primary is for filled controls; highlighted text in dark mode uses the lighter semantic highlight. `--header` follows the canvas; `--soft-accent` derives from primary and canvas. Preset `--card` tokens may remain for preset compatibility but do not authorize page Card containers. Table fixed cells use an opaque canvas-matched background so scrolled text cannot show through. Do not restore old blue or custom amber colors inside individual screens. Errors use destructive color plus text. Forced-colors mode maps the interface to system colors.

The restored top bar uses the same semantic canvas background as the workspace and a subtle bottom separator. The previous custom silver bar colors no longer define this composition. Keep all shadcn preset tokens: `--sidebar-primary` still supplies the dark text highlight even though there is no physical sidebar. Window caption colors and controls belong to Windows, with the native theme following the app's system/light/dark preference. Forced-colors mappings remain system-color based.

## Typography

The explicit font stack is `--font-inter: 'Inter Variable', 'Noto Sans SC Variable', sans-serif`, adapted through `--font-sans` for the interface. `@fontsource-variable/inter` and `@fontsource-variable/noto-sans-sc`, both declared at `^5.3.0`, are bundled locally. Inter supplies Latin/numeric glyphs and Noto Sans SC supplies Chinese glyphs; Microsoft YaHei is no longer the designated fallback. There is no runtime font download. Body text is 13 px at 1.65 line height; responsive heading and metric sizes are owned by `styles.css`.

Metrics use tabular numerals. Primary panel DPS preserves grouping and the backend's `floor(raw + 0.5)` formatting. Do not abbreviate it to `23.5k` or calculate it again in React. Estimated combat metrics can use the backend's abbreviated display. Use restrained weights and complete Chinese names. Important paths, items, warnings and acquisition instructions remain readable.

Center all table column headers. In the equipment comparison, center the compact slot, component-name and augment-name columns; keep populated candidate/current equipment names, attachment sources and equipment locations left-aligned. Every empty marker is a full-width centered block regardless of the column's populated-text alignment. In the single-item metric table, keep metric row labels left-aligned and center current, predicted, change, measured-input and deviation values. Apply alignment to the actual cell button/input, with symmetric header padding; preserve sizing, truncation and full-text access.

## Layout

The native window starts at 1440 × 940 and permits 960 × 680. Tauri enables system decorations: Windows owns the title bar and its upper-right minimize, maximize/restore and close buttons, as well as caption dragging, double-click, system menu and supported snap behavior. Do not draw macOS traffic-light controls or duplicate caption actions inside React. The browser preview has no app-owned window controls; the 62 px application navigation bar remains inside the native client area.

Use a 62 px canvas-colored top navigation bar. Place the brand at the left and the text-only 角色库 / 配装工坊 / 方案对比 / 实战工具 tabs directly after the brand, without navigation icons. The brand icon no longer sits in a white background chip. The analysis character and settings share a group at the right; hide the analysis-character chip in 实战工具 so it cannot be mistaken for the active game character. Reuse shadcn Tabs with `TabsList variant="line"`; the active tab has an underline, not a raised white segment or gray group capsule. The full 52 px contextual-toolbar row stays removed. Header/footer content keeps 24 px horizontal padding, reducing to 18 px at ≤760 px and 16 px at ≤520 px. Main content has no outer padding or maximum-width cap; the same responsive reading padding belongs inside its sections. Do not add a new visible page title. All four tabs remain accessible on narrow layouts without introducing a sidebar.

Workspace tab labels are plain names: 方案对比 never appends a result-count badge or count to its visible or accessible label. Candidate counts belong in the result content.

The upper-left brand displays and announces `Grim Dawn Optimizer`; this naming adjustment is scoped to that brand control. Brand and About reuse the existing armor PNG at `src/GrimDawnOptimizer.Desktop/src-tauri/icons/icon.png`, not an SVG replacement. Ordinary action icons continue to use Lucide. Candidate choices are compact 40 px single-line rows with the candidate title and panel DPS; show the DPS label once in the list header. Remove repeated change summaries, per-row DPS labels and check icons. The selected result summary retains the replacement count, and each candidate's accessible name includes its title, panel DPS and replacement-slot count.

The main/TabsContent/page/layout chain fills the available workspace height. The page grids use zero gaps and stretch their columns; horizontal boundaries reach the column edges and vertical boundaries run from the workspace top to its bottom. Reading padding is applied to section interiors, never to an ancestor that would shorten its separator. Inner form/control spacing can remain 20/16 px and section headers/bodies keep their own compact rhythm. Change separator orientation when columns stack. The equipment table fills spare container width by expanding only its final equipment-location column for rendering; other base widths, pin offsets and saved preferences remain authoritative. Narrow containers retain horizontal scrolling. Source selectors and adjacent icon buttons use one shared control-height variant.

- **角色库**: a full-height vertical line separates source and character columns. 检测目录 sits beside 数据源; read-note and import explanations stay inside the source column so they do not interrupt the vertical boundary. Character rows remain compact. Empty states give one concrete next action without enclosing Cards.
- **配装工坊**: main-skill DPS, default-attack DPS and health form one metrics band above equipment on the left; their boundaries and disclosures reach the column edges. The analysis-condition divider spans the full workspace height. 保存配置 sits beside 分析条件; it saves analysis settings without requiring candidate results. The 已装备物品 heading groups slot count and 刷新角色. Interior reading padding keeps controls off the edges.
- **方案对比**: the candidate divider is full-height and each 40 px candidate row separator spans its column. At widths ≥1200 px, one summary grid places candidate identity, three metrics and 调整条件 side by side. Below 1200 px, identity/action share the first row and the three metrics form the next row. Keep validity, replacement count, confidence and all secondary metric text accessible. Result metrics use 22 px values (20 px at ≤520 px) and 10–12 px vertical insets. These compact rules are scoped to Results; Build metrics retain their established sizing.
- **实战工具**: a compact connection/path/action region precedes one ungrouped grid of nine features in their existing order. Remove the three category headings and wrappers. Columns adapt to available width with `repeat(auto-fit, minmax(min(100%, 280px), 1fr))`, rather than a fixed count or category layout. Keep zero grid gaps and separators reaching both workspace edges, including incomplete final rows; forced-colors mode uses a system-colored outline when decorative shadows are suppressed; keep the same internal 24/18/16 px reading insets and shared canvas. Use nine labeled Switch controls, the existing RadioGroup for movement 2X/4X/8X/16X, and shared buttons for path selection, launch, connect/disconnect and disable-all. Keep shortcuts and pending/acknowledged states readable. Optional explanations use a focusable Tooltip trigger that can open a full-text Dialog; errors use the shared Alert pattern. Do not import the source tool's glass panels, palette, ornaments or independent window chrome.

Combat connection state belongs to that workspace and remains independent of analysis progress, source selection and result state. All effects start off and no page navigation initiates a game connection. The analysis character is hidden on this page because the combat session acts on the game's current single-player character. Requested effects and acknowledged effects must remain distinguishable without relying on color. Multiplayer, missing character and stale heartbeat clear requests; returning to a ready game never silently restores them. Disable-all and disconnect remain reachable while analysis runs or the game is temporarily unavailable. The full lifecycle contract is in UX-CONTRACT.md.

Lock life reuses the native three-second notice and its existing typography, color, placement and input-free behavior. Show “你已经死亡了 XXX 次” using the active character's native death total. The first lethal damage packet in a game update restores full health immediately and increments the saved statistic through the game's save request. Desktop feature help and the native lock choice describe the refill and death count. Switching characters clears the previous notice. Protocol and verification details live in [docs/lock-life-deaths.md](docs/lock-life-deaths.md).

Use one 40 px comparison toolbar, sharing `--comparison-row-height` with candidate items, table headers and table rows: heading plus visible slot count at the left, changed-only control and reset layout at the right. `EquipmentComparisonTable.renderToolbar` lets App compose this row while the table retains its reset handler. Remove the duplicate tools row, its extra top gap and the visible fixed-column explanation; keep keyboard/pinning instructions in sr-only help. Keep this toolbar on one line; at ≤520 px the reset button may omit its visible text while retaining its accessible name and title. Summary, toolbar, table viewport and disclosure boundaries still meet their partition edges. A single noninteractive overlay draws the fixed-column boundary continuously through the table header/body and follows the actual pinned width, replacing per-cell shadows.

At 960 px and below the analysis configuration moves above the main content to protect usable equipment space; it remains on the right on wider windows. Narrow top-bar adaptation retains all four text tabs/settings, while section actions wrap without hiding titles or controls. Tables own horizontal overflow; forms retain natural vertical scrolling. All 14 equipment slots and at most ten candidates are deliberately rendered without pagination. TanStack owns column sizing and pinning; pinned offsets update with widths, and opaque pinned-cell backgrounds/stacking keep headers and data readable while scrolling. Drag handles have visible focus and a keyboard resizing equivalent. Single-line clipping must offer the full item/source text by click and keyboard, not hover alone. Do not impose table-only viewport constraints on forms.

Column defaults are content-aware estimates, using column labels and all candidates' rows, with separate Chinese/Latin character widths and usable minimum/maximum bounds. Use the stable unfiltered candidate dataset for estimates so switching candidate or toggling changed-only does not make columns jump. User drag/keyboard widths override estimates and persist locally in a version 1 layout preference across reloads and application restarts. The fixed-column budget is 60% of the table container, bounded to 184–384 px; narrow-window constraints affect rendered sizes without destroying the stored preference. Double-clicking a column divider or pressing Enter on it clears that column's override and restores its content-estimated default; the reset-layout action clears all overrides. Defaults are estimates, not a guarantee that every string fits; full-text access remains available.

The final equipment-location column has a 400 px prediction ceiling and 4000 px explicit-resize ceiling so a wide rendered fill can be dragged from its visible boundary. Filler is separate from saved widths; commit only changed sizing keys, and a zero-movement drag must not create a preference. The continuous fixed-boundary overlay uses a 6 px gradient only during overflow; forced colors hide the gradient and retain a system-colored solid line.

Desktop Results uses two shared grid rows (`auto 1fr`): the candidate heading shares the summary’s natural-height first row, while the candidate list and comparison detail start together in the second. The candidate section spans both rows with CSS subgrid, retaining its semantic grouping. Candidate 1 aligns with the equipment-list toolbar, candidate 2 with the table header, and each subsequent candidate with the next rendered equipment row. Use the shared 40 px border-box rhythm rather than a guessed summary spacer; candidate buttons have no active translation. The toolbar owns the boundary above the table, so the table container contributes no extra top pixel, and the continuous pinned boundary begins at table top. At ≤760 px use three stacked grid rows and a horizontal candidate strip; items remain 40 px high, but cross-column alignment no longer applies.

Floating progress is centered in the full-width workspace. Remove the old sidebar-width offsets from both its position and maximum width, as well as from the shell grid. Keep progress/cancel reachable without covering the final form control.

## Elevation & Depth

All page sections use the shared workspace background, with whitespace, row alignment and separators providing structure. Do not wrap source selection, metrics, analysis conditions, equipment, coverage, result details or empty states in Cards, rounded panels, shadows or contrasting background blocks. Interactive hover/selection states remain visible. Menus and dialogs inherit the preset's subtle treatment, with elevation reserved for floating progress and modal surfaces.

## Shapes

The preset base is `--radius: 0.45rem`. Shared control radii flow through `@theme inline` and shadcn primitives. Avoid independent page-local button or select geometry. Icon-only controls use actual buttons with localized accessible names. Windows owns native caption-button geometry, icons, labels and interaction feedback. Visual simplicity must not reduce target size or focus visibility.

Character rows keep stable geometry when pressed: `.character-row` locally disables the preset button's active translation while retaining hover/selected feedback. The character list keeps its 400 px maximum height and real vertical scrolling; do not remove overflow handling to conceal a transient scrollbar.

SelectField popups use Radix popper positioning, start alignment and 8 px collision padding. Their minimum width equals the trigger; `width: max-content` expands to the longest item, bounded by Radix's available width and `100vw - 16px`. Short-option menus therefore stay trigger-width, while long-option menus grow only as far as the viewport permits. Item text can wrap, including unbroken paths, once that maximum is reached. Keep Escape dismissal and the existing selection/focus behavior.

Directory and executable paths use the shared PathInput: an editable Input and 36 px 浏览 button, followed by an explicit 使用 action only while the draft differs from the saved value. Enter also applies; blur never does. Browsing always opens the system folder/file picker. Confirming the picker applies that explicit selection; cancelling preserves the draft. Sources keeps only the latest game-directory path and the latest save-directory path, with no history dropdown or removal control. A successfully accepted path replaces that kind's saved value; an invalid submission or cancellation does not. Rejected submissions keep the exact typed draft, an associated inline error and input focus. Trim outer whitespace and one matching outer double-quote pair when submitting or suggesting an initial picker location; never interpret shell syntax. Local busy state prevents duplicate pick/commit actions without altering unrelated analysis or combat state. The development picker enters per-monitor v2 DPI awareness on its dedicated thread before COM/window creation and restores that thread after cleanup, avoiding bitmap-scaled dialog text. Tauri retains its existing native DPI-aware dialog behavior; no application font or system scaling override is introduced.

## Components

| Role | Canonical owner | Runtime mapping / consumers |
|---|---|---|
| Functional regions | Desktop `App.tsx`, shared section/disclosure patterns and `src/styles.css` | Full-width/full-height semantic partitions, zero grid gaps, edge-reaching separators and interior reading padding; no Card wrappers |
| Equipment comparison | Desktop `src/components/equipment-comparison-table.tsx`, `equipment-table-model.ts` and scoped CSS; shadcn `ui/table.tsx` primitives | TanStack sizing/pinning, content-estimated defaults and versioned overrides, render-only last-column fill, continuous fixed-boundary overlay, shared renderToolbar, one overflow owner and full-text Dialog |
| Diagnostic metrics table | Desktop `SwapComparison` and shadcn `src/components/ui/table.tsx` | Existing compact numeric/input table, distinct from resizable equipment comparisons |
| Single choice | Desktop `src/components/ui/radio-group.tsx` | RadioGroup for analysis goals, appearance and combat movement multiplier, with associated Label |
| Workspace navigation | Desktop `src/components/ui/tabs.tsx` and `App.tsx` | Controlled Tabs value follows workspace state; TabsList variant=line/Triggers provide underline text tabs and Radix roving focus; active TabsContent owns the associated panel |
| Shared controls | Desktop `src/components/ui/*` | Preset Button, Input, Select, Label, Dialog, Accordion, Switch, Badge, Progress, Tooltip and semantic Tailwind utilities; Button owns character/candidate selection and regular actions |
| Shared patterns | Desktop `src/components/shared.tsx` | SelectField, NumericField and ActionButton reuse controls; Disclosure uses Accordion; Notice uses Alert; EmptyState uses Empty |
| Path input / picker | Desktop `src/components/path-input.tsx`, scoped CSS and `src/lib/path-picker.ts` | Shared editable source/executable paths, explicit apply, native folder/file selection, one latest source path per kind and inline errors; Tauri dialog plugin or local development pickPath service |
| Theme / surface tokens | Desktop `src/styles.css` | `:root` / `.dark` → `@theme inline` → shared controls and application classes |
| Typography | Desktop `src/styles.css` | Locally bundled Inter/Noto Sans SC → `--font-inter` → `--font-sans` → body and shared heading/metric selectors |
| Layout rhythm | Desktop `src/styles.css` | 62 px underline-tab top bar, section-local actions, uncapped main without outer padding, zero partition gaps, internal 24/18/16 px padding and full-workspace progress centering |
| Scrollbar | Desktop `src/styles.css` global base layer | Standards properties, WebKit thumb/hover/active/track, forced-colors fallback |
| Application state | Desktop `src/lib/bridge.ts` and `App.tsx` | Tauri commands or development-only local transport; ordered .NET analysis snapshots |
| Application updates | Desktop `src/components/app-updates.tsx`, `src/lib/use-app-updater.ts`, native `src-tauri/src/updates.rs` | Existing settings Dialog; shared Switch/Label/ActionButton/Progress/Notice; version, checking/download/error/retry states and native signed installer updates; no new palette |
| Combat tools | Desktop `src/components/combat-tools.tsx`, `combat-tools.css`, `src/lib/use-trainer.ts` and `trainer-types.ts` | Fourth workspace; one responsive ungrouped feature grid; independent connection/capability state; shared Switch, RadioGroup, Button, Tooltip/Dialog and Alert; no new page surface tokens |
| Game popup and life notice | Native `native/trainer/overlay.cpp`, Dear ImGui Win32 + DX9/DX11 backends | Fullscreen in-frame variant; same feature order, life/movement choices and persistent controller. Green accent, quiet dark surface and local Windows Chinese font. Notices never capture input; no separate OS window. |

Shared controls own hover, focus, pressed, disabled and busy states. Reuse Radix keyboard/focus behavior for selects, tabs, radio groups, dialogs and disclosures. Workspace tabs use one roving tab stop, horizontal arrow keys and Home/End, selected-state semantics and tab-to-panel association; the controlled selection continues through the existing workspace navigation handler. Character/candidate selection remains a semantic Button. Preserve input values on failure and keep field errors nearby. Native Windows folder/file selection and the actual native-window control buttons are deliberate platform-owned exceptions.

This migration intentionally replaces WPF styling and composition while reusing .NET workflow and calculation code. Analysis numerical behavior, read-only source protection and missing-skill semantics remain governed by [UX-CONTRACT.md](UX-CONTRACT.md) and existing tests.

## Do's and Don'ts

- Show current metrics and useful actions before explanatory text.
- Keep skill, difficulty and temporary-state choices explicit when the save cannot establish them.
- Distinguish panel values, combat estimates and survival limits without repeating the same explanation in multiple places.
- Keep acquisition locations, invalid input, stale sources and unsupported mechanics accessible.
- Reuse the selected preset and shared primitives throughout all workspaces; separate page sections with lines.
- Keep metrics together and align titles/actions to interior reading insets; let structural separators reach every partition edge.
- Preserve fixed table columns during horizontal scrolling and provide full clipped text through keyboard/click access.
- Remember user column widths and keep content-estimated defaults stable across candidate/filter changes; double-click resets only the selected column.
- Do not add page Cards, rounded/background panel substitutes, marketing headlines, ornamental avatars, decorative metrics or repeated context rows.
- Do not claim global optimality, directly overwrite game-save files, add analytics telemetry or use fake window controls. The explicitly requested lock-life death statistic uses the game's own increment and save request.

Update and combat persistence (2026-11-01): keep the existing geometry and tokens. The combat connection region adds a labeled auto-connect Switch and short state-dependent hint. Feature Switch values represent remembered choices, with 已记住，等待单人角色 separate from pending/applied runtime acknowledgment. First-use defaults are off/2X; subsequent choices survive restart. The settings Dialog adds 应用更新 beneath appearance, with two switches, version/status, manual check/install actions, progress and inline retry errors. Its existing bounded scrolling owns overflow at narrow/short viewports. Runtime behavior and release-channel ownership are in UX-CONTRACT.md and docs/github-actions.md.
