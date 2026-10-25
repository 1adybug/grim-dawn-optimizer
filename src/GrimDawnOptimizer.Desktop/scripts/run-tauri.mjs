import { existsSync } from "node:fs";
import { createRequire } from "node:module";
import { homedir } from "node:os";
import { delimiter, join } from "node:path";
import { spawn } from "node:child_process";

// A newly installed rustup is available immediately, even when the terminal
// predates its PATH update. This only changes the child build environment.
const cargoBin = join(process.env.CARGO_HOME || join(homedir(), ".cargo"), "bin");
const environment = { ...process.env };
const pathKey = Object.keys(environment).find((key) => key.toLowerCase() === "path") || "PATH";
if (existsSync(join(cargoBin, process.platform === "win32" ? "cargo.exe" : "cargo"))) {
  environment[pathKey] = `${cargoBin}${delimiter}${environment[pathKey] || ""}`;
}
const require = createRequire(import.meta.url);
const cli = require.resolve("@tauri-apps/cli/tauri.js");
const child = spawn(process.execPath, [cli, ...process.argv.slice(2)], {
  env: environment,
  stdio: "inherit",
});
child.on("error", (error) => {
  console.error(`Unable to start Tauri: ${error.message}`);
  process.exitCode = 1;
});
child.on("exit", (code) => { process.exitCode = code ?? 1; });
