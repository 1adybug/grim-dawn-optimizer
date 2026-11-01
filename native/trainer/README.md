# Cairn Companion native bridge

Imported from the local Cairn Companion 0.6.0 project, with charge-aware automatic
health/mana potions added in 1.0.24. The shared-memory protocol remains v5.
This directory contains the native bridge, architecture-specific symbol mappings,
injector, and offline simulation tests.

Build both x86 and x64 helpers with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File src/GrimDawnOptimizer.Desktop/scripts/prepare-trainer-native.ps1
```

Append `-RunTests` to compile and run the native simulations. These tests use mock
game objects and do not discover, launch, attach to, or inject into a game process.
The build requires Visual Studio C++ tools and the Windows SDK. It uses the static
MSVC runtime and produces only the four current protocol v5 helper files under
`src/GrimDawnOptimizer.Desktop/src-tauri/binaries/trainer-native/`.

The .NET trainer service owns the shared memory protocol and the heartbeat. Its
game-version checks, single-player restrictions, and disable-on-disconnect behavior
must remain intact when changing this code. The DLL remains loaded until game exit;
an expired heartbeat makes its hooks pass-through. Do not unload active trampolines.

MinHook sources and their full license are in `vendor/minhook/` and the packaged
license copy is `LICENSES/MinHook-BSD.txt`.
