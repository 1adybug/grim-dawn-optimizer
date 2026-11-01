# Dear ImGui

Official upstream: https://github.com/ocornut/imgui

Version: v1.91.9b, commit `f5befd2d29e66809cd1110a152e375a7f1981f06`.
Core and Win32/DX9/DX11 backend files are copied unchanged from that commit.
The build defines `IMGUI_IMPL_WIN32_DISABLE_GAMEPAD` because this popup uses
mouse/keyboard input. MIT license: LICENSE.txt; packaged copy:
LICENSES/Dear-ImGui-MIT.txt. Windows Chinese fonts are read from the system and
are not redistributed.
