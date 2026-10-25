---
version: alpha
name: Grim Dawn Optimizer
description: A macOS-inspired desktop equipment inspector for Chinese-speaking Grim Dawn players, implemented in Windows WPF.
colors:
  primary: "#007AFF"
  canvas: "#F5F5F7"
  surface: "#FFFFFF"
  sidebar: "#ECECEE"
  text: "#1D1D1F"
  secondary: "#63636A"
  border: "#DADADD"
  accent: "#007AFF"
  accent-soft: "#E1EEFF"
typography:
  body:
    fontFamily: "Microsoft YaHei UI"
    fontSize: "13px"
  section:
    fontFamily: "Microsoft YaHei UI"
    fontSize: "16px"
  page:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "25px"
  metric:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
    fontSize: "38px"
spacing:
  label-gap: "7px"
  section-gap: "20px"
  content-inset: "32px"
rounded:
  control: "7px"
  surface: "10px"
omitted:
  - section: components
    reason: WPF resources and native control templates are canonical; ownership and states are documented below.
---

# Grim Dawn Optimizer Design System

## Overview

The user explicitly requested a macOS application style and a simpler, more accurate workflow on 2026-10-25. This replaces the previous Windows-accent Fluent/Mica visual direction. The application remains native Windows WPF; it does not claim to run on macOS.

The reference is the restrained structure of Finder and macOS settings: a silver sidebar, clear white workspace, compact controls, hairline separators and system-blue selection. The audience is Chinese-reading Grim Dawn players comparing their saved character with candidate equipment. The main job is to read a character, see its automatically calculated panel DPS, and compare usable loadouts.

The signature is an exact panel DPS number paired with an equipment replacement list. No game splash art, gradients, marketing hero, purple status ribbons or dense calibration dashboard. The window's three colored controls live inside `TitleBar.Header` and perform real close, minimize and maximize/restore actions. The title bar retains native dragging and double-click maximize/restore behavior.

## Colors

Runtime ownership is model B: `Services/DesktopPalette.cs` owns the palette; `App.xaml` provides shared component styles and consumes semantic brushes. Views consume `App*Brush` resources, not copied color values. The WPF UI controls that remain in use receive semantic aliases from that same adapter.

| Document role | Runtime resource | Light | Dark |
|---|---|---|---|
| canvas | AppCanvasBrush | #F5F5F7 | #1E1E20 |
| surface | AppSurfaceBrush | #FFFFFF | #2A2A2D |
| sidebar | AppSidebarBrush | #ECECEE | #252527 |
| text | AppTextBrush | #1D1D1F | #F5F5F7 |
| secondary | AppSecondaryBrush | #63636A | #AEAEB4 |
| border | AppBorderBrush | #DADADD | #414145 |
| accent | AppAccentBrush | #007AFF | #0A84FF |
| accent-soft | AppAccentSoftBrush | #E1EEFF | #183A5B |

Warning and success use semantic amber/green plus text. High contrast maps surfaces, text, borders and selection to Windows system colors. Theme selection can follow the system or be explicitly light/dark. The task palette deliberately does not inherit an arbitrary Windows accent.

## Typography

Body is 13 DIP; labels 12, section titles 16, page titles 25. Primary DPS is 38 DIP, exact rounded integer with grouping, never a shortened `23.5k` as the main panel value. Secondary estimated combat values can abbreviate. Microsoft YaHei UI supplies Chinese body text; Segoe UI supplies numeric display metrics with Microsoft YaHei UI fallback; do not depend on unavailable Apple fonts. Chinese help wraps at 20 DIP line height. Shared text uses grayscale rendering and ideal formatting; the base TextBlock style inherits foreground so button text follows its control state. Use semibold for decision points and numbers, not every label.

## Layout

The window starts at 1380 × 900 DIP and supports 1100 × 720. A 204 DIP sidebar hosts three destinations: 角色, 配装分析, 方案对比. The title bar is 58 DIP; the footer owns compact progress/status. Content has 32 DIP leading inset. The normal workflow has no separate search-configuration page.

Sources presents detected paths with an optional directory editor and explicit character selection. Analysis keeps main skill, difficulty and optimization goal visible, shows a current-loadout preview, and puts temporary buffs and special combat scenarios behind disclosure. Start/cancel stay reachable. Results use a narrow candidate list and one scrollable detail pane with four equipment columns; full item names and acquisition locations wrap.

## Elevation & Depth

Use flat surfaces and 1 DIP borders. Depth comes from hierarchy, not nested shadows. No Mica wallpaper tint. The sidebar is visually continuous with the title-bar region. Banners do not consume permanent large areas; actionable calculation gaps have a concise visible summary and expandable detail.

## Shapes

Shared buttons and ComboBox surfaces use 7 DIP corners, panel surfaces 10, candidate selections 9. The shared ComboBox style sets a 36 DIP minimum height; a small chevron opens a bounded, scrollable menu. Expander headers are flat, with a chevron and no enclosing box or circular arrow. These controls remain native WPF controls with shared application templates. The 13 DIP window dots sit inside 26 × 30 DIP keyboard-accessible targets.

## Components

`App.xaml` owns `PrimaryButton`, `SecondaryButton`, `QuietButton`, `NavButton`, `PanelSurface`, `PageTitle`, `SectionTitle`, `FieldLabel`, `SecondaryText`, `MetricValue`, `Divider`, `PageScrollViewer` and the shared native WPF `ComboBox`, `ComboBoxItem` and `Expander` templates. `DisclosureHeader` is the shared Expander toggle template. No page-local replacement for a shared form control. The application templates own popup width, placement, scrolling structure and visual states; native WPF preserves selection, arrow navigation, Enter/Escape and Space behavior. WPF UI supplies the remaining themed scrollbar controls, with palette aliases applied globally; every page uses its canonical content scroller.

Buttons define hover, pressed, focus and disabled states. Long-running work disables mutable inputs and offers cancellation; it does not change button size. Navigation/selection motion respects `SystemParameters.ClientAreaAnimation`. Read-only source operations require no confirmation. Unknown skill states remain visible and cannot silently become a different attack.

## Do's and Don'ts

- Do show automatically calculated panel DPS immediately after import or an applicable state change.
- Do keep necessary skill, difficulty and temporary-state choices explicit when the save cannot establish them.
- Do separate panel coverage from estimated combat and survival limitations.
- Do mark source changes and outdated results; never keep a valid-looking number from the wrong character or main skill.
- Don't expose engine knobs, calibration anchors, Monte Carlo counts or result-count inputs in the normal workflow.
- Don't hide missing mechanics or claim universal accuracy from a successful set of observations.
- Don't add network behavior, game-save writes, fake traffic-light controls or custom Apple font dependencies.
