const test = require("node:test");
const assert = require("node:assert/strict");
const { mkdtemp, writeFile, readFile, rm } = require("node:fs/promises");
const { join } = require("node:path");
const { tmpdir } = require("node:os");
const { createHash } = require("node:crypto");
const publish = require("../../.github/scripts/publish-release.cjs");

const sha256 = (data) => createHash("sha256").update(data).digest("hex");
// The tag publishing run is different from the verified main-branch build run.
const context = {
  ref: "refs/tags/v1.0.21",
  sha: "a".repeat(40),
  runId: 456,
  repo: { owner: "test-owner", repo: "test-repo" },
};

async function fixture(t) {
  const previousRunId = process.env.RELEASE_BUILD_RUN_ID;
  process.env.RELEASE_BUILD_RUN_ID = "123";
  t.after(() => {
    if (previousRunId === undefined) delete process.env.RELEASE_BUILD_RUN_ID;
    else process.env.RELEASE_BUILD_RUN_ID = previousRunId;
  });
  const directory = await mkdtemp(join(tmpdir(), "gd-release-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const files = [];
  for (const name of ["GrimDawnOptimizer-1.0.21-win-x64.exe"]) {
    const data = Buffer.from(`test payload ${name}`);
    await writeFile(join(directory, name), data);
    files.push({ name, size: data.length, sha256: sha256(data) });
  }
  const manifest = {
    schemaVersion: 1,
    version: "1.0.21",
    commit: context.sha,
    runId: "123",
    files,
  };
  await writeFile(
    join(directory, "release-manifest.json"),
    JSON.stringify(manifest),
  );
  await writeFile(
    join(directory, "SHA256SUMS.txt"),
    files.map((file) => `${file.sha256}  ${file.name}\n`).join(""),
  );
  const assets = [];
  for (const name of files.map((file) => file.name)) {
    const data = await readFile(join(directory, name));
    assets.push({
      id: assets.length + 1,
      name,
      state: "uploaded",
      size: data.length,
      digest: `sha256:${sha256(data)}`,
    });
  }
  return { directory, assets, manifest };
}

function mock({
  draft,
  assets = [],
  failUpload,
  hiddenDraft = false,
  failLookup,
} = {}) {
  let release =
    draft === undefined ? null : { id: 10, tag_name: "v1.0.21", draft };
  let remote = assets.map((asset) => ({ ...asset }));
  let nextId = 100;
  const writes = [];
  const reads = [];
  const missing = () => Object.assign(new Error("Not Found"), { status: 404 });
  const repos = {
    getReleaseByTag: async (args) => {
      reads.push(["getReleaseByTag", args]);
      if (failLookup) throw failLookup;
      if (!release || hiddenDraft) throw missing();
      return { data: { ...release } };
    },
    listReleases: async (args) => {
      reads.push(["listReleases", args]);
      return { data: release ? [{ ...release }] : [] };
    },
    createRelease: async (args) => {
      writes.push(["createRelease", args]);
      release = { id: 10, ...args };
      return { data: { ...release } };
    },
    getRelease: async (args) => {
      reads.push(["getRelease", args]);
      return { data: { ...release } };
    },
    listReleaseAssets: async (args) => {
      reads.push(["listReleaseAssets", args]);
      return {
        data: remote.slice(
          (args.page - 1) * args.per_page,
          args.page * args.per_page,
        ),
      };
    },
    deleteReleaseAsset: async (args) => {
      writes.push(["deleteReleaseAsset", args]);
      assert.equal(release.draft, true);
      remote = remote.filter((asset) => asset.id !== args.asset_id);
      return { status: 204 };
    },
    uploadReleaseAsset: async (args) => {
      writes.push(["uploadReleaseAsset", args]);
      assert.equal(release.draft, true);
      assert.equal(args.headers["content-length"], args.data.length);
      if (args.name === failUpload) throw new Error("Upload interrupted");
      const asset = {
        id: nextId++,
        name: args.name,
        state: "uploaded",
        size: args.data.length,
        digest: `sha256:${sha256(args.data)}`,
      };
      remote.push(asset);
      return { data: asset };
    },
    updateRelease: async (args) => {
      writes.push(["updateRelease", args]);
      release = { ...release, ...args };
      return { data: { ...release } };
    },
  };
  return {
    github: { rest: { repos } },
    writes,
    reads,
    get release() {
      return release;
    },
    get assets() {
      return remote;
    },
  };
}

const run = (f, api, override = {}) =>
  publish({
    github: api.github,
    context,
    core: { info() {} },
    releaseDirectory: f.directory,
    ...override,
  });

test("new release stays draft until its portable EXE verifies and never uploads CI metadata", async (t) => {
  const f = await fixture(t);
  const api = mock();
  await run(f, api);
  assert.deepEqual(
    api.writes.map(([name]) => name),
    ["createRelease", "uploadReleaseAsset", "updateRelease"],
  );
  const create = api.writes[0][1];
  assert.equal(create.draft, true);
  assert.equal(create.generate_release_notes, true);
  assert.equal(create.target_commitish, context.sha);
  assert.equal(create.tag_name, "v1.0.21");
  assert.equal(api.release.draft, false);
  assert.equal(api.release.make_latest, "true");
  assert.equal(api.assets.length, 1);
  assert.equal(api.assets[0].name, "GrimDawnOptimizer-1.0.21-win-x64.exe");
});

test("upload failure leaves a recoverable draft and never publishes", async (t) => {
  const f = await fixture(t);
  const api = mock({ failUpload: f.assets[0].name });
  await assert.rejects(run(f, api), /Upload interrupted/);
  assert.equal(api.release.draft, true);
  assert.equal(api.assets.length, 0);
  assert.equal(
    api.writes.some(([name]) => name === "updateRelease"),
    false,
  );
});

test("matching public release is checked without any mutation", async (t) => {
  const f = await fixture(t);
  const api = mock({ draft: false, assets: f.assets });
  await run(f, api);
  assert.deepEqual(api.writes, []);
});

test("public release with wrong digest, size, missing, or extra asset is never repaired", async (t) => {
  const f = await fixture(t);
  const cases = [
    [
      { ...f.assets[0], digest: "sha256:" + "0".repeat(64) },
      ...f.assets.slice(1),
    ],
    [{ ...f.assets[0], size: 0 }, ...f.assets.slice(1)],
    [{ ...f.assets[0], digest: null }, ...f.assets.slice(1)],
    f.assets.slice(1),
    [...f.assets, { ...f.assets[0], id: 50, name: "unexpected.zip" }],
  ];
  for (const assets of cases) {
    const api = mock({ draft: false, assets });
    await assert.rejects(run(f, api), /Release assets differ/);
    assert.deepEqual(api.writes, []);
  }
});

test("draft rerun replaces an incorrect same-name EXE before publication", async (t) => {
  const f = await fixture(t);
  const api = mock({
    draft: true,
    hiddenDraft: true,
    assets: [{ ...f.assets[0], digest: "sha256:" + "0".repeat(64) }],
  });
  await run(f, api);
  assert.equal(
    api.writes.some(([name]) => name === "createRelease"),
    false,
  );
  assert.equal(
    api.writes.filter(([name]) => name === "deleteReleaseAsset").length,
    1,
  );
  assert.equal(
    api.writes.find(([name]) => name === "deleteReleaseAsset")[1].asset_id,
    f.assets[0].id,
  );
  assert.deepEqual(
    api.writes
      .filter(([name]) => name === "uploadReleaseAsset")
      .map(([, args]) => args.name),
    f.assets.map((asset) => asset.name),
  );
  assert.notEqual(api.assets[0].id, f.assets[0].id);
  assert.equal(api.release.draft, false);
});

test("draft rerun retains a verified EXE or uploads the missing EXE", async (t) => {
  const f = await fixture(t);
  for (const present of [true, false]) {
    const api = mock({
      draft: true,
      hiddenDraft: true,
      assets: present ? f.assets : [],
    });
    await run(f, api);
    assert.deepEqual(
      api.writes.map(([name]) => name),
      present ? ["updateRelease"] : ["uploadReleaseAsset", "updateRelease"],
    );
    assert.equal(api.assets.length, 1);
    if (present) assert.equal(api.assets[0].id, f.assets[0].id);
    assert.equal(api.release.draft, false);
  }
});

test("unknown draft assets are preserved and prevent accidental publication", async (t) => {
  const f = await fixture(t);
  const api = mock({
    draft: true,
    assets: [{ ...f.assets[0], name: "unexpected.zip" }],
  });
  await assert.rejects(run(f, api), /unexpected assets/);
  assert.deepEqual(api.writes, []);
  assert.equal(api.release.draft, true);
});

test("unsafe manifest filenames fail before any GitHub request", async (t) => {
  const f = await fixture(t);
  for (const name of ["../escape.zip", "..\\escape.zip", "C:\\escape.zip"]) {
    await writeFile(
      join(f.directory, "release-manifest.json"),
      JSON.stringify({
        ...f.manifest,
        files: [{ ...f.manifest.files[0], name }],
      }),
    );
    const api = mock();
    await assert.rejects(run(f, api), /Unsafe release asset name/);
    assert.deepEqual(api.reads, []);
    assert.deepEqual(api.writes, []);
  }
});

test("ZIP, installer, multiple payloads and modified checksums fail before any GitHub request", async (t) => {
  const f = await fixture(t);
  for (const files of [
    [{ ...f.manifest.files[0], name: "GrimDawnOptimizer-1.0.21-win-x64.zip" }],
    [
      {
        ...f.manifest.files[0],
        name: "GrimDawnOptimizer-1.0.21-x64-setup.exe",
      },
    ],
    [...f.manifest.files, { ...f.manifest.files[0], name: "extra.exe" }],
  ]) {
    await writeFile(
      join(f.directory, "release-manifest.json"),
      JSON.stringify({ ...f.manifest, files }),
    );
    const api = mock();
    await assert.rejects(run(f, api), /Expected.*portable EXE/);
    assert.deepEqual(api.reads, []);
  }
  await writeFile(
    join(f.directory, "release-manifest.json"),
    JSON.stringify(f.manifest),
  );
  await writeFile(join(f.directory, "SHA256SUMS.txt"), "altered checksum");
  const api = mock();
  await assert.rejects(run(f, api), /Checksum list/);
  assert.deepEqual(api.reads, []);
});

test("mismatched build identity and altered payload fail before creating a release", async (t) => {
  const f = await fixture(t);
  const api = mock();
  await assert.rejects(
    run(f, api, { context: { ...context, sha: "b".repeat(40) } }),
    /does not match/,
  );
  process.env.RELEASE_BUILD_RUN_ID = "124";
  await assert.rejects(run(f, api), /does not match/);
  delete process.env.RELEASE_BUILD_RUN_ID;
  await assert.rejects(run(f, api), /does not match/);
  process.env.RELEASE_BUILD_RUN_ID = "123";
  await writeFile(join(f.directory, f.manifest.files[0].name), "tampered");
  await assert.rejects(run(f, api), /failed local verification/);
  assert.deepEqual(api.reads, []);
  assert.deepEqual(api.writes, []);
});

test("authorization errors do not masquerade as a missing release", async (t) => {
  const f = await fixture(t);
  const api = mock({
    failLookup: Object.assign(new Error("Forbidden"), { status: 403 }),
  });
  await assert.rejects(run(f, api), /Forbidden/);
  assert.deepEqual(api.writes, []);
});

test("draft becoming public before a repair prevents every asset mutation", async (t) => {
  const f = await fixture(t);
  const api = mock({ draft: true, assets: [{ ...f.assets[0], size: 0 }] });
  api.github.rest.repos.getRelease = async () => ({
    data: { id: 10, draft: false },
  });
  await assert.rejects(run(f, api), /became public/);
  assert.deepEqual(api.writes, []);
});
