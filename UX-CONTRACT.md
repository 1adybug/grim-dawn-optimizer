# Desktop workflow contract

The product reads Grim Dawn files and searches equipment combinations. The source/save import is read-only. [DESIGN.md](DESIGN.md) records visual intent and existing runtime token ownership.

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Form | MainViewModel validation with WPF UI fields and native CheckBox | This contract; App.xaml field styles | Automatic calculation; explicitly enabled DPS calibration; independent optional attribute calibration | OptionalDpsCalibrationViewModelTests; OptimizerTests; native desktop smoke test |
| Select/Listbox | WPF ComboBox and its WPF UI theme | App.xaml ControlsDictionary | Platform-owned popup/keyboard behavior | Existing Build/Sources screens; desktop keyboard check |
| Scrollbar | WPF UI ScrollViewer theme plus App.xaml PageScrollViewer geometry | App.xaml | Page vertical scrolling; result list's own scrolling | Long form at minimum window size; desktop inspection |

## Calculation and calibration

- Automatic calculation is the default. Missing or stale DPS anchor values must not block it or alter the calculated result.
- “使用 DPS 校准（可选）” explicitly enables manual DPS calibration. The main-skill input is then required; reliable weapon-damage skills also require the default-attack DPS input. Both accept finite values greater than zero and at most 10,000,000.
- Disabling DPS calibration hides its fields and removes their validation requirement. Existing optional character-attribute calibration remains independent.
- Loading/reloading a character, selecting a different main skill, or replacing the profile clears DPS inputs and turns DPS calibration off. Neither the switch nor DPS inputs are persisted in profiles.
- Changing calculation inputs clears stale results. An invalid visible calibration field shows a correction message and disables the run action. Hidden DPS values must never produce an error.
- The main-skill panel DPS comparison displays `floor(raw + 0.5)`. Its change is the displayed prediction minus the displayed baseline; measured deviation compares the same displayed integers. Other attribute precision and all internal simulation values stay unchanged. Abbreviated panel-DPS summaries expose the full integer in a tooltip and accessibility help text.

## Operation states

“运行多策略优化” starts one search. While busy, calibration controls are disabled and the existing progress/status UI reports work; “取消” cancels it. Success opens the results workspace. Errors keep the current workspace and expose a status message; retry remains available. No network, session, financial, destructive-game-save, or upload workflow is involved.

Keyboard users use native Tab and Space behavior for the checkbox and the existing field controls. The checkbox and inputs have Chinese accessible names. Collapsed inputs leave the keyboard tab order. At the minimum supported window size, the page remains scrollable. The same themes, typography, and busy conventions apply as on the Build workspace.

## Verification ownership

Core and ViewModel regressions cover default calculation, explicit opt-in, validation, switching/reloading, and profile persistence. The task's integration owner runs `dotnet test`/publish and inspects the native WPF checkbox off/on, error, busy, keyboard, minimum-window, and theme states. The premium static auditor only recognizes web source extensions, so its output cannot establish WPF rendering or accessibility correctness.
