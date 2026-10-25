import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  copyFile,
  mkdir,
  readFile,
  readdir,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../..",
);
const defaultDirectory = join(repositoryRoot, "artifacts/release");
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
const payloadNames = (version) => [
  `grim-dawn-optimizer-${version}-win-x64.zip`,
  `GrimDawnOptimizer-${version}-x64-setup.exe`,
];
const checksumText = (files) =>
  files.map((file) => `${file.sha256}  ${file.name}\n`).join("");

export async function verifyRelease({
  directory = defaultDirectory,
  commit,
  tag,
  runId,
}) {
  const manifest = JSON.parse(
    await readFile(join(directory, "release-manifest.json"), "utf8"),
  );
  assert.equal(manifest.schemaVersion, 1, "Unsupported release manifest");
  assert.match(commit ?? "", /^[a-f0-9]{40}$/, "Expected a full commit SHA");
  assert.equal(
    manifest.commit,
    commit,
    "Release commit does not match the tag",
  );
  assert.match(
    manifest.version ?? "",
    /^\d+\.\d+\.\d+$/,
    "Invalid release version",
  );
  assert.equal(
    tag,
    `v${manifest.version}`,
    "Tag does not match the package version",
  );
  assert.match(
    String(runId ?? ""),
    /^\d+$/,
    "Expected the source build run ID",
  );
  assert.equal(
    manifest.runId,
    String(runId),
    "Release came from a different build run",
  );
  assert.ok(Array.isArray(manifest.files), "Missing release files");
  assert.deepEqual(
    manifest.files.map((file) => file.name),
    payloadNames(manifest.version),
    "Unexpected release payloads",
  );
  const expectedFiles = [
    ...payloadNames(manifest.version),
    "release-manifest.json",
    "SHA256SUMS.txt",
  ];
  assert.deepEqual(
    (await readdir(directory)).sort(),
    expectedFiles.sort(),
    "Missing or unexpected artifact files",
  );
  for (const file of manifest.files) {
    const bytes = await readFile(join(directory, file.name));
    assert.ok(
      file.size > 0 && Number.isSafeInteger(file.size),
      "Invalid package size",
    );
    assert.equal(bytes.length, file.size, `Size mismatch: ${file.name}`);
    assert.equal(sha256(bytes), file.sha256, `Checksum mismatch: ${file.name}`);
  }
  assert.equal(
    await readFile(join(directory, "SHA256SUMS.txt"), "utf8"),
    checksumText(manifest.files),
    "Checksum list does not match the manifest",
  );
  return manifest;
}

export async function stageRelease({
  root = repositoryRoot,
  directory = defaultDirectory,
  commit,
  runId,
}) {
  assert.match(commit ?? "", /^[a-f0-9]{40}$/, "Expected a full commit SHA");
  assert.match(
    String(runId ?? ""),
    /^\d+$/,
    "Expected the source build run ID",
  );
  const desktop = join(root, "src/GrimDawnOptimizer.Desktop");
  const configuration = JSON.parse(
    await readFile(join(desktop, "src-tauri/tauri.conf.json"), "utf8"),
  );
  const packageJson = JSON.parse(
    await readFile(join(desktop, "package.json"), "utf8"),
  );
  const version = configuration.version;
  assert.match(version, /^\d+\.\d+\.\d+$/, "Invalid package version");
  assert.equal(
    packageJson.version,
    version,
    "Frontend and installer versions differ",
  );
  const names = payloadNames(version);
  const sources = [
    join(root, "artifacts/tauri-win-x64.zip"),
    join(
      desktop,
      "src-tauri/target/release/bundle/nsis",
      `Grim Dawn Optimizer_${version}_x64-setup.exe`,
    ),
  ];
  await mkdir(directory, { recursive: true });
  const files = [];
  for (const [index, name] of names.entries()) {
    await copyFile(sources[index], join(directory, name));
    const bytes = await readFile(join(directory, name));
    files.push({ name, size: bytes.length, sha256: sha256(bytes) });
  }
  const manifest = {
    schemaVersion: 1,
    version,
    commit,
    runId: String(runId),
    files,
  };
  await writeFile(
    join(directory, "release-manifest.json"),
    JSON.stringify(manifest, null, 2) + "\n",
  );
  await writeFile(join(directory, "SHA256SUMS.txt"), checksumText(files));
  return verifyRelease({ directory, commit, tag: `v${version}`, runId });
}

if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  const options = {
    commit: process.env.GITHUB_SHA,
    runId: process.env.RELEASE_BUILD_RUN_ID || process.env.GITHUB_RUN_ID,
    tag: process.env.GITHUB_REF_NAME,
  };
  const command = process.argv[2];
  assert.ok(
    command === "stage" || command === "verify",
    "Usage: release-artifacts.mjs stage|verify",
  );
  const manifest = await (command === "stage"
    ? stageRelease(options)
    : verifyRelease(options));
  console.log(
    `Verified v${manifest.version}: ${manifest.commit}, build ${manifest.runId}, ${manifest.files.length} packages`,
  );
}
