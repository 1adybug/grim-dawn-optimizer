import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import {
  stageRelease,
  verifyRelease,
} from "../../.github/scripts/release-artifacts.mjs";

const commit = "a".repeat(40);
const runId = "12345";
const tag = "v1.0.21";

async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), "grim-dawn-release-test-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const inputs = {
    "src/GrimDawnOptimizer.Desktop/package.json": JSON.stringify({
      version: "1.0.21",
    }),
    "src/GrimDawnOptimizer.Desktop/src-tauri/tauri.conf.json": JSON.stringify({
      version: "1.0.21",
    }),
    "artifacts/GrimDawnOptimizer-1.0.21-win-x64.exe": "EXE fixture",
  };
  for (const [path, contents] of Object.entries(inputs)) {
    await mkdir(dirname(join(root, path)), { recursive: true });
    await writeFile(join(root, path), contents);
  }
  const directory = join(root, "release");
  await stageRelease({ root, directory, commit, runId });
  return { root, directory, commit, runId, tag };
}

test("the single portable executable survives staging and verification", async (t) => {
  const options = await fixture(t);
  const manifest = await verifyRelease(options);
  assert.equal(manifest.files.length, 1);
  assert.equal(
    await readFile(join(options.directory, manifest.files[0].name), "utf8"),
    "EXE fixture",
  );
});

for (const [field, value] of [
  ["commit", "b".repeat(40)],
  ["tag", "v1.0.22"],
  ["runId", "54321"],
]) {
  test(`reject a different ${field}`, async (t) => {
    const options = await fixture(t);
    await assert.rejects(verifyRelease({ ...options, [field]: value }));
  });
}

test("reject modified package bytes even when the file size is unchanged", async (t) => {
  const options = await fixture(t);
  await writeFile(
    join(options.directory, "GrimDawnOptimizer-1.0.21-win-x64.exe"),
    "BAD fixture",
  );
  await assert.rejects(verifyRelease(options), /Checksum mismatch/);
});

test("reject missing packages, extra files and modified checksum lists", async (t) => {
  const options = await fixture(t);
  const extra = join(options.directory, "unexpected.exe");
  await writeFile(extra, "unexpected");
  await assert.rejects(verifyRelease(options), /unexpected artifact files/);
  await rm(extra);
  await writeFile(join(options.directory, "SHA256SUMS.txt"), "wrong");
  await assert.rejects(verifyRelease(options), /Checksum list/);
  await rm(join(options.directory, "GrimDawnOptimizer-1.0.21-win-x64.exe"));
  await assert.rejects(verifyRelease(options), /unexpected artifact files/);
});

test("reject manifest paths outside the artifact directory", async (t) => {
  const options = await fixture(t);
  const path = join(options.directory, "release-manifest.json");
  const manifest = JSON.parse(await readFile(path, "utf8"));
  manifest.files[0].name = "../other.zip";
  await writeFile(path, JSON.stringify(manifest));
  await assert.rejects(verifyRelease(options), /Unexpected release payloads/);
});
