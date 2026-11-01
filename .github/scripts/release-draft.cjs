const assert = require("node:assert/strict");

const marker = "<!-- grim-dawn-build";
const payloadNames = (version) => [
  `GrimDawnOptimizer-${version}-win-x64.exe`,
  `GrimDawnOptimizer-${version}-win-x64-setup.exe`,
  `GrimDawnOptimizer-${version}-win-x64-setup.exe.sig`,
  "latest.json",
];

function validateManifest(manifest) {
  assert.ok(
    manifest && typeof manifest === "object",
    "Missing release build manifest",
  );
  assert.deepEqual(
    Object.keys(manifest).sort(),
    ["commit", "files", "runId", "schemaVersion", "version"],
    "Unexpected release manifest fields",
  );
  assert.equal(
    manifest.schemaVersion,
    2,
    "Unsupported release manifest schema",
  );
  assert.match(
    manifest.version ?? "",
    /^\d+\.\d+\.\d+$/,
    "Invalid release version",
  );
  assert.match(
    manifest.commit ?? "",
    /^[a-f0-9]{40}$/,
    "Invalid release build commit",
  );
  assert.match(
    manifest.runId ?? "",
    /^[1-9]\d*$/,
    "Invalid source build run ID",
  );
  assert.ok(
    Array.isArray(manifest.files) && manifest.files.length === 4,
    "Expected portable EXE, installer, signature and update metadata",
  );
  assert.deepEqual(
    manifest.files.map((file) => file.name),
    payloadNames(manifest.version),
    "Unexpected release payloads",
  );
  for (const file of manifest.files) {
    assert.ok(file && typeof file === "object", "Missing release payload");
    assert.deepEqual(
      Object.keys(file).sort(),
      ["name", "sha256", "size"],
      "Unexpected release payload fields",
    );
    assert.ok(
      Number.isSafeInteger(file.size) && file.size > 0,
      "Invalid release payload size",
    );
    assert.match(
      file.sha256 ?? "",
      /^[a-f0-9]{64}$/,
      "Invalid release payload SHA-256",
    );
  }
  return manifest;
}

function formatDraftBody(manifest) {
  return `${marker}\n${JSON.stringify(validateManifest(manifest))}\n-->`;
}

function parseDraftBody(body) {
  assert.equal(typeof body, "string", "Missing release build manifest body");
  assert.equal(
    body.split(marker).length - 1,
    1,
    "Expected exactly one release build manifest marker",
  );
  const matches = [
    ...body.matchAll(/<!-- grim-dawn-build\r?\n([\s\S]*?)\r?\n-->/g),
  ];
  assert.equal(matches.length, 1, "Malformed release build manifest marker");
  return validateManifest(JSON.parse(matches[0][1]));
}

module.exports = {
  formatDraftBody,
  parseDraftBody,
  validateManifest,
  payloadNames,
};
