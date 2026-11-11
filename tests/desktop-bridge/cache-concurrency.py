"""Exercise actual cache publication across four independent Windows processes.

Requires PowerShell 7 and a built Release Bridge. DLL fixtures contain marker
bytes only; they are never loaded, executed or injected into a game.
"""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
WORKER = r"""
param([string]$Bridge, [string]$Bundled, [string]$Cache, [string]$Ready, [string]$Gate)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom($Bridge)
$type = $assembly.GetType('GrimDawnOptimizer.Bridge.Trainer.TrainerBridgeCache', $true)
$prepare = $type.GetMethod('PrepareAsync', [Reflection.BindingFlags]'Static,NonPublic')
[IO.File]::WriteAllText($Ready, 'ready')
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (-not [IO.File]::Exists($Gate)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Cache test start gate timed out' }
    Start-Sleep -Milliseconds 10
}
$task = $prepare.Invoke($null, [object[]]@($Bundled, [Threading.CancellationToken]::None, $Cache))
$task.GetAwaiter().GetResult()
"""


def exercise(bridge, pwsh, root, corrupted):
    root.mkdir()
    bundled = root / "installation" / "CacheFixture.dll"
    bundled.parent.mkdir()
    payload = b"OFFLINE CACHE FIXTURE: NEVER LOAD\n" + bytes(range(256)) * 2048
    bundled.write_bytes(payload)
    cache = root / "cache"
    expected = cache / hashlib.sha256(payload).hexdigest() / bundled.name
    if corrupted:
        expected.parent.mkdir(parents=True)
        expected.write_bytes(b"corrupt")
    worker_script = root / "prepare.ps1"
    worker_script.write_text(WORKER, encoding="utf-8")
    gate = root / "start"
    ready = [root / f"ready-{index}" for index in range(4)]
    workers = []
    try:
        for marker in ready:
            workers.append(subprocess.Popen(
                [pwsh, "-NoProfile", "-NonInteractive", "-File", str(worker_script),
                 "-Bridge", str(bridge), "-Bundled", str(bundled), "-Cache", str(cache),
                 "-Ready", str(marker), "-Gate", str(gate)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            ))
        deadline = time.monotonic() + 30
        while not all(marker.exists() for marker in ready):
            for worker in workers:
                if worker.poll() is not None:
                    output, error = worker.communicate()
                    raise AssertionError(f"Worker exited before start: {output}\n{error}")
            if time.monotonic() > deadline:
                raise TimeoutError("Cache workers failed to reach the start gate")
            time.sleep(0.01)
        gate.touch()
        for worker in workers:
            output, error = worker.communicate(timeout=30)
            assert worker.returncode == 0, (worker.returncode, output, error)
            assert Path(output.strip()).samefile(expected), output
        assert expected.read_bytes() == payload
        assert list(expected.parent.iterdir()) == [expected], "Temporary or duplicate cache files remain"
    finally:
        for worker in workers:
            if worker.poll() is None:
                worker.kill()
                worker.communicate(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bridge", type=Path, default=ROOT / "src/GrimDawnOptimizer.Bridge/bin/Release/net8.0-windows/win-x64/GrimDawnOptimizer.Bridge.dll")
    args = parser.parse_args()
    bridge = args.bridge.resolve(strict=True)
    pwsh = shutil.which("pwsh")
    if not pwsh:
        parser.error("PowerShell 7 (pwsh) is required")
    with tempfile.TemporaryDirectory(prefix="grim-dawn-cache-processes-") as temporary:
        root = Path(temporary).resolve()
        assert root.is_relative_to(Path(tempfile.gettempdir()).resolve())
        exercise(bridge, pwsh, root / "missing", False)
        exercise(bridge, pwsh, root / "corrupted", True)
    print("PASS: four-process cache publication and corruption repair; complete DLL bytes and no temporary files")


if __name__ == "__main__":
    main()
