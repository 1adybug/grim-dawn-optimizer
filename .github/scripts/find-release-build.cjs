"use strict";

const { parseDraftBody } = require("./release-draft.cjs");
const WORKFLOW = "build-release.yml";

function sameRepository(repository, expected) {
  return (
    typeof repository?.full_name === "string" &&
    repository.full_name.toLowerCase() === expected.toLowerCase()
  );
}

function validId(value) {
  return Number.isSafeInteger(value) && value > 0;
}

async function verifyTag(github, repository, tag, sha) {
  let object = (
    await github.rest.git.getRef({ ...repository, ref: `tags/${tag}` })
  ).data.object;
  const visited = new Set();
  while (object?.type === "tag") {
    if (visited.has(object.sha) || visited.size >= 8)
      throw new Error("Cannot resolve the release tag to a commit.");
    visited.add(object.sha);
    object = (
      await github.rest.git.getTag({ ...repository, tag_sha: object.sha })
    ).data.object;
  }
  if (object?.type !== "commit" || object.sha !== sha)
    throw new Error(
      "The current Git tag does not point to this exact release commit.",
    );
}

async function findRelease(github, repository, tag) {
  try {
    return (await github.rest.repos.getReleaseByTag({ ...repository, tag }))
      .data;
  } catch (error) {
    if (error.status !== 404) throw error;
  }
  const releases = await github.paginate(github.rest.repos.listReleases, {
    ...repository,
    per_page: 100,
  });
  const matching = releases.filter((release) => release.tag_name === tag);
  if (matching.length > 1)
    throw new Error(
      `Several releases use ${tag}; refusing an ambiguous build source.`,
    );
  if (!matching.length)
    throw new Error(
      `No staged release exists for ${tag}. Rerun the exact main build before retrying this tag release.`,
    );
  return matching[0];
}

/** Resolve a successful main build stored in a release. Never execute its EXE. */
module.exports = async function findReleaseBuild({ github, context, core }) {
  const { owner, repo } = context.repo;
  const repository = { owner, repo };
  const fullName = `${owner}/${repo}`;
  const sha = context.sha;
  if (typeof sha !== "string" || !/^[0-9a-f]{40}$/.test(sha))
    throw new Error("The tag event did not provide a valid full commit SHA.");
  if (!/^refs\/tags\/v\d+\.\d+\.\d+$/.test(context.ref ?? ""))
    throw new Error("Release selection requires a version tag.");
  const tag = context.ref.slice("refs/tags/".length);

  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    ...repository,
    workflow_id: WORKFLOW,
    head_sha: sha,
    event: "push",
    branch: "main",
    status: "success",
    per_page: 100,
  });
  const eligible = runs.filter(
    (run) =>
      validId(run.id) &&
      run.head_sha === sha &&
      run.head_branch === "main" &&
      run.path === `.github/workflows/${WORKFLOW}` &&
      run.event === "push" &&
      run.status === "completed" &&
      run.conclusion === "success" &&
      sameRepository(run.repository, fullName) &&
      sameRepository(run.head_repository, fullName),
  );
  if (!eligible.length)
    throw new Error(
      `No completed successful main push run of ${WORKFLOW} exists for ${fullName}@${sha}. ` +
        "Wait for that exact main build to succeed, then rerun this tag release. Tag releases do not rebuild.",
    );

  const release = await findRelease(github, repository, tag);
  if (
    !validId(release.id) ||
    release.tag_name !== tag ||
    release.target_commitish !== sha ||
    typeof release.draft !== "boolean"
  )
    throw new Error(
      "Release identity or target commit does not match this version tag.",
    );
  const manifest = parseDraftBody(release.body);
  if (`v${manifest.version}` !== tag || manifest.commit !== sha)
    throw new Error(
      "Release manifest does not match the exact tag and commit.",
    );
  const run = eligible.find(
    (candidate) => String(candidate.id) === manifest.runId,
  );
  if (!run)
    throw new Error(
      "Release manifest does not belong to a completed successful main push build for this commit.",
    );

  const assets = await github.paginate(github.rest.repos.listReleaseAssets, {
    ...repository,
    release_id: release.id,
    per_page: 100,
  });
  const file = manifest.files[0];
  const asset = assets[0];
  if (
    assets.length !== 1 ||
    !validId(asset?.id) ||
    asset.name !== file.name ||
    asset.state !== "uploaded" ||
    asset.size !== file.size ||
    asset.digest?.toLowerCase() !== `sha256:${file.sha256.toLowerCase()}`
  )
    throw new Error(
      "Release must contain exactly the verified portable EXE (name, size, and SHA-256 digest).",
    );
  await verifyTag(github, repository, tag, sha);

  const alreadyPublished = !release.draft;
  core.setOutput("run-id", String(run.id));
  core.setOutput("release-id", String(release.id));
  core.setOutput("asset-id", String(asset.id));
  core.setOutput("already-published", String(alreadyPublished));
  core.info(
    `Verified ${fullName} main build ${run.id}, release ${release.id}, asset ${asset.id}, commit ${sha}.`,
  );
  return {
    runId: run.id,
    releaseId: release.id,
    assetId: asset.id,
    alreadyPublished,
    manifest,
  };
};
