import { spawn } from "node:child_process";
import * as fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { stagePublicTemplateCatalog } from "./public-reference-library.mjs";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptsDirectory, "..");
const stageRoot = path.join(projectRoot, "templates", "local");
const publicTemplateSourceRoot = path.join(projectRoot, "resources", "templates");
const workerPath = path.join(projectRoot, "worker", "publish", "win-x64", "Md2Word.Worker.exe");
const packageMetadata = JSON.parse(await fs.readFile(path.join(projectRoot, "package.json"), "utf8"));
const npmCliPath = process.env.npm_execpath && path.isAbsolute(process.env.npm_execpath)
  ? path.resolve(process.env.npm_execpath)
  : undefined;

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: projectRoot,
      env: process.env,
      shell: false,
      stdio: "inherit",
      windowsHide: false,
    });
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0) resolve();
      else reject(new Error(`${command} failed (${signal ?? `exit ${code}`}).`));
    });
  });
}

async function pathExists(target) {
  try {
    await fs.lstat(target);
    return true;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

async function cleanupLegacyReleaseLayout() {
  const releaseRoot = path.join(projectRoot, "release");
  if (!(await pathExists(releaseRoot))) return;
  const releaseStat = await fs.lstat(releaseRoot);
  if (!releaseStat.isDirectory() || releaseStat.isSymbolicLink() || path.dirname(releaseRoot) !== projectRoot) {
    throw new Error("Refusing to migrate an unexpected or linked release directory.");
  }
  const legacyBaseName = `MD2Word-${packageMetadata.version}-win-x64`;
  for (const name of [
    `${legacyBaseName}-portable`,
    `${legacyBaseName}-portable.exe`,
    `${legacyBaseName}-portable.zip`,
    `${legacyBaseName}-SHA256SUMS.txt`,
    "templates",
    "public",
  ]) {
    const target = path.join(releaseRoot, name);
    if (!(await pathExists(target))) continue;
    const targetStat = await fs.lstat(target);
    if (targetStat.isSymbolicLink()) throw new Error(`Refusing to remove a linked legacy release entry: ${name}`);
    try {
      await fs.rm(target, { recursive: targetStat.isDirectory(), force: true });
    } catch (error) {
      if (name !== "templates" || !targetStat.isDirectory() || !["EBUSY", "EPERM"].includes(error?.code)) throw error;
      for (const entry of await fs.readdir(target)) {
        await fs.rm(path.join(target, entry), { recursive: true, force: true });
      }
      process.stdout.write("Legacy root template directory is locked; its private contents were cleared. Close Explorer to remove the empty directory.\n");
    }
  }
}

async function packageWindows() {
  try {
    if (!npmCliPath) throw new Error("Run Windows packaging through npm run package:win.");
    await cleanupLegacyReleaseLayout();
    await run(process.execPath, [npmCliPath, "run", "build:desktop"]);
    await stagePublicTemplateCatalog({
      projectRoot,
      stageRoot,
      sourceRoot: publicTemplateSourceRoot,
      workerPath,
    });
    process.stdout.write("Tracked public template packages Worker-validated and staged under templates/.\n");
    const artifactName = "MD2Word-${version}-win-${arch}-portable.${ext}";
    await run(process.execPath, [
      npmCliPath,
      "exec",
      "--",
      "electron-builder",
      "--win",
      "portable",
      "zip",
      "--x64",
      "--config.directories.output=release",
      `--config.win.artifactName=${artifactName}`,
      `--config.portable.artifactName=${artifactName}`,
    ]);
    await run(process.execPath, [path.join(scriptsDirectory, "finalize-windows-artifacts.mjs")]);
  } finally {
    await fs.rm(stageRoot, { recursive: true, force: true });
  }
}

try {
  await packageWindows();
} catch (error) {
  const message = error instanceof Error ? error.message : "Unknown packaging error.";
  process.stderr.write(`Windows packaging failed: ${message}\n`);
  process.exitCode = 1;
}
