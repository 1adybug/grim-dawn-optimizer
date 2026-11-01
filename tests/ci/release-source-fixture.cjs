"use strict";

const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const {
  formatDraftBody,
  payloadNames,
} = require("../../.github/scripts/release-draft.cjs");
const SHA = "a".repeat(40);
const OTHER_SHA = "b".repeat(40);
const PAYLOAD = Buffer.from("portable EXE fixture; never executable");
const HASH = createHash("sha256").update(PAYLOAD).digest("hex");
const REPOSITORY = { full_name: "owner/project" };

function run(overrides = {}) {
  return {
    id: 101,
    head_sha: SHA,
    head_branch: "main",
    event: "push",
    path: ".github/workflows/build-release.yml",
    status: "completed",
    conclusion: "success",
    repository: REPOSITORY,
    head_repository: REPOSITORY,
    ...overrides,
  };
}

function harness(options = {}) {
  const manifest = options.manifest ?? {
    schemaVersion: 2,
    version: "1.0.21",
    commit: SHA,
    runId: "101",
    files: payloadNames("1.0.21").map((name) => ({
      name,
      size: PAYLOAD.length,
      sha256: HASH,
    })),
  };
  const release = {
    id: 201,
    tag_name: "v1.0.21",
    target_commitish: SHA,
    draft: true,
    body: formatDraftBody(manifest),
    ...options.release,
  };
  const assets =
    options.assets ??
    manifest.files.map((file, index) => ({
      id: 301 + index,
      name: file.name,
      size: PAYLOAD.length,
      state: "uploaded",
      digest: `sha256:${HASH}`,
    }));
  const calls = [];
  const outputs = {};
  const actions = { listWorkflowRuns() {} };
  const repos = {
    listReleases() {},
    listReleaseAssets() {},
    async getReleaseByTag(parameters) {
      calls.push({ method: "getReleaseByTag", parameters });
      if (options.lookupError) throw options.lookupError;
      return { data: release };
    },
    async getReleaseAsset(parameters) {
      calls.push({ method: "getReleaseAsset", parameters });
      return { data: options.bytes ?? PAYLOAD };
    },
  };
  const git = {
    async getRef(parameters) {
      calls.push({ method: "getRef", parameters });
      return {
        data: { object: options.tagObject ?? { type: "commit", sha: SHA } },
      };
    },
    async getTag(parameters) {
      calls.push({ method: "getTag", parameters });
      return {
        data: {
          object: options.annotatedObject ?? { type: "commit", sha: SHA },
        },
      };
    },
  };
  const github = {
    rest: { actions, repos, git },
    async paginate(method, parameters) {
      if (method === actions.listWorkflowRuns) {
        calls.push({ method: "listWorkflowRuns", parameters });
        return options.runs ?? [run()];
      }
      if (method === repos.listReleases) {
        calls.push({ method: "listReleases", parameters });
        return options.releases ?? [release];
      }
      assert.equal(method, repos.listReleaseAssets);
      calls.push({ method: "listReleaseAssets", parameters });
      return assets;
    },
  };
  return {
    input: {
      github,
      context: {
        repo: { owner: "owner", repo: "project" },
        sha: SHA,
        ref: "refs/tags/v1.0.21",
      },
      core: {
        setOutput(name, value) {
          outputs[name] = value;
        },
        info() {},
      },
    },
    calls,
    outputs,
    manifest,
    release,
    assets,
  };
}

module.exports = { SHA, OTHER_SHA, PAYLOAD, HASH, run, harness };
