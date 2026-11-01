"use strict";

const { createHash } = require("node:crypto");
const { mkdir, writeFile } = require("node:fs/promises");
const { join, resolve } = require("node:path");
const findReleaseBuild = require("./find-release-build.cjs");

/** Download verified bytes only. Never execute a release payload. */
module.exports = async function downloadReleaseBuild({
  github,
  context,
  core,
  releaseDirectory = resolve(
    process.env.GITHUB_WORKSPACE || process.cwd(),
    "artifacts/release",
  ),
}) {
  const expected = {
    releaseId: process.env.RELEASE_ID,
    assetId: process.env.RELEASE_ASSET_ID,
    runId: process.env.RELEASE_BUILD_RUN_ID,
  };
  for (const [name, value] of Object.entries(expected)) {
    if (!/^[1-9]\d*$/.test(value ?? "") || !Number.isSafeInteger(Number(value)))
      throw new Error(
        `Missing or invalid selected release identifier: ${name}`,
      );
  }
  // Re-read the release, source run and current tag: a draft may have changed
  // since selection. Never follow mutable IDs or trust body text alone.
  const source = await findReleaseBuild({
    github,
    context,
    core: { info() {}, setOutput() {} },
  });
  for (const [name, value] of Object.entries(expected)) {
    if (String(source[name]) !== value)
      throw new Error(
        `Selected release source changed before download: ${name}`,
      );
  }
  const downloaded = [];
  for (const file of source.manifest.files) {
    const response = await github.rest.repos.getReleaseAsset({
      owner: context.repo.owner,
      repo: context.repo.repo,
      asset_id: source.assets.find((asset) => asset.name === file.name).id,
      headers: { accept: "application/octet-stream" },
    });
    const raw = response.data;
    if (!(raw instanceof ArrayBuffer) && !ArrayBuffer.isView(raw))
      throw new Error("Release asset download did not return binary data.");
    const bytes =
      raw instanceof ArrayBuffer
        ? Buffer.from(raw)
        : Buffer.from(raw.buffer, raw.byteOffset, raw.byteLength);
    const hash = createHash("sha256").update(bytes).digest("hex");
    if (bytes.length !== file.size || hash !== file.sha256.toLowerCase())
      throw new Error(
        `Downloaded release asset failed size or SHA-256 verification: ${file.name}`,
      );
    downloaded.push({ file, bytes });
  }

  await mkdir(releaseDirectory, { recursive: true });
  for (const { file, bytes } of downloaded)
    await writeFile(join(releaseDirectory, file.name), bytes);
  await writeFile(
    join(releaseDirectory, "release-manifest.json"),
    JSON.stringify(source.manifest, null, 2) + "\n",
  );
  await writeFile(
    join(releaseDirectory, "SHA256SUMS.txt"),
    source.manifest.files
      .map((file) => `${file.sha256}  ${file.name}\n`)
      .join(""),
  );
  core.info(
    `Downloaded and verified ${downloaded.length} assets from main build ${source.runId}.`,
  );
  return source.manifest;
};
