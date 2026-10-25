# Desktop workflow contract

The product is a read-only Grim Dawn equipment analyzer. The 2026-10-25 user request authorizes a macOS-inspired visual redesign and a simpler automatic workflow. [Archived DESIGN.md](legacy-wpf-design.md) owns this legacy visual direction; local database/save contracts and calculation regressions own numerical behavior. This document is retained as historical WPF evidence; the repository-root contracts govern the current Tauri interface.

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Form | MainViewModel and App.xaml shared controls | This contract; actual save/database rules | Automatic analysis; explicit temporary-state input; optional special scenario | Automatic workflow and skill/input tests; native UI inspection |
| Select/Listbox | Native WPF ComboBox/ComboBoxItem with App.xaml shared templates; native ListBox | App.xaml; native selection and keyboard behavior | Character, skill, difficulty, goal, appearance | Popup, arrow keys, Enter/Escape, narrow window |
| Disclosure | Native WPF Expander with App.xaml shared Expander and DisclosureHeader templates | App.xaml; native IsExpanded state and ToggleButton keyboard behavior | Temporary buffs, special scenarios, calculation details | Chevron/content agreement; keyboard Space; disabled inputs |
| Scrollbar | WPF UI global ScrollViewer plus App.xaml PageScrollViewer geometry | App.xaml and DesktopPalette semantic aliases | Page vertical scroll; bounded candidate list | Long content at minimum window size |
| Feedback | Shared window footer plus inline analysis/coverage messages | MainViewModel state | Import/progress/error/status; persistent calculation-gap summary | Failure, cancellation and stale-source tests; native UI |
| Table Selection | ResultsView read-only equipment table and candidate list | ResultViewModel | Bounded 14 equipment slots; selected candidate; optional single-item comparison | Full names, changed-slot identity and attachment regressions |

## Normal workflow

1. 角色 automatically discovers plausible installations and save directories. Reading remains an explicit local operation. Directory edits are optional; ambiguous characters are never silently treated as the active game character.
2. 配装分析 shows the current saved loadout's uncalibrated panel DPS immediately. The visible inputs are main skill, difficulty and output/survival goal. Default weapon attack is an actual selection. A missing saved attack remains visibly unresolved instead of being replaced with another learned skill.
3. 开始分析 uses automatic calculations, a 180-second budget, ten candidates and 4,000 samples. These settings are implementation defaults, not required user inputs. Hidden legacy calibration anchors cannot block or change normal analysis. Results open 方案对比; cancellation preserves only valid completed results if the optimizer supports them.
4. 刷新角色 reloads the saved state and preserves the explicitly selected character identity when present. It clears ephemeral buffs and calibration state. A changed source or failed load must not leave a prior character's results usable under a new source label.

The former Search enum is a compatibility route to analysis, not an extra navigation step. No result is called globally optimal; the interface says candidate/方案.

## Correctness and input state

- The default is no temporary buff. Explicit temporary choices describe the moment being compared, not combat uptime; candidate calculations recheck source and weapon eligibility. Choices are not persisted in profiles and clear on reload.
- Skill, goal, difficulty, buff, duration, target count and extra debuff changes invalidate previous results. Changing a main skill also rebuilds the automatic preview.
- An explicitly selected equipment-granted main skill resolves its source and level for each candidate. Missing sources and weapon-ineligible attacks are unavailable, not default attacks; such candidates are excluded with a reason.
- Source freshness checks are read-only. A disk save change triggers reload/review before analysis instead of quietly using a stale snapshot. The application cannot observe unsaved in-game changes and explains that limit.
- Manual calibration APIs can remain for legacy regression coverage, but normal UI analysis clears/ignores them. Optional measured values in the single-item comparison are diagnostic only and never fit the model.
- Displayed panel DPS is `floor(raw + 0.5)`. The primary number is fully visible. Estimated combat damage, target defenses and survival are labeled separately.
- Coverage distinguishes panel DPS, combat output and survival. A concise coverage summary is always visible. Detailed records and explanations are expandable. No-warning means no detected gap, not universal empirical accuracy.
- Equipment changes compare item identity and attachment/completion identity, not just the displayed item name.

## States, accessibility and persistence

Shared actions keep stable labels, geometry and keyboard behavior. Busy state disables mutable inputs, provides progress and cancellation, and prevents duplicate runs. Failure preserves a recoverable source-selection screen or explicitly stale state, with a readable reason. Empty character lists explain how to read or choose a directory. Native folder dialogs remain the canonical Windows picker.

Primary, secondary and quiet buttons share one template. Window controls live inside `TitleBar.Header`, perform actual window operations and have Chinese accessible names/tooltips; native title-bar dragging and double-click maximize/restore remain available. Native WPF ComboBox/ComboBoxItem use the application's shared rounded templates, retaining open-state, selection and keyboard behavior. Native WPF Expander uses a flat ToggleButton header with a chevron; Space toggles the same IsExpanded state that controls content visibility. The base TextBlock style inherits foreground, uses Microsoft YaHei UI with grayscale/ideal rendering, and permits Segoe UI display text with the Chinese fallback. Important values and names wrap; fixed footer summaries expose full text through accessibility and tooltip. Minimum window size is 1100 × 720; long content stays scrollable. Themes use the same semantic hierarchy; high contrast uses system colors. Reduced system animation disables transitions.

Only directory history, explicit appearance preference and local optimization profiles are persisted. Runtime buff state, hand-entered DPS and current result state are session-only. The application makes no network requests and never writes game saves.

## Verification

Run the solution's tests and Release publish, preserve the eight game-observed panel regression samples, then exercise the native application: detected source import, skill/default selection, current preview, start/cancel, results, changed source/reload, popup keyboard, narrow window and light/dark. The premium static audit only recognizes web-source patterns, so its output does not prove WPF rendering. Record native screenshots alongside the test results.


### Verification record · 2026-10-25

- Integration test suite: 395/395 passed. DESIGN.md lint: 0 warnings.
- Native window checks completed: reading the real character displayed main-skill panel DPS 23,478 and default-attack DPS 4,374; analysis cancellation; window maximize/restore; theme selection with End/Enter; the 1100 × 720 minimum window; Expander toggling with Space.
- The premium static auditor does not recognize WPF rendering or interaction behavior; its clean result is only a static check.
- Results verified with the real character: four candidates after 53,780 evaluations, the unchanged loadout at 23,478 DPS, selected-candidate changes, attachment-only changes, full 14-slot scrolling and the single-item comparison disclosure at minimum width. Selecting default attack updates the preview to 4,374 and invalidates previous results. Window close/minimize/restore were exercised. Screenshots are saved in `artifacts/ui-refresh-1025/screenshots/`.
