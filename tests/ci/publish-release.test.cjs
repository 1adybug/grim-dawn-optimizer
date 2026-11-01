const test = require("node:test");
const assert = require("node:assert/strict");
const { mkdtemp, writeFile, readFile, rm } = require("node:fs/promises");
const { join } = require("node:path");
const { tmpdir } = require("node:os");
const { createHash } = require("node:crypto");
const publish = require("../../.github/scripts/publish-release.cjs");
const {
  formatDraftBody,
  parseDraftBody,
  payloadNames,
} = require("../../.github/scripts/release-draft.cjs");

const sha256 = (data) => createHash("sha256").update(data).digest("hex");
// The tag publishing run is different from the verified main-branch build run.
const context = {
  ref: "refs/tags/v1.0.21",
  sha: "a".repeat(40),
  runId: 456,
  repo: { owner: "test-owner", repo: "test-repo" },
};
const portableName = "GrimDawnOptimizer-1.0.21-win-x64.exe";
const portableData = Buffer.from(`test payload ${portableName}`);
const sourceManifest = {
  schemaVersion: 2,
  version: "1.0.21",
  commit: context.sha,
  runId: "123",
  files: payloadNames("1.0.21").map((name) => {
    const data = Buffer.from(`test payload ${name}`);
    return { name, size: data.length, sha256: sha256(data) };
  }),
};
const stageOptions = {
  stageOnly: true,
  context: { ...context, ref: "refs/heads/main", runId: 123 },
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
  for (const name of payloadNames("1.0.21")) {
    const data = Buffer.from(`test payload ${name}`);
    await writeFile(join(directory, name), data);
    files.push({ name, size: data.length, sha256: sha256(data) });
  }
  const manifest = {
    schemaVersion: 2,
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
  failNotes = false,
  resetOmittedDraftTag = false,
  body = formatDraftBody(sourceManifest),
  targetCommit = context.sha,
} = {}) {
  let release =
    draft === undefined
      ? null
      : {
          id: 10,
          tag_name: "v1.0.21",
          draft,
          body,
          target_commitish: targetCommit,
        };
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
    generateReleaseNotes: async (args) => {
      reads.push(["generateReleaseNotes", args]);
      if (failNotes) throw new Error("Release notes unavailable");
      return { data: { body: "## Changes\n\nGenerated release notes." } };
    },
    updateRelease: async (args) => {
      writes.push(["updateRelease", args]);
      release = { ...release, ...args };
      if (resetOmittedDraftTag && release.draft && !args.tag_name)
        release.tag_name = "untagged-github-placeholder";
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

test("main stages both packages and updater metadata and tag publishes notes with hidden provenance", async (t) => {
  const f = await fixture(t);
  const api = mock();
  await run(f, api, stageOptions);
  assert.deepEqual(
    api.writes.map(([name]) => name),
    ["createRelease", ...Array(4).fill("uploadReleaseAsset"), "updateRelease"],
  );
  const create = api.writes[0][1];
  assert.equal(create.draft, true);
  assert.equal(create.generate_release_notes, undefined);
  assert.equal(create.target_commitish, context.sha);
  assert.equal(create.tag_name, "v1.0.21");
  assert.equal(api.release.draft, true);
  assert.deepEqual(parseDraftBody(api.release.body), f.manifest);
  assert.equal(api.release.target_commitish, context.sha);
  assert.equal(
    api.reads.some(([name]) => name === "generateReleaseNotes"),
    false,
  );
  assert.equal(api.assets.length, 4);
  assert.equal(api.assets[0].name, "GrimDawnOptimizer-1.0.21-win-x64.exe");
  await run(f, api);
  assert.equal(api.release.draft, false);
  assert.equal(api.release.make_latest, "true");
  assert.ok(
    api.release.body.startsWith("## Changes\n\nGenerated release notes."),
  );
  assert.deepEqual(parseDraftBody(api.release.body), f.manifest);
  assert.equal(
    api.writes.filter(([name]) => name === "uploadReleaseAsset").length,
    4,
  );
});

test("draft updates explicitly retain their version before the Git tag exists", async (t) => {
  const f = await fixture(t);
  const api = mock({ resetOmittedDraftTag: true });
  await run(f, api, stageOptions);
  assert.equal(api.release.tag_name, "v1.0.21");
  await run(f, api);
  assert.equal(api.release.tag_name, "v1.0.21");
  assert.equal(api.release.draft, false);
  const updates = api.writes.filter(([name]) => name === "updateRelease");
  assert.equal(updates.length, 2);
  for (const [, args] of updates) assert.equal(args.tag_name, "v1.0.21");
});

test("staging rejects a GitHub response that lost the version tag", async (t) => {
  const f = await fixture(t);
  const api = mock();
  const update = api.github.rest.repos.updateRelease;
  api.github.rest.repos.updateRelease = async (args) => {
    const response = await update(args);
    response.data.tag_name = "untagged-github-placeholder";
    return response;
  };
  await assert.rejects(
    run(f, api, stageOptions),
    /retain the staged version tag/,
  );
});

test("upload failure leaves a recoverable draft and never publishes", async (t) => {
  const f = await fixture(t);
  const api = mock({ failUpload: f.assets[0].name });
  await assert.rejects(run(f, api, stageOptions), /Upload interrupted/);
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
      present
        ? ["updateRelease"]
        : [...Array(4).fill("uploadReleaseAsset"), "updateRelease"],
    );
    assert.equal(api.assets.length, 4);
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
    await assert.rejects(
      run(f, api),
      /Unexpected release payloads|Expected portable EXE/,
    );
    assert.deepEqual(api.reads, []);
    assert.deepEqual(api.writes, []);
  }
});

test("ZIP, incomplete packages, extra payloads and modified checksums fail before any GitHub request", async (t) => {
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

test("a newer main build can replace a prior draft payload and then update its provenance", async (t) => {
  const f = await fixture(t);
  const previous = { ...f.manifest, commit: "b".repeat(40), runId: "122" };
  const api = mock({
    draft: true,
    body: formatDraftBody(previous),
    targetCommit: previous.commit,
    assets: [{ ...f.assets[0], digest: `sha256:${"0".repeat(64)}` }],
  });
  await run(f, api, stageOptions);
  assert.deepEqual(
    api.writes.map(([name]) => name),
    [
      "deleteReleaseAsset",
      ...Array(4).fill("uploadReleaseAsset"),
      "updateRelease",
    ],
  );
  assert.equal(api.release.draft, true);
  assert.equal(api.release.target_commitish, context.sha);
  assert.deepEqual(parseDraftBody(api.release.body), f.manifest);
  assert.equal(
    api.reads.some(([name]) => name === "generateReleaseNotes"),
    false,
  );
});

test("main staging leaves an already public version untouched even when its provenance differs", async (t) => {
  const f = await fixture(t);
  const api = mock({
    draft: false,
    body: "Earlier public release",
    targetCommit: "b".repeat(40),
    assets: [],
  });
  await run(f, api, stageOptions);
  assert.deepEqual(api.writes, []);
  assert.equal(api.release.body, "Earlier public release");
});

test("staging requires main and the current build run rather than a different source run", async (t) => {
  const f = await fixture(t);
  const api = mock();
  await assert.rejects(
    run(f, api, { stageOnly: true }),
    /requires the main branch/,
  );
  await assert.rejects(
    run(f, api, {
      stageOnly: true,
      context: { ...context, ref: "refs/heads/main" },
    }),
    /does not match/,
  );
  assert.deepEqual(api.reads, []);
  assert.deepEqual(api.writes, []);
});

test("tag refuses missing, mismatched or malformed staged provenance before mutating assets", async (t) => {
  const f = await fixture(t);
  const absent = mock();
  await assert.rejects(run(f, absent), /No staged release/);
  assert.deepEqual(absent.writes, []);
  for (const options of [
    { body: "No build manifest" },
    { body: formatDraftBody({ ...f.manifest, commit: "b".repeat(40) }) },
    { body: formatDraftBody({ ...f.manifest, runId: "122" }) },
    { targetCommit: "b".repeat(40) },
  ]) {
    const api = mock({ draft: true, assets: f.assets, ...options });
    await assert.rejects(run(f, api));
    assert.deepEqual(api.writes, []);
  }
});

test("release notes failure preserves a fully staged draft", async (t) => {
  const f = await fixture(t);
  const api = mock({ draft: true, assets: f.assets, failNotes: true });
  await assert.rejects(run(f, api), /Release notes unavailable/);
  assert.deepEqual(api.writes, []);
  assert.equal(api.release.draft, true);
  assert.deepEqual(parseDraftBody(api.release.body), f.manifest);
});

test("shared draft manifest parser accepts hidden notes metadata and rejects ambiguity or unsafe payloads", async (t) => {
  const f = await fixture(t);
  const marker = formatDraftBody(f.manifest);
  assert.deepEqual(parseDraftBody(`Public notes\n\n${marker}`), f.manifest);
  assert.throws(() => parseDraftBody(`${marker}\n${marker}`), /exactly one/);
  assert.throws(() => parseDraftBody("<!-- grim-dawn-build\n{bad}\n-->"));
  for (const invalid of [
    { ...f.manifest, runId: 123 },
    { ...f.manifest, commit: "main" },
    { ...f.manifest, extra: "unexpected" },
    {
      ...f.manifest,
      files: [{ ...f.manifest.files[0], name: "../escape.exe" }],
    },
    { ...f.manifest, files: [{ ...f.manifest.files[0], size: -1 }] },
  ])
    assert.throws(() => formatDraftBody(invalid));
});
