"""Verify the real JSONL trainer protocol entirely offline.

Only state, preference and disable/disconnect commands are
allowed. This test never launches, connects to or injects into a game. Fixture
executables/DLLs contain marker text and are only used for path validation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import time

from smoke import Bridge, ROOT


class OfflineBridge(Bridge):
    ALLOWED = {
        "state", "demo", "optimize", "cancel", "trainerState",
        "trainerSelectExecutable", "trainerSetMovementMultiplier",
        "trainerSetLifeMode", "trainerToggleOverlay",
        "trainerSetFeature", "trainerDisableAll", "trainerDisconnect",
        "trainerUnknown",
        "trainerSetAutoConnect",
    }

    def request(self, command: str, payload: dict | None = None, *, ok=True):
        assert command in self.ALLOWED, f"Offline test forbids {command}"
        return super().request(command, payload, ok=ok)


def all_off(state: dict):
    assert not state["connected"] and not state["connecting"] and not state["available"]
    assert state["pid"] is None and state["architecture"] is None
    assert all(not feature[key] for feature in state["features"] for key in ("requested", "applied", "pending"))
    assert all(state[key] is None for key in ("health", "maxHealth", "mana", "maxMana"))


def fingerprint(state: dict):
    return json.dumps({key: state[key] for key in (
        "selectedCharacterId", "workspace", "goalIndex", "equipment", "panel", "results",
    )}, ensure_ascii=False, sort_keys=True)


def assert_same_file(actual: str | Path | None, expected: Path):
    # Windows TEMP can contain an 8.3 alias (RUNNER~1) while .NET returns
    # its expanded name. Compare filesystem identity, not path spelling.
    assert actual is not None and str(actual), "Missing configured executable"
    assert Path(actual).samefile(expected), f"Configured executable is a different file: {actual!s} != {expected!s}"


def verify_path_identity_comparison(expected: Path, different: Path):
    assert_same_file(expected.resolve(strict=True), expected)
    assert_same_file(expected.parent / ".." / expected.parent.name / expected.name, expected)
    # Fixtures intentionally have identical bytes. A content match or a loose
    # basename comparison must never hide selection of another actual file.
    try:
        assert_same_file(different, expected)
    except AssertionError:
        pass
    else:
        raise AssertionError("Path identity assertion accepted a different file")


def exercise(executable: Path):
    with tempfile.TemporaryDirectory(prefix="grim-dawn-trainer-offline-") as temporary:
        data = Path(temporary)
        fixture = data / "Game folder 中文" / "x64"
        fixture.mkdir(parents=True)
        files = [fixture / name for name in ("Grim Dawn.exe", "Game.dll", "Engine.dll")]
        for file in files:
            file.write_bytes(b"OFFLINE PATH-VALIDATION FIXTURE: NEVER EXECUTE")
        other_executable = data / "Another game folder" / "Grim Dawn.exe"
        other_executable.parent.mkdir()
        other_executable.write_bytes(files[0].read_bytes())
        verify_path_identity_comparison(files[0], other_executable)
        hashes = {file: hashlib.sha256(file.read_bytes()).hexdigest() for file in [*files, other_executable]}
        bridge = OfflineBridge(executable, data)
        try:
            first = bridge.request("trainerState")["state"]
            all_off(first)
            assert first["configuredExecutable"] is None, "Isolated storage must not import legacy paths"
            assert first["movementMultiplier"] == 2
            assert first["lifeMode"] == "infinite" and first["lifeTriggers"] == 0
            assert first["deathCount"] is None
            assert "存档死亡次数 +1" in first["features"][0]["description"]
            assert "立即回满生命" in first["features"][0]["description"]
            all_off(bridge.request("trainerSetLifeMode", {"value": "lock"})["state"])
            assert bridge.request("trainerSetLifeMode", {"value": "bad"}, ok=False)["state"]["lifeMode"] == "lock"
            all_off(bridge.request("trainerToggleOverlay", ok=False)["state"])
            assert first["autoConnect"]
            bridge.request("trainerSetAutoConnect", {"enabled": False})
            expected = ["life", "mana", "kill", "moveSpeed", "attackSpeed", "castSpeed", "cooldown", "autoHealth", "autoMana"]
            assert [feature["id"] for feature in first["features"]] == expected
            assert [feature["shortcut"] for feature in first["features"]] == [f"Ctrl+Alt+F{i}" for i in range(1, 10)]
            assert len(expected) == len({feature["id"] for feature in first["features"]})

            analysis = bridge.request("demo")["state"]
            before = fingerprint(analysis)
            changed = bridge.request("trainerSetMovementMultiplier", {"value": 16})["state"]
            all_off(changed)
            assert changed["movementMultiplier"] == 16
            assert changed["sessionId"] == first["sessionId"] and changed["revision"] > first["revision"]
            for value in (0, 3, 32, "4"):
                invalid = bridge.request("trainerSetMovementMultiplier", {"value": value}, ok=False)["state"]
                all_off(invalid)
                assert invalid["movementMultiplier"] == 16
            enabled = bridge.request("trainerSetFeature", {"id": "life", "enabled": True})["state"]
            all_off(enabled)
            assert enabled["features"][0]["enabled"]
            for payload in ({"id": "quest", "enabled": True}, {"id": "mana", "enabled": "yes"}):
                all_off(bridge.request("trainerSetFeature", payload, ok=False)["state"])
            all_off(bridge.request("trainerUnknown", ok=False)["state"])
            assert fingerprint(bridge.request("state")["state"]) == before, "Trainer errors must not replace/invalidate analysis"

            saved = bridge.request("trainerSelectExecutable", {"path": str(files[0])})["state"]
            assert_same_file(saved["configuredExecutable"], files[0])
            preference = data / "combat-settings.json"
            saved_bytes = preference.read_bytes()
            for path in ("Grim Dawn.exe", str(fixture / "Wrong.exe"), str(data / "missing" / "Grim Dawn.exe")):
                rejected = bridge.request("trainerSelectExecutable", {"path": path}, ok=False)["state"]
                assert_same_file(rejected["configuredExecutable"], files[0])
                assert preference.read_bytes() == saved_bytes

            # The optimizer's long task must not monopolize combat off/state.
            bridge.request("optimize")
            bridge.wait_state(lambda state: state.get("isBusy"))
            started = time.monotonic()
            for command in ("trainerDisableAll", "trainerDisconnect", "trainerDisableAll"):
                all_off(bridge.request(command)["state"])
            assert time.monotonic() - started < 5, "Combat shutdown commands waited for analysis"
            assert bridge.request("state")["state"]["isBusy"], "Combat commands must not cancel analysis"
            bridge.request("cancel")
            bridge.wait_state(lambda state: not state.get("isBusy"))
            assert bridge.state["selectedCharacterId"] == analysis["selectedCharacterId"]
            assert bridge.state["canOptimize"]
            bridge.request("trainerSetFeature", {"id": "mana", "enabled": True})
        finally:
            bridge.close()

        reopened = OfflineBridge(executable, data)
        try:
            state = reopened.request("trainerState")["state"]
            all_off(state)
            assert_same_file(state["configuredExecutable"], files[0])
            assert state["movementMultiplier"] == 16
            assert state["lifeMode"] == "lock"
            assert not state["autoConnect"]
            assert state["features"][1]["enabled"], "Offline choices must persist without requesting game effects"
            assert state["sessionId"] != first["sessionId"]
        finally:
            reopened.close()
        assert all(hashlib.sha256(file.read_bytes()).hexdigest() == digest for file, digest in hashes.items())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path)
    args = parser.parse_args()
    candidates = [ROOT / "src/GrimDawnOptimizer.Bridge/bin" / configuration / "net8.0-windows/win-x64/GrimDawnOptimizer.Bridge.exe" for configuration in ("Release", "Debug")]
    executable = args.executable or next((path for path in candidates if path.is_file()), None)
    if executable is None:
        parser.error("Build the Bridge project first or supply --executable")
    exercise(executable.resolve())
    print("PASS: offline trainer JSONL defaults, validation, isolated persistence/restart, event separation, analysis concurrency and EOF. No game launch/connect/injection.")


if __name__ == "__main__":
    main()
