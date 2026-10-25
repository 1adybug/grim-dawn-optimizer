"use strict";

const WORKFLOW = "build-release.yml";
const ARTIFACT = "windows-x64-release";

function sameRepository(repository, expected) {
  return (
    typeof repository?.full_name === "string" &&
    repository.full_name.toLowerCase() === expected.toLowerCase()
  );
}

function validId(value) {
  return Number.isSafeInteger(value) && value > 0;
}

/** Resolve an already tested main build. Never execute artifacts or start a build. */
module.exports = async function findReleaseBuild({ github, context, core }) {
  const { owner, repo } = context.repo;
  const repository = `${owner}/${repo}`;
  const sha = context.sha;
  if (typeof sha !== "string" || !/^[0-9a-f]{40}$/i.test(sha)) {
    throw new Error("The tag event did not provide a valid full commit SHA.");
  }

  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    owner,
    repo,
    workflow_id: WORKFLOW,
    head_sha: sha,
    event: "push",
    branch: "main",
    status: "success",
    per_page: 100,
  });
  // Recheck metadata rather than trusting server-side search filters alone.
  const eligible = runs
    .filter(
      (run) =>
        validId(run.id) &&
        run.head_sha === sha &&
        run.head_branch === "main" &&
        run.event === "push" &&
        run.status === "completed" &&
        run.conclusion === "success" &&
        sameRepository(run.repository, repository) &&
        sameRepository(run.head_repository, repository),
    )
    .sort((left, right) => right.id - left.id);

  if (eligible.length === 0) {
    throw new Error(
      `No completed successful main push run of ${WORKFLOW} exists for ${repository}@${sha}. ` +
        "Wait for that exact main build to succeed, then rerun this tag release. Tag releases do not rebuild.",
    );
  }

  for (const run of eligible) {
    const artifacts = await github.paginate(
      github.rest.actions.listWorkflowRunArtifacts,
      { owner, repo, run_id: run.id, per_page: 100 },
    );
    const matching = artifacts.filter(
      (artifact) => artifact.name === ARTIFACT && artifact.expired === false,
    );
    if (matching.length > 1) {
      throw new Error(
        `Run ${run.id} contains multiple unexpired ${ARTIFACT} artifacts. Refusing an ambiguous release source.`,
      );
    }
    if (matching.length === 0) continue;

    const artifact = matching[0];
    if (!validId(artifact.id)) {
      throw new Error(`Run ${run.id} returned an invalid release artifact ID.`);
    }
    if (
      (artifact.workflow_run?.head_sha !== undefined &&
        artifact.workflow_run.head_sha !== sha) ||
      (artifact.workflow_run?.id !== undefined &&
        artifact.workflow_run.id !== run.id)
    ) {
      throw new Error(
        `Artifact ${artifact.id} does not belong to the exact selected commit/run. Refusing mismatched release metadata.`,
      );
    }

    core.setOutput("run-id", String(run.id));
    core.setOutput("artifact-id", String(artifact.id));
    core.info(
      `Reusing ${repository} main build ${run.id}, artifact ${artifact.id}, commit ${sha}.`,
    );
    return { runId: run.id, artifactId: artifact.id };
  }

  throw new Error(
    `Successful main builds exist for ${repository}@${sha}, but none contains an unexpired ${ARTIFACT} artifact. ` +
      "Rerun the main build for that exact commit to create its release artifact, then rerun this tag release. No fallback build is started.",
  );
};
