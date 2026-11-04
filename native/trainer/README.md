# Cairn Companion native bridge

Imported from the local Cairn Companion 0.6.0 project, with charge-aware automatic
health/mana potions added in 1.0.24. The shared-memory protocol is now v7:
offset 40 selects infinite life (0) or death protection (1), and offset 44 is
an atomic trigger counter. The structure is 1136 bytes; offsets 720–743 contain
overlay toggle/status and ring counters, followed by 32 UI actions and an
independent emergency-off flag at 1128 and the native uint32 death total at 1132.
Old loaded bridges require a game restart; different versions must never install
hooks together in one process.
This directory contains the native bridge, architecture-specific symbol mappings,
injector, and offline simulation tests.

Build both x86 and x64 helpers with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File src/GrimDawnOptimizer.Desktop/scripts/prepare-trainer-native.ps1
```

Append `-RunTests` to compile and run the native simulations. These tests use mock
game objects and do not discover, launch, attach to, or inject into a game process.
The build requires Visual Studio C++ tools and the Windows SDK. It uses the static
MSVC runtime and produces the four current protocol v7 helper files under
`src/GrimDawnOptimizer.Desktop/src-tauri/binaries/trainer-native/`.

The .NET trainer service owns the shared memory protocol and the heartbeat. Its
game-version checks, single-player restrictions, and disable-on-disconnect behavior
must remain intact when changing this code. The DLL remains loaded until game exit;
an expired heartbeat makes its hooks pass-through. Do not unload active trampolines.

Each lock-life trigger calls the game's `PlayStats::IncrementDeaths` and
`Player::ForceSave`; infinite life and ordinary non-intercepted damage never count.
The notice shows the active character's total, including existing real deaths.
See `docs/lock-life-deaths.md` for the exact-build ABI and persistence limits.

MinHook sources and their full license are in `vendor/minhook/` and the packaged
license copy is `LICENSES/MinHook-BSD.txt`.

`overlay.cpp` draws the popup and life notice inside the DX9/DX11 framebuffer,
including exclusive fullscreen. Dear ImGui sources are pinned under
`vendor/imgui/`; their packaged MIT license is `LICENSES/Dear-ImGui-MIT.txt`.
Popup input is captured through Win32 and DirectInput; notification-only rendering
never captures input. Host actions use a bounded ring and the normal preference
validation/persistence path. Emergency off uses a separate flag, even if the ring
is full. The hooks pass through when the host heartbeat expires.

Append `-RunOverlayTests` for independent x86/x64 GPU tests in DX9/DX11 windowed
and exclusive-fullscreen modes. They create only their own test windows/devices,
read back actual rendered pixels, and check input capture, Reset/ResizeBuffers,
notice expiry and heartbeat loss. They never open or inject into the game.

Append `-RunInputTests` for hidden-window input regressions. They call the actual
window procedure and DirectInput/Win32 detours with deterministic original-entry
stubs, covering held-button close, F10, Escape, focus loss, cursor restoration,
unchanged post-close mouse clicks/data, error propagation and notice-only input.
They also cover Ctrl/Alt held across popup opening, releases in both Win32 and
buffered DirectInput, all three close paths, popup-owned repeats after closing,
PEEK/count-only reads and legacy DX3 records. Input ownership follows each down:
game-owned releases pass through the popup; popup-owned repeats/releases remain
blocked until the physical release. Count-only reads retain game-owned releases
while draining popup events, so the game can consume them even after closing.
The fixed panel feeds ImGui mouse events directly without acquiring Windows mouse
capture. Every close clears queued/held input; existing game-owned capture remains
untouched. Focus loss never re-confines the desktop cursor.
