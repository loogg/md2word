import * as fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { validateTemplateCatalog } from "./private-template-library.mjs";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptsDirectory, "..");
const releaseRoot = path.resolve(projectRoot, "release");
const packageMetadata = JSON.parse(await fs.readFile(path.join(projectRoot, "package.json"), "utf8"));
const version = packageMetadata.version;
const architecture = "x64";
const baseName = `MD2Word-${version}-win-${architecture}`;
const artifactNames = [
  `${baseName}-portable.exe`,
  `${baseName}-portable.zip`,
  `${baseName}-setup.exe`,
];
const portableDirectoryName = `${baseName}-portable`;
const templatesDirectoryName = "templates";
const capabilityDocumentName = "MD2Word-支持能力说明.md";
const templateGuideName = "MD2Word-模板制作指南.md";
const deliveredNames = [portableDirectoryName, ...artifactNames, templatesDirectoryName, capabilityDocumentName, templateGuideName];
const allowedReleaseNames = new Set(deliveredNames);
const templatesStage = path.join(projectRoot, "templates", "local");

if (
  path.resolve(projectRoot, packageMetadata.build?.directories?.output ?? "") !== releaseRoot
  || path.dirname(releaseRoot) !== projectRoot
  || path.basename(releaseRoot) !== "release"
) {
  throw new Error("Refusing to finalize artifacts from unexpected directories.");
}

const releaseStat = await fs.lstat(releaseRoot);
const realProjectRoot = await fs.realpath(projectRoot);
const realReleaseRoot = await fs.realpath(releaseRoot);
if (
  !releaseStat.isDirectory()
  || releaseStat.isSymbolicLink()
  || realReleaseRoot !== path.join(realProjectRoot, "release")
) {
  throw new Error("Refusing to clean a linked or unexpected release directory.");
}

async function pathExists(filePath) {
  try {
    await fs.lstat(filePath);
    return true;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

async function validatePortableDirectoryLocation(directoryPath) {
  const stat = await fs.lstat(directoryPath);
  const realDirectoryPath = await fs.realpath(directoryPath);
  if (
    !stat.isDirectory()
    || stat.isSymbolicLink()
    || path.dirname(realDirectoryPath) !== realReleaseRoot
    || path.basename(realDirectoryPath) !== path.basename(directoryPath)
  ) {
    throw new Error(`Portable directory is linked or unexpected: ${directoryPath}`);
  }
}

async function validatePortableDirectory(directoryPath) {
  await validatePortableDirectoryLocation(directoryPath);

  for (const relativePath of [
    "MD2Word.exe",
    "MD2Word-支持能力说明.md",
    "MD2Word-模板制作指南.md",
    "resources/app.asar",
    "resources/worker/Md2Word.Worker.exe",
    "resources/conversion/capabilities.json",
    "resources/conversion/worddom-template.html",
    "resources/conversion/filters/manifest.json",
  ]) {
    const requiredPath = path.join(directoryPath, relativePath);
    const requiredStat = await fs.stat(requiredPath);
    if (!requiredStat.isFile() || requiredStat.size <= 0) {
      throw new Error(`Portable directory is missing a required file: ${relativePath}`);
    }
  }
  await validateTemplateCatalog(path.join(directoryPath, templatesDirectoryName));
}

const unpackedDirectory = path.join(releaseRoot, "win-unpacked");
const portableDirectory = path.join(releaseRoot, portableDirectoryName);
if (await pathExists(unpackedDirectory)) {
  await validatePortableDirectory(unpackedDirectory);
  if (await pathExists(portableDirectory)) {
    await validatePortableDirectoryLocation(portableDirectory);
    await fs.rm(portableDirectory, { recursive: true, force: true });
  }
  await fs.rename(unpackedDirectory, portableDirectory);
}
await validatePortableDirectory(portableDirectory);

await validateTemplateCatalog(templatesStage);
const deliveredTemplates = path.join(releaseRoot, templatesDirectoryName);
if (await pathExists(deliveredTemplates)) {
  const deliveredTemplatesStat = await fs.lstat(deliveredTemplates);
  const deliveredTemplatesRealPath = await fs.realpath(deliveredTemplates);
  if (
    !deliveredTemplatesStat.isDirectory()
    || deliveredTemplatesStat.isSymbolicLink()
    || path.dirname(deliveredTemplatesRealPath) !== realReleaseRoot
    || path.basename(deliveredTemplatesRealPath) !== templatesDirectoryName
  ) {
    throw new Error("Refusing to replace a linked or unexpected delivered template directory.");
  }
  await fs.rm(deliveredTemplates, { recursive: true, force: true });
}
await fs.cp(templatesStage, deliveredTemplates, { recursive: true, force: false, errorOnExist: true });
await validateTemplateCatalog(deliveredTemplates);

const capabilityDocumentSource = path.join(projectRoot, "resources", "docs", capabilityDocumentName);
await fs.copyFile(path.join(projectRoot, "resources", "docs", templateGuideName), path.join(releaseRoot, templateGuideName));
const deliveredCapabilityDocument = path.join(releaseRoot, capabilityDocumentName);
await fs.copyFile(capabilityDocumentSource, deliveredCapabilityDocument);
const capabilityDocumentStat = await fs.stat(deliveredCapabilityDocument);
if (!capabilityDocumentStat.isFile() || capabilityDocumentStat.size <= 0) {
  throw new Error("The delivered capability document is empty or missing.");
}

for (const artifactName of artifactNames) {
  const artifactPath = path.join(releaseRoot, artifactName);
  const stat = await fs.stat(artifactPath);
  if (!stat.isFile() || stat.size <= 0) {
    throw new Error(`Windows artifact is empty or missing: ${artifactName}`);
  }
}

for (const entry of await fs.readdir(releaseRoot, { withFileTypes: true })) {
  if (!allowedReleaseNames.has(entry.name)) {
    await fs.rm(path.join(releaseRoot, entry.name), { recursive: true, force: true });
  }
}

const finalNames = (await fs.readdir(releaseRoot)).sort();
const expectedNames = [...allowedReleaseNames].sort();
if (JSON.stringify(finalNames) !== JSON.stringify(expectedNames)) {
  throw new Error(`Unexpected files remain in release: ${finalNames.join(", ")}`);
}

process.stdout.write(`Windows artifacts ready:\n${deliveredNames.map((name) => `- ${name}`).join("\n")}\n`);
