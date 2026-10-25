const { readFile, lstat } = require("node:fs/promises");
const { resolve, join, basename } = require("node:path");
const { createHash } = require("node:crypto");

const digest = (data) =>
  `sha256:${createHash("sha256").update(data).digest("hex")}`;

function safeName(name) {
  if (
    typeof name !== "string" ||
    !name ||
    name !== basename(name) ||
    /[\\/:\0]/.test(name) ||
    name === "." ||
    name === ".." ||
    /[. ]$/.test(name)
  )
    throw new Error(`Unsafe release asset name: ${JSON.stringify(name)}`);
  return name;
}

async function localAssets(directory, context) {
  const read = async (name) => {
    const file = join(directory, safeName(name));
    if (!(await lstat(file)).isFile())
      throw new Error(`Release asset is not a regular file: ${name}`);
    const data = await readFile(file);
    return { name, data, size: data.length, digest: digest(data) };
  };
  const manifestAsset = await read("release-manifest.json");
  const manifest = JSON.parse(manifestAsset.data.toString("utf8"));
  if (!context.ref?.startsWith("refs/tags/v"))
    throw new Error("Publishing requires a version tag.");
  const tag = context.ref.slice("refs/tags/".length);
  const buildRunId = process.env.RELEASE_BUILD_RUN_ID;
  if (
    manifest.schemaVersion !== 1 ||
    tag !== `v${manifest.version}` ||
    manifest.commit !== context.sha ||
    !buildRunId ||
    manifest.runId !== buildRunId
  )
    throw new Error(
      "Release manifest does not match this tag, commit, and source build run.",
    );
  if (!Array.isArray(manifest.files) || manifest.files.length !== 2)
    throw new Error(
      "Expected one portable ZIP and one installer EXE in the release manifest.",
    );
  const names = manifest.files.map((file) => safeName(file.name));
  if (
    new Set(names.map((name) => name.toLowerCase())).size !== 2 ||
    !names.some((name) => name.toLowerCase().endsWith(".zip")) ||
    !names.some((name) => name.toLowerCase().endsWith(".exe"))
  )
    throw new Error("Expected distinct ZIP and EXE release assets.");
  const assets = [];
  for (const file of manifest.files) {
    const asset = await read(file.name);
    if (
      !Number.isSafeInteger(file.size) ||
      asset.size !== file.size ||
      typeof file.sha256 !== "string" ||
      !/^[a-fA-F0-9]{64}$/.test(file.sha256) ||
      asset.digest !== `sha256:${file.sha256.toLowerCase()}`
    )
      throw new Error(
        `Release payload failed local verification: ${file.name}`,
      );
    assets.push(asset);
  }
  assets.push(manifestAsset, await read("SHA256SUMS.txt"));
  return { tag, assets };
}

async function listAll(method, parameters) {
  const items = [];
  for (let page = 1; ; page++) {
    const { data } = await method({ ...parameters, per_page: 100, page });
    items.push(...data);
    if (data.length < 100) return items;
  }
}

async function findRelease(repos, repository, tag) {
  try {
    return (await repos.getReleaseByTag({ ...repository, tag })).data;
  } catch (error) {
    if (error.status !== 404) throw error;
  }
  // Authenticated release listings include drafts even when lookup by tag does
  // not return them. Never create another draft just because that lookup is 404.
  const matching = (await listAll(repos.listReleases, repository)).filter(
    (release) => release.tag_name === tag,
  );
  if (matching.length > 1)
    throw new Error(
      `Several releases already use ${tag}; resolve the ambiguity before retrying.`,
    );
  return matching[0];
}

const matches = (remote, local) =>
  remote.name === local.name &&
  remote.size === local.size &&
  remote.state === "uploaded" &&
  typeof remote.digest === "string" &&
  remote.digest.toLowerCase() === local.digest;

function verifyAssets(remote, local) {
  if (
    remote.length !== local.length ||
    local.some((asset) => {
      const named = remote.filter((item) => item.name === asset.name);
      return named.length !== 1 || !matches(named[0], asset);
    })
  )
    throw new Error(
      "Release assets differ from the verified build (name, size, or SHA-256 digest).",
    );
}

module.exports = async function publishRelease({
  github,
  context,
  core,
  releaseDirectory = resolve(
    process.env.GITHUB_WORKSPACE || process.cwd(),
    "artifacts/release",
  ),
}) {
  const { tag, assets } = await localAssets(releaseDirectory, context);
  const repository = { owner: context.repo.owner, repo: context.repo.repo };
  const repos = github.rest.repos;
  let release = await findRelease(repos, repository, tag);
  if (!release) {
    release = (
      await repos.createRelease({
        ...repository,
        tag_name: tag,
        target_commitish: context.sha,
        name: tag,
        draft: true,
        prerelease: false,
        generate_release_notes: true,
      })
    ).data;
  }
  const parameters = { ...repository, release_id: release.id };
  let remote = await listAll(repos.listReleaseAssets, parameters);
  if (!release.draft) {
    verifyAssets(remote, assets);
    core.info(
      `Release ${tag} is already public and its four assets match. No changes made.`,
    );
    return release;
  }
  const expectedNames = new Set(assets.map((asset) => asset.name));
  if (remote.some((asset) => !expectedNames.has(asset.name)))
    throw new Error(
      "Draft contains unexpected assets; leaving it unchanged for review.",
    );

  const requireDraft = async () => {
    const current = (await repos.getRelease(parameters)).data;
    if (!current.draft)
      throw new Error(
        "Release became public while uploading; refusing to modify its assets.",
      );
  };
  for (const asset of assets) {
    const named = remote.filter((item) => item.name === asset.name);
    const retained = named.find((item) => matches(item, asset));
    // Only an unpublished draft may have incomplete or incorrect uploads
    // replaced. Upload errors leave the draft available for a safe retry.
    for (const item of named) {
      if (item === retained) continue;
      await requireDraft();
      await repos.deleteReleaseAsset({ ...repository, asset_id: item.id });
    }
    if (retained) continue;
    await requireDraft();
    const uploaded = (
      await repos.uploadReleaseAsset({
        ...parameters,
        name: asset.name,
        data: asset.data,
        headers: {
          "content-type": "application/octet-stream",
          "content-length": asset.size,
        },
      })
    ).data;
    if (!matches(uploaded, asset))
      throw new Error(`Uploaded asset did not verify: ${asset.name}`);
  }
  remote = await listAll(repos.listReleaseAssets, parameters);
  verifyAssets(remote, assets);
  await requireDraft();
  const published = (
    await repos.updateRelease({
      ...parameters,
      draft: false,
      prerelease: false,
      make_latest: "true",
    })
  ).data;
  core.info(`Published ${tag} with all four verified release assets.`);
  return published;
};
