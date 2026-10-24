---
version: alpha
name: Grim Dawn Optimizer
description: Windows desktop equipment analysis with native Fluent controls and readable Chinese forms.
typography:
  body:
    fontFamily: "Segoe UI Variable Text, Microsoft YaHei UI"
    fontSize: "13px"
  section:
    fontFamily: "Segoe UI Variable Display, Microsoft YaHei UI"
    fontSize: "17px"
  page:
    fontFamily: "Segoe UI Variable Display, Microsoft YaHei UI"
    fontSize: "26px"
spacing:
  label-gap: "6px"
  section-gap: "20px"
omitted:
  - section: colors
    reason: Dynamic Windows accent and WPF UI semantic resources own light, dark, and high-contrast colors.
  - section: rounded
    reason: WPF UI controls own their themed corner geometry.
  - section: components
    reason: Existing WPF UI and application styles are canonical; component ownership is documented below.
---

# Grim Dawn Optimizer Design System

## Overview

The reference is a Windows analysis workbench: four workspaces move from source files to a build, a bounded search, and equipment comparisons. The audience is Chinese-reading Grim Dawn players using a Windows desktop alongside the game. Locale is simplified Chinese; no particular geographic market or Japanese workflow is assumed.

Keep the existing Fluent/Mica shell, compact forms, and numerical comparisons. The distinctive content is the equipped-item provenance and predicted changes, not decorative game artwork. Do not introduce a website hero, new palette, or new dashboard cards for a small feature.

Runtime ownership is model B: `src/GrimDawnOptimizer.App/App.xaml`, WPF UI 4.3 `ThemesDictionary`/`ControlsDictionary`, and `MainWindow.xaml` are canonical. This document records their current choices; it does not generate a competing theme. Behavior belongs in [UX-CONTRACT.md](UX-CONTRACT.md).

## Colors

Consume dynamic semantic resources: `TextFillColorSecondaryBrush` for help/labels, `SystemFillColorCriticalBrush` for input errors, and `DividerStrokeColorDefaultBrush` for separators. WPF UI owns accent, selection, focus, hover, disabled, light/dark, and high-contrast resources. Do not hardcode feature-specific replacements.

## Typography

`App.xaml` defines body 13 DIP, section titles 17 DIP semibold, page titles 26 DIP semibold, and field labels 12 DIP. Preserve Segoe UI Variable with Microsoft YaHei UI fallback for mixed Chinese/Latin text. Help wraps; numerical inputs retain their accessible labels.

## Layout

The desktop window starts at 1440 × 900 DIP with a 1100 × 700 minimum. Build and search use the same two-column form, 1 DIP divider, and 28 DIP internal side margins. `PageScrollViewer` owns vertical page scrolling; long forms remain reachable. The normal label gap is 6 DIP and section separation is 20 DIP. No mobile layout is promised.

## Elevation & Depth

Mica and native themed controls provide depth. Use separators and typography for form hierarchy; no new shadows or nested decorative surfaces.

## Shapes

Preserve WPF UI control corners and focus treatment. Dividers are 1 DIP. New controls use the existing native/themed primitives.

## Components

`ui:Button`, `ui:TextBox`, `ui:NumberBox`, native `CheckBox`, `ComboBox`, and `Expander` keep standard keyboard, checked, disabled, hover, and focus behavior. `SectionTitle`, `FieldLabel`, `SecondaryText`, and `PageScrollViewer` are the shared application styles. `ui:SymbolIcon` supplies icons; meaningful actions retain text.

Primary buttons run the requested operation; secondary buttons cancel or perform optional actions. Busy state disables editable controls, preserves button dimensions, and uses the existing progress ring and status region. No new animations are added. Errors use text as well as semantic color.

Search defaults to automatic calculation. Optional DPS inputs appear only after the user checks “使用 DPS 校准（可选）”. Optional attribute calibration stays independently available. Keep labels consistent across help, validation, results, and documentation.

## Do's and Don'ts

- Do reuse the Build workspace's form density and the shared theme resources.
- Do distinguish calculated values from optional manually calibrated values.
- Don't imply every game skill has been verified or retain calibration consent after changing the loaded build.
- Don't add network behavior, game-save writes, or a visual redesign to calculation work.
