"""Exercise the actual JSONL child process without changing the user's settings.

Run after building the Bridge project:
  python tests/desktop-bridge/smoke.py
Use --complete to also wait for a full demo analysis and inspect result payloads.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time


ROOT = Path(__file__).resolve().parents[2]


class Bridge:
    def __init__(self, executable: Path, data: Path):
        self.messages: queue.Queue[dict] = queue.Queue()
        self.state: dict = {}
        self.trainer_state: dict = {}
        self.request_kinds: dict[str, bool] = {}
        self.sequence = 0
        self.errors: list[str] = []
        self.process = subprocess.Popen(
            [str(executable), "--data-dir", str(data)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._read_errors, daemon=True).start()
        self.wait_state(lambda state: state.get("ready"), 30)

    def _read(self):
        assert self.process.stdout
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except json.JSONDecodeError as error:
                self.messages.put({"protocol_error": str(error), "line": line})

    def _read_errors(self):
        assert self.process.stderr
        for line in self.process.stderr:
            self.errors.append(line.rstrip())

    def _next(self, deadline: float) -> dict:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError(f"Bridge timeout; last state: {self.state.get('status')}")
        try:
            message = self.messages.get(timeout=remaining)
        except queue.Empty as error:
            raise TimeoutError(f"Bridge timeout; stderr: {self.errors}") from error
        assert "protocol_error" not in message, message
        if "state" in message:
            if message.get("event") == "trainer" or self.request_kinds.get(str(message.get("id")), False):
                self.trainer_state = message["state"]
                assert "features" in self.trainer_state and "protocolVersion" not in self.trainer_state
            else:
                self.state = message["state"]
                assert self.state["protocolVersion"] == 1
        return message

    def request(self, command: str, payload: dict | None = None, *, ok=True) -> dict:
        self.sequence += 1
        identity = f"smoke-{self.sequence}"
        self.request_kinds[identity] = command.startswith("trainer")
        assert self.process.stdin
        self.process.stdin.write(json.dumps({"id": identity, "command": command, "payload": payload or {}}, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + 30
        while True:
            message = self._next(deadline)
            if message.get("id") == identity:
                self.request_kinds.pop(identity, None)
                assert message["ok"] is ok, message.get("error") or message
                return message

    def wait_state(self, predicate, timeout=15) -> dict:
        deadline = time.monotonic() + timeout
        while not predicate(self.state):
            self._next(deadline)
        return self.state

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
            raise AssertionError("Bridge did not stop after stdin EOF")
        assert self.process.returncode == 0, self.errors
        assert not self.errors, self.errors


def exercise(executable: Path, complete: bool):
    with tempfile.TemporaryDirectory(prefix="grim-dawn-bridge-smoke-") as temporary:
        data = Path(temporary)
        bridge = Bridge(executable, data)
        try:
            initial = bridge.request("state")["state"]
            assert not initial["isBusy"] and not initial["canOptimize"]
            bridge.request("unsupportedCommand", ok=False)
            demo = bridge.request("demo")["state"]
            assert demo["workspace"] == "build" and demo["canOptimize"]
            assert len(demo["characters"]) == 1 and len(demo["equipment"]) == 14
            assert demo["panel"]["dps"] not in ["—", "不可用", ""]
            before_goal = demo["goalIndex"]
            bridge.request("configure", {"goalIndex": (before_goal + 1) % 3, "targetCount": 0}, ok=False)
            assert bridge.request("state")["state"]["goalIndex"] == before_goal, "Invalid patches must be atomic"
            configured = bridge.request("configure", {
                "skillRecord": "", "difficultyIndex": 3, "goalIndex": 1,
                "theme": "dark", "targetCount": 2, "durationSeconds": 45,
                "enemyDamageIncreasePercent": 12, "resistanceDebuffs": {"fire": 10},
            })["state"]
            assert configured["selectedSkillRecord"] == ""
            assert configured["panel"]["dps"] == configured["panel"]["defaultDps"]
            assert configured["difficultyIndex"] == 3 and configured["theme"] == "dark"
            assert configured["scenario"]["targetCount"] == 2
            assert any(row["type"] == "fire" and row["reduction"] == 10 for row in configured["scenario"]["resistanceDebuffs"])
            bridge.request("optimize")
            bridge.wait_state(lambda state: state.get("isBusy"))
            bridge.request("configure", {"goalIndex": 0}, ok=False)
            bridge.request("cancel")
            bridge.wait_state(lambda state: not state.get("isBusy"))
            assert bridge.state["canOptimize"], "Cancel must leave a usable character"

            if complete:
                bridge.request("optimize")
                bridge.wait_state(lambda state: not state.get("isBusy"), 210)
                assert bridge.state["results"], bridge.state["status"]
                assert bridge.state["workspace"] == "results"
                results = bridge.state["results"]
                assert len(results) <= 10
                for result in results:
                    assert len(result["slotRows"]) == 14
                    assert all(all(key in row for key in ["slot", "currentItem", "candidateItem", "component", "augment", "source", "changed"]) for row in result["slotRows"])
                selected = bridge.request("selectResult", {"rank": results[-1]["rank"]})["state"]
                assert selected["selectedResultRank"] == results[-1]["rank"]
                for swap in results[-1]["singleSwaps"]:
                    if swap["rows"]:
                        row = swap["rows"][0]
                        compared = bridge.request("compareItem", {"index": swap["index"], "metricKey": row["key"], "actualInput": row["predicted"]})["state"]
                        chosen = next(item for item in compared["results"] if item["rank"] == results[-1]["rank"])
                        assert chosen["singleSwaps"][swap["index"]]["rows"][0]["actualInput"] == row["predicted"]
                        break
                invalidated = bridge.request("configure", {"goalIndex": 2})["state"]
                assert not invalidated["results"] and invalidated["selectedResultRank"] is None

            source = data / "empty-save-source"
            source.mkdir()
            changed = bridge.request("selectSaveDirectory", {"path": str(source)})["state"]
            assert not changed["characters"] and not changed["results"]
            assert changed["workspace"] == "sources"
            removed = bridge.request("deleteSaveDirectory", {"path": str(source)})["state"]
            assert source.is_dir(), "History deletion must never delete the folder"
            assert all(Path(item["path"]) != source for item in removed["saveLocations"])
            bridge.request("demo")
            bridge.request("saveProfile")
            bridge.wait_state(lambda state: not state.get("isBusy"))
        finally:
            bridge.close()
        preferences = json.loads((data / "ui-settings.json").read_text(encoding="utf-8"))
        assert preferences["theme"] == "Dark"
        assert (data / "profiles.json").is_file()
        restarted = Bridge(executable, data)
        try:
            assert restarted.request("state")["state"]["theme"] == "dark"
        finally:
            restarted.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", type=Path)
    parser.add_argument("--complete", action="store_true")
    args = parser.parse_args()
    candidates = [ROOT / "src/GrimDawnOptimizer.Bridge/bin" / configuration / "net8.0-windows/win-x64/GrimDawnOptimizer.Bridge.exe" for configuration in ["Release", "Debug"]]
    executable = args.executable or next((path for path in candidates if path.is_file()), None)
    if executable is None:
        parser.error("Build src/GrimDawnOptimizer.Bridge first, or pass --executable.")
    exercise(executable.resolve(), args.complete)
    print("PASS: actual bridge process, isolated settings, demo, validation, cancel, source invalidation, profile, EOF, restart" + (", complete optimization/results" if args.complete else ""))


if __name__ == "__main__":
    main()
