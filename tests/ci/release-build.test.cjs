"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const findReleaseBuild = require("../../.github/scripts/find-release-build.cjs");

const SHA = "a".repeat(40);
const OTHER_SHA = "b".repeat(40);
const REPOSITORY = { full_name: "owner/project" };

function run(overrides = {}) {
  return {
    id: 101,
    head_sha: SHA,
    head_branch: "main",
    event: "push",
    status: "completed",
    conclusion: "success",
    repository: REPOSITORY,
    head_repository: REPOSITORY,
    ...overrides,
  };
}

function artifact(overrides = {}) {
  return {
    id: 201,
    name: "windows-x64-release",
    expired: false,
    workflow_run: { id: 101, head_sha: SHA },
    ...overrides,
  };
}

function harness(runs = [run()], artifacts = { 101: [artifact()] }) {
  const calls = [];
  const outputs = {};
  const actions = {
    listWorkflowRuns() {},
    listWorkflowRunArtifacts() {},
  };
  const github = {
    rest: { actions },
    async paginate(method, parameters) {
      calls.push({ method, parameters });
      if (method === actions.listWorkflowRuns) return runs;
      assert.equal(method, actions.listWorkflowRunArtifacts);
      return artifacts[parameters.run_id] ?? [];
    },
  };
  return {
    input: {
      github,
      context: { repo: { owner: "owner", repo: "project" }, sha: SHA },
      core: {
        setOutput(name, value) {
          outputs[name] = value;
        },
        info() {},
      },
    },
    calls,
    outputs,
  };
}

test("selects the exact successful main build and exports immutable identifiers", async () => {
  const fixture = harness();
  assert.deepEqual(await findReleaseBuild(fixture.input), {
    runId: 101,
    artifactId: 201,
  });
  assert.deepEqual(fixture.outputs, { "run-id": "101", "artifact-id": "201" });
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
  assert.deepEqual(fixture.calls[1].parameters, {
    owner: "owner",
    repo: "project",
    run_id: 101,
    per_page: 100,
  });
});

for (const [description, overrides] of [
  ["another commit", { head_sha: OTHER_SHA }],
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
    const fixture = harness([run(overrides)]);
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /No completed successful main push run/,
    );
    assert.equal(fixture.calls.length, 1);
    assert.deepEqual(fixture.outputs, {});
  });
}

test("rejects an empty run search with an actionable retry message", async () => {
  const fixture = harness([]);
  await assert.rejects(
    findReleaseBuild(fixture.input),
    /rerun this tag release.*do not rebuild/,
  );
  assert.deepEqual(fixture.outputs, {});
});

for (const [description, artifacts] of [
  ["no artifact", []],
  ["only an expired artifact", [artifact({ expired: true })]],
  ["a differently named artifact", [artifact({ name: "untrusted-package" })]],
]) {
  test(`rejects ${description} without producing release outputs`, async () => {
    const fixture = harness([run()], { 101: artifacts });
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /none contains an unexpired windows-x64-release artifact/,
    );
    assert.deepEqual(fixture.outputs, {});
  });
}

test("rejects duplicate live artifacts instead of guessing", async () => {
  const fixture = harness([run()], {
    101: [artifact(), artifact({ id: 202 })],
  });
  await assert.rejects(findReleaseBuild(fixture.input), /multiple unexpired/);
  assert.deepEqual(fixture.outputs, {});
});

for (const [description, metadata] of [
  ["another SHA", { id: 101, head_sha: OTHER_SHA }],
  ["another run", { id: 999, head_sha: SHA }],
]) {
  test(`rejects artifact metadata naming ${description}`, async () => {
    const fixture = harness([run()], {
      101: [artifact({ workflow_run: metadata })],
    });
    await assert.rejects(
      findReleaseBuild(fixture.input),
      /Refusing mismatched release metadata/,
    );
    assert.deepEqual(fixture.outputs, {});
  });
}

test("allows absent optional artifact workflow metadata after validating its owning run", async () => {
  const fixture = harness([run()], {
    101: [artifact({ workflow_run: undefined })],
  });
  assert.equal((await findReleaseBuild(fixture.input)).artifactId, 201);
});

test("prefers the newest eligible run and can reuse an older same-commit artifact", async () => {
  const fixture = harness([run(), run({ id: 102 })], { 101: [artifact()] });
  assert.equal((await findReleaseBuild(fixture.input)).runId, 101);
  assert.deepEqual(
    fixture.calls.slice(1).map((call) => call.parameters.run_id),
    [102, 101],
  );
});

test("does not hide an API permission/network failure as missing artifacts", async () => {
  const fixture = harness();
  fixture.input.github.paginate = async () => {
    throw new Error("API unavailable");
  };
  await assert.rejects(findReleaseBuild(fixture.input), /API unavailable/);
  assert.deepEqual(fixture.outputs, {});
});

test("rejects an invalid event SHA before any API call", async () => {
  const fixture = harness();
  fixture.input.context.sha = "main";
  await assert.rejects(
    findReleaseBuild(fixture.input),
    /valid full commit SHA/,
  );
  assert.equal(fixture.calls.length, 0);
});
