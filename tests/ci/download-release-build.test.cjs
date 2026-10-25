"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { mkdtemp, readFile, readdir, rm } = require("node:fs/promises");
const { join } = require("node:path");
const { tmpdir } = require("node:os");
const download = require("../../.github/scripts/download-release-build.cjs");
const {
  OTHER_SHA,
  PAYLOAD,
  run,
  harness,
} = require("./release-source-fixture.cjs");

async function fixture(t, options = {}) {
  const api = harness(options);
  const directory = await mkdtemp(join(tmpdir(), "gd-release-download-"));
  const values = {
    RELEASE_ID: "201",
    RELEASE_ASSET_ID: "301",
    RELEASE_BUILD_RUN_ID: "101",
  };
  const previous = {};
  for (const [key, value] of Object.entries(values)) {
    previous[key] = process.env[key];
    process.env[key] = value;
  }
  t.after(async () => {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
    await rm(directory, { recursive: true, force: true });
  });
  return {
    ...api,
    directory,
    input: { ...api.input, releaseDirectory: directory },
  };
}

test("downloads binary bytes and recreates internal evidence without executing anything", async (t) => {
  const f = await fixture(t);
  assert.deepEqual(await download(f.input), f.manifest);
  const file = f.manifest.files[0];
  assert.deepEqual(
    (await readdir(f.directory)).sort(),
    [file.name, "SHA256SUMS.txt", "release-manifest.json"].sort(),
  );
  assert.deepEqual(await readFile(join(f.directory, file.name)), PAYLOAD);
  assert.deepEqual(
    JSON.parse(
      await readFile(join(f.directory, "release-manifest.json"), "utf8"),
    ),
    f.manifest,
  );
  assert.equal(
    await readFile(join(f.directory, "SHA256SUMS.txt"), "utf8"),
    `${file.sha256}  ${file.name}\n`,
  );
  assert.deepEqual(f.calls.at(-1).parameters, {
    owner: "owner",
    repo: "project",
    asset_id: 301,
    headers: { accept: "application/octet-stream" },
  });
});

test("supports Octokit ArrayBuffer downloads and typed views without unrelated backing bytes", async (t) => {
  const padded = Buffer.concat([
    Buffer.from("extra"),
    PAYLOAD,
    Buffer.from("extra"),
  ]);
  const f = await fixture(t, {
    bytes: new Uint8Array(padded.buffer, padded.byteOffset + 5, PAYLOAD.length),
  });
  await download(f.input);
  assert.deepEqual(
    await readFile(join(f.directory, f.manifest.files[0].name)),
    PAYLOAD,
  );
  const array = PAYLOAD.buffer.slice(
    PAYLOAD.byteOffset,
    PAYLOAD.byteOffset + PAYLOAD.length,
  );
  f.input.github.rest.repos.getReleaseAsset = async () => ({ data: array });
  await download(f.input);
});

for (const [description, bytes] of [
  ["corrupted equal-size bytes", Buffer.alloc(PAYLOAD.length)],
  ["truncated bytes", PAYLOAD.subarray(1)],
  ["JSON metadata instead of bytes", { name: "not downloaded" }],
]) {
  test(`rejects ${description} before writing files`, async (t) => {
    const f = await fixture(t, { bytes });
    await assert.rejects(download(f.input), /verification|binary data/);
    assert.deepEqual(await readdir(f.directory), []);
  });
}

for (const [key, value] of [
  ["RELEASE_ID", "202"],
  ["RELEASE_ASSET_ID", "302"],
  ["RELEASE_BUILD_RUN_ID", "102"],
  ["RELEASE_ID", ""],
  ["RELEASE_ASSET_ID", "../301"],
]) {
  test(`rejects changed or invalid selection ${key}=${value}`, async (t) => {
    const f = await fixture(t);
    process.env[key] = value;
    await assert.rejects(download(f.input), /identifier|source changed/);
    assert.equal(
      f.calls.filter((call) => call.method === "getReleaseAsset").length,
      0,
    );
    assert.deepEqual(await readdir(f.directory), []);
  });
}

for (const [description, options] of [
  ["failed source run", { runs: [run({ conclusion: "failure" })] }],
  ["moved tag", { tagObject: { type: "commit", sha: OTHER_SHA } }],
  ["changed release target", { release: { target_commitish: OTHER_SHA } }],
]) {
  test(`rechecks ${description} before fetching bytes`, async (t) => {
    const f = await fixture(t, options);
    await assert.rejects(download(f.input));
    assert.equal(
      f.calls.filter((call) => call.method === "getReleaseAsset").length,
      0,
    );
    assert.deepEqual(await readdir(f.directory), []);
  });
}
