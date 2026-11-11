"""Exercise the real NSIS hooks in an isolated directory, without the game."""
import ctypes
import os
from pathlib import Path
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
COMPILER = Path(os.environ["LOCALAPPDATA"]) / "tauri/NSIS/makensis.exe"
HOOKS = ROOT / "src/GrimDawnOptimizer.Desktop/src-tauri/installer-hooks.nsh"


def main():
    with tempfile.TemporaryDirectory(prefix="gdo-installer-hooks-") as temporary:
        root = Path(temporary)
        script = root / "harness.nsi"
        script.write_text(f'''Unicode true
!include MUI2.nsh
!include "{HOOKS}"
Name "Trainer hook test"
OutFile "hook-test.exe"
RequestExecutionLevel user
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"
Section
  StrCpy $0 "preserved0"
  StrCpy $1 "preserved1"
  StrCpy $2 "preserved2"
  !insertmacro NSIS_HOOK_PREINSTALL
  !insertmacro NSIS_HOOK_PREUNINSTALL
  ${{If}} $0 != "preserved0"
  ${{OrIf}} $1 != "preserved1"
  ${{OrIf}} $2 != "preserved2"
    SetErrorLevel 3
    Abort
  ${{EndIf}}
  FileOpen $0 "$INSTDIR\\passed" w
  FileClose $0
SectionEnd
''', encoding="utf-8-sig")
        subprocess.run([str(COMPILER), "/WX", "/V2", str(script)], check=True, timeout=30)
        installer = root / "hook-test.exe"
        installation = root / "installation"
        installation.mkdir()
        passed = installation / "passed"

        def run(expected_success):
            passed.unlink(missing_ok=True)
            result = subprocess.run([str(installer), "/S", f"/D={installation}"], timeout=15)
            assert (result.returncode == 0) == expected_success, result.returncode
            assert passed.exists() == expected_success

        # A fresh install needs no trainer directory.
        run(True)
        native = installation / "trainer-native"
        native.mkdir()
        for name in ("CairnBridge.v7.x86.dll", "CairnBridge.v7.x64.dll", "CairnBridge.v6.x86.dll"):
            (native / name).write_bytes(b"intact existing component")
        before = {path: path.read_bytes() for path in native.iterdir()}
        run(True)
        assert all(path.read_bytes() == data for path, data in before.items())

        # Load a harmless Windows DLL to reproduce the executable-image lock.
        dll = native / "CairnBridge.v7.x64.dll"
        dll.write_bytes((Path(os.environ["WINDIR"]) / "System32/version.dll").read_bytes())
        original = dll.read_bytes()
        module = ctypes.WinDLL(str(dll))
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.FreeLibrary.argtypes = [ctypes.c_void_p]
        kernel.FreeLibrary.restype = ctypes.c_int
        try:
            run(False)
            assert dll.read_bytes() == original
        finally:
            assert kernel.FreeLibrary(module._handle)
        run(True)
        assert dll.read_bytes() == original
        print("PASS: fresh install, intact unlocked files, loaded-DLL rejection, and retry after release.")


if __name__ == "__main__":
    main()
