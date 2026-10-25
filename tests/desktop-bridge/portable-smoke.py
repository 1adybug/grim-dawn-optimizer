"""Verify the actual single-file portable EXE without opening any native UI.

Extraction is exercised in a temporary Unicode path. Every extracted file is
compared with the build staging directory before exercising the real engine's
calculation and offline trainer protocols. No game is launched or connected.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
NO_WINDOW = subprocess.CREATE_NO_WINDOW


def fingerprint(directory: Path) -> dict[str, str]:
    return {
        path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in directory.rglob("*") if path.is_file()
    }


def run(arguments: list[str], timeout: int = 120):
    result = subprocess.run(arguments, creationflags=NO_WINDOW, timeout=timeout,
                            capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        raise AssertionError(f"Command failed ({result.returncode}): {arguments}\n{result.stdout}\n{result.stderr}")
    return result


def main():
    version = json.loads((ROOT / "src/GrimDawnOptimizer.Desktop/package.json").read_text(encoding="utf-8"))["version"]
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", type=Path,
                        default=ROOT / f"artifacts/GrimDawnOptimizer-{version}-win-x64.exe")
    parser.add_argument("--staging", type=Path, default=ROOT / "artifacts/tauri-win-x64")
    options = parser.parse_args()
    executable = options.executable.resolve()
    expected = fingerprint(options.staging)
    required = {
        "GrimDawnOptimizer.exe", "grim-dawn-engine.exe", "LICENSE", "THIRD-PARTY-NOTICES.md",
        "trainer-native/CairnBridge.v5.x86.dll", "trainer-native/CairnBridge.v5.x64.dll",
        "trainer-native/CairnInject.x86.exe", "trainer-native/CairnInject.x64.exe",
    }
    assert required <= expected.keys(), f"Missing staged runtime files: {required - expected.keys()}"
    assert any(name.startswith("LICENSES/") for name in expected), "Dependency licenses are missing"
    run([str(executable), "--verify-only"])
    with tempfile.TemporaryDirectory(prefix="grim-dawn-portable-") as temporary:
        invalid_cwd = Path(temporary) / "invalid target fixture"
        invalid_cwd.mkdir()
        marker = invalid_cwd / "keep.txt"
        marker.write_text("KEEP", encoding="utf-8")
        for invalid in ("", " ", ".", ".."):
            result = subprocess.run([str(executable), "--extract-only", invalid],
                                    cwd=invalid_cwd, creationflags=NO_WINDOW,
                                    capture_output=True, timeout=15)
            assert result.returncode != 0, f"Unsafe extraction argument accepted: {invalid!r}"
            assert marker.read_text(encoding="utf-8") == "KEEP", "Invalid target changed the working directory"
            assert set(Path(temporary).iterdir()) == {invalid_cwd}, "Invalid target created extraction files"
        output = Path(temporary) / "便携程序 smoke test"
        arguments = [str(executable), "--extract-only", str(output)]
        run(arguments)
        assert fingerprint(output) == expected, "Extracted payload differs from staged application"

        # Repeated launches must reuse intact files and repair corrupt cache data.
        engine = output / "grim-dawn-engine.exe"
        unchanged_time = engine.stat().st_mtime_ns
        run(arguments)
        assert engine.stat().st_mtime_ns == unchanged_time, "Intact payload was needlessly rewritten"
        engine.write_bytes(b"CORRUPT CACHE FIXTURE")
        run(arguments)
        assert fingerprint(output) == expected, "Corrupt payload was not repaired"

        # Two first launches to the same cache must serialize extraction safely.
        concurrent = Path(temporary) / "parallel extraction"
        commands = [str(executable), "--extract-only", str(concurrent)]
        processes = [subprocess.Popen(commands, creationflags=NO_WINDOW,
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE) for _ in range(2)]
        try:
            for process in processes:
                stdout, stderr = process.communicate(timeout=120)
                assert process.returncode == 0, (stdout, stderr)
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)
        assert fingerprint(concurrent) == expected, "Concurrent extraction produced an incomplete payload"

        for script in ("smoke.py", "trainer-smoke.py"):
            result = run([sys.executable, str(Path(__file__).with_name(script)),
                          "--executable", str(engine)])
            print(result.stdout.strip())
    print("PASS: portable EXE payload hashes, Unicode paths, reuse, repair, concurrent extraction and packaged engine protocols")


if __name__ == "__main__":
    main()
