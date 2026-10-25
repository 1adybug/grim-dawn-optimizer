"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const findReleaseBuild = require("../../.github/scripts/find-release-build.cjs");
const { formatDraftBody } = require("../../.github/scripts/release-draft.cjs");
const {
  SHA,
  OTHER_SHA,
  run,
  harness,
} = require("./release-source-fixture.cjs");

test("selects the exact successful main build and its verified release EXE", async () => {
  const fixture = harness();
  assert.deepEqual(await findReleaseBuild(fixture.input), {
    runId: 101,
    releaseId: 201,
    assetId: 301,
    alreadyPublished: false,
    manifest: fixture.manifest,
  });
  assert.deepEqual(fixture.outputs, {
    "run-id": "101",
    "release-id": "201",
    "asset-id": "301",
    "already-published": "false",
  });
  assert.deepEqual(fixture.calls[0].parameters, {
    owner: "owner",
    repo: "project",
    workflow_id: "build-release.yml",
    head_sha: SHA,
    event: "push",
    branch: "main",
    status: "success",
    per_page: 100,
  });
  assert.deepEqual(fixture.calls.at(-1).parameters, {
    owner: "owner",
    repo: "project",
    ref: "tags/v1.0.21",
  });
});

for (const [description, overrides] of [
  ["another commit", { head_sha: OTHER_SHA }],
  ["another workflow", { path: ".github/workflows/other.yml" }],
  ["another branch", { head_branch: "feature" }],
  ["a PR event", { event: "pull_request" }],
  ["a manual event", { event: "workflow_dispatch" }],
  ["a failed run", { conclusion: "failure" }],
  ["an incomplete run", { status: "in_progress" }],
  ["another repository", { repository: { full_name: "other/project" } }],
  ["a fork head", { head_repository: { full_name: "contributor/project" } }],
  ["missing repository metadata", { head_repository: null }],
]) {
  test(`rejects ${description} even when returned by the filtered API`, async () => {
    const fixture = harness({ runs: [run(overrides)] });
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /No completed successful main push run/,
    );
    assert.equal(fixture.calls.length, 1);
    assert.deepEqual(fixture.outputs, {});
  });
}

test("a draft hidden from lookup is found through the authenticated release list", async () => {
  const fixture = harness({
    lookupError: Object.assign(new Error("not found"), { status: 404 }),
  });
  assert.equal((await findReleaseBuild(fixture.input)).releaseId, 201);
  assert.ok(fixture.calls.some((call) => call.method === "listReleases"));
});

test("missing or ambiguous releases fail without output", async () => {
  const fixture = harness({
    lookupError: Object.assign(new Error("not found"), { status: 404 }),
    releases: [],
  });
  await assert.rejects(findReleaseBuild(fixture.input), /No staged release/);
  const duplicated = harness({
    lookupError: Object.assign(new Error("not found"), { status: 404 }),
    releases: [fixture.release, { ...fixture.release, id: 202 }],
  });
  await assert.rejects(
    findReleaseBuild(duplicated.input),
    /ambiguous build source/,
  );
});

test("does not conceal permission or network failures as a missing draft", async () => {
  const fixture = harness({
    lookupError: Object.assign(new Error("forbidden"), { status: 403 }),
  });
  await assert.rejects(findReleaseBuild(fixture.input), /forbidden/);
  assert.equal(
    fixture.calls.filter((call) => call.method === "listReleases").length,
    0,
  );
});

for (const [description, overrides] of [
  ["wrong target", { target_commitish: OTHER_SHA }],
  ["branch target", { target_commitish: "main" }],
  ["wrong tag", { tag_name: "v1.0.22" }],
  ["missing manifest", { body: "Release notes only" }],
  ["invalid release ID", { id: 0 }],
]) {
  test(`rejects release ${description}`, async () => {
    const fixture = harness({ release: overrides });
    await assert.rejects(findReleaseBuild(fixture.input));
    assert.deepEqual(fixture.outputs, {});
  });
}

for (const [field, value] of [
  ["commit", OTHER_SHA],
  ["version", "1.0.22"],
  ["runId", "999"],
]) {
  test(`rejects a release manifest with a different ${field}`, async () => {
    const fixture = harness();
    const manifest = { ...fixture.manifest, [field]: value };
    if (field === "version")
      manifest.files = [
        {
          ...manifest.files[0],
          name: `GrimDawnOptimizer-${value}-win-x64.exe`,
        },
      ];
    fixture.release.body = formatDraftBody(manifest);
    await assert.rejects(findReleaseBuild(fixture.input), /manifest does not/);
    assert.deepEqual(fixture.outputs, {});
  });
}

for (const [field, value] of [
  ["name", "installer.exe"],
  ["size", 999],
  ["digest", `sha256:${"0".repeat(64)}`],
  ["digest", null],
  ["state", "starter"],
  ["id", 0],
]) {
  test(`rejects mismatching asset ${field}=${value}`, async () => {
    const fixture = harness();
    fixture.assets[0][field] = value;
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /exactly the verified portable EXE/,
    );
  });
}

test("rejects missing or additional release assets", async () => {
  for (const count of [0, 2]) {
    const fixture = harness();
    if (!count) fixture.assets.length = 0;
    else fixture.assets.push({ ...fixture.assets[0], id: 302 });
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /exactly the verified portable EXE/,
    );
  }
});

test("selects the successful run named by the manifest, not an unrelated newer run", async () => {
  const fixture = harness({ runs: [run({ id: 102 }), run()] });
  assert.equal((await findReleaseBuild(fixture.input)).runId, 101);
});

test("a correctly published release is idempotent only with full build proof", async () => {
  const fixture = harness({ release: { draft: false } });
  assert.equal((await findReleaseBuild(fixture.input)).alreadyPublished, true);
  assert.equal(fixture.outputs["already-published"], "true");
  fixture.release.body = "No source manifest";
  await assert.rejects(findReleaseBuild(fixture.input), /manifest marker/);
});

test("resolves annotated tags and rejects tags moved after the workflow started", async () => {
  const fixture = harness({ tagObject: { type: "tag", sha: OTHER_SHA } });
  await findReleaseBuild(fixture.input);
  assert.ok(fixture.calls.some((call) => call.method === "getTag"));
  const moved = harness({ tagObject: { type: "commit", sha: OTHER_SHA } });
  await assert.rejects(findReleaseBuild(moved.input), /current Git tag/);
  assert.deepEqual(moved.outputs, {});
  const cycle = harness({
    tagObject: { type: "tag", sha: OTHER_SHA },
    annotatedObject: { type: "tag", sha: OTHER_SHA },
  });
  await assert.rejects(findReleaseBuild(cycle.input), /Cannot resolve/);
});

test("invalid event identity fails before API access", async () => {
  for (const [field, value] of [
    ["sha", "main"],
    ["ref", "refs/heads/main"],
  ]) {
    const fixture = harness();
    fixture.input.context[field] = value;
    await assert.rejects(findReleaseBuild(fixture.input));
    assert.equal(fixture.calls.length, 0);
  }
});
